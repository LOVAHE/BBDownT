using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Web;
using BBDownT.Core.Util;

namespace BBDownT;

internal sealed record IntlQrCode(Uri Url, string Ticket);
internal enum IntlQrStatus { Waiting, Scanned, Expired, Success }
internal sealed record IntlQrPollResult(IntlQrStatus Status, Uri? GoUrl = null);

internal sealed class IntlLoginClient : IDisposable
{
    private static readonly Uri Passport = new("https://passport.bilibili.tv/");
    private static readonly Uri Api = new("https://api.bilibili.tv/");
    private const string Query = "?sLocale=en_US&platform=web";
    private const string LoginFailure = "国际站登录请求失败，请重试或手动导入Cookie";
    private const string InvalidAddress = "国际站登录返回了不受信任的地址";
    private readonly HttpClient client;
    private readonly CookieContainer cookies = new();

    internal IntlLoginClient(HttpMessageHandler? handler = null, bool disposeHandler = true)
    {
        handler ??= new HttpClientHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            AutomaticDecompression = DecompressionMethods.All
        };
        client = new HttpClient(handler, disposeHandler) { Timeout = TimeSpan.FromSeconds(20) };
    }

    internal async Task<IntlQrCode> GenerateAsync(CancellationToken cancellationToken = default)
    {
        var root = await GetJsonAsync(Route("qrcode/auth/url"), cancellationToken);
        if (ReadCode(root) != 0 || !root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object
            || !data.TryGetProperty("qr_url", out var url) || url.ValueKind != JsonValueKind.String)
            throw new InvalidOperationException(LoginFailure);
        var qrUrl = ResolveTrusted(url.GetString()!, Passport);
        var ticket = HttpUtility.ParseQueryString(qrUrl.Query)["ticket"];
        if (string.IsNullOrWhiteSpace(ticket)) throw new InvalidOperationException(LoginFailure);
        return new IntlQrCode(qrUrl, ticket);
    }

    internal async Task<IntlQrPollResult> PollAsync(string ticket, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(ticket)) throw new InvalidOperationException(LoginFailure);
        var root = await GetJsonAsync(new Uri(Route("qrcode/auth/fetch").AbsoluteUri
            + "&ticket=" + Uri.EscapeDataString(ticket)), cancellationToken);
        var code = ReadCode(root);
        return code switch
        {
            10018101 => new(IntlQrStatus.Waiting),
            10018102 => new(IntlQrStatus.Scanned),
            10018100 => new(IntlQrStatus.Expired),
            0 => new(IntlQrStatus.Success, ReadGoUrl(root)),
            _ => throw new InvalidOperationException(LoginFailure)
        };
    }

    internal async Task<string> CompleteAsync(Uri? goUrl, CancellationToken cancellationToken = default)
    {
        if (goUrl is not null) ValidateTrusted(goUrl);
        var root = await GetJsonAsync(Route("web/sso/list"), cancellationToken);
        var followedGoUrl = false;
        if (ReadCode(root) == -101 && goUrl is not null)
        {
            await FollowAsync(goUrl, cancellationToken);
            followedGoUrl = true;
            root = await GetJsonAsync(Route("web/sso/list"), cancellationToken);
        }
        // Finish the official SSO flow, then verify the API's login state.
        // Cookie names alone cannot establish an international login.
        if (ReadCode(root) != 0 || !root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object
            || !data.TryGetProperty("sso", out var sso) || sso.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("国际站尚未确认登录，请重试或手动导入Cookie");
        var targets = new List<Uri>();
        foreach (var target in sso.EnumerateArray())
        {
            if (target.ValueKind != JsonValueKind.String) throw new InvalidOperationException(LoginFailure);
            targets.Add(ResolveTrusted(target.GetString()!, Passport));
        }
        // Validate every listed target before following any of them.
        foreach (var target in targets) await FollowAsync(target, cancellationToken);
        if (!followedGoUrl && goUrl is not null) await FollowAsync(goUrl, cancellationToken);

        var user = await GetJsonAsync(new Uri(Api,
            "intl/gateway/web/v2/user?s_locale=en_US&platform=web"), cancellationToken);
        if (ReadCode(user) != 0 || !user.TryGetProperty("data", out var userData)
            || userData.ValueKind != JsonValueKind.Object || !userData.TryGetProperty("is_login", out var isLogin)
            || isLogin.ValueKind != JsonValueKind.True)
            throw new InvalidOperationException("国际站尚未确认登录，请重试或手动导入Cookie");
        var apiCookieHeader = cookies.GetCookieHeader(Api);
        if (apiCookieHeader.Length == 0)
            throw new InvalidOperationException("国际站未返回可用登录Cookie，未覆盖现有登录文件");
        return IntlCookieStore.Normalize(apiCookieHeader);
    }

    private static Uri Route(string route) => new(Passport,
        "x/intl/passport-login/" + route + Query);

    private static int ReadCode(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("code", out var code)
            || !int.TryParse(code.ToString(), out var value))
            throw new InvalidOperationException(LoginFailure);
        return value;
    }

    private static Uri? ReadGoUrl(JsonElement root)
    {
        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException(LoginFailure);
        if (!data.TryGetProperty("go_url", out var url) || url.ValueKind == JsonValueKind.Null) return null;
        if (url.ValueKind != JsonValueKind.String) throw new InvalidOperationException(LoginFailure);
        return string.IsNullOrWhiteSpace(url.GetString()) ? null : ResolveTrusted(url.GetString()!, Passport);
    }

    private async Task<JsonElement> GetJsonAsync(Uri uri, CancellationToken cancellationToken)
    {
        // HttpClient's timeout ends at headers with ResponseHeadersRead. Keep
        // the same deadline active while reading the JSON response body.
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        using var response = await SendAsync(uri, timeout.Token);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException(LoginFailure);
        try
        {
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(timeout.Token));
            return document.RootElement.Clone();
        }
        catch (JsonException) { throw new InvalidOperationException(LoginFailure); }
        catch (OperationCanceledException) { throw new InvalidOperationException("国际站登录请求超时或已取消"); }
        catch (HttpRequestException) { throw new InvalidOperationException(LoginFailure); }
    }

    private async Task FollowAsync(Uri uri, CancellationToken cancellationToken)
    {
        for (var redirects = 0; ; redirects++)
        {
            using var response = await SendAsync(uri, cancellationToken);
            if ((int)response.StatusCode is 301 or 302 or 303 or 307 or 308)
            {
                if (redirects >= 8 || response.Headers.Location is not { } location)
                    throw new InvalidOperationException("国际站登录重定向次数过多或响应无效");
                uri = ResolveTrusted(location.OriginalString, uri);
                continue;
            }
            if (!response.IsSuccessStatusCode) throw new InvalidOperationException(LoginFailure);
            return;
        }
    }

    private async Task<HttpResponseMessage> SendAsync(Uri uri, CancellationToken cancellationToken)
    {
        ValidateTrusted(uri);
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        HTTPUtil.ApplyWebRequestHeaders(request, uri.AbsoluteUri, sendCookie: false, forceAuthenticatedProfile: true);
        request.Headers.Referrer = new Uri("https://www.bilibili.tv/");
        request.Headers.TryAddWithoutValidation("Origin", "https://www.bilibili.tv");
        request.Headers.Remove("Sec-Fetch-Site");
        request.Headers.TryAddWithoutValidation("Sec-Fetch-Site",
            TrustedHost(uri.IdnHost, "bilibili.tv") ? "same-site" : "cross-site");
        var cookie = cookies.GetCookieHeader(uri);
        if (cookie.Length != 0) request.Headers.TryAddWithoutValidation("Cookie", cookie);
        HttpResponseMessage? response = null;
        try
        {
            response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (response.Headers.TryGetValues("Set-Cookie", out var values))
            {
                foreach (var value in values)
                {
                    ValidateCookieDomain(value);
                    cookies.SetCookies(uri, value);
                }
            }
            return response;
        }
        catch (CookieException)
        {
            response?.Dispose();
            throw new InvalidOperationException("国际站登录Cookie响应无效");
        }
        catch (HttpRequestException)
        {
            response?.Dispose();
            throw new InvalidOperationException(LoginFailure);
        }
        catch (OperationCanceledException)
        {
            response?.Dispose();
            throw new InvalidOperationException("国际站登录请求超时或已取消");
        }
    }

    private static Uri ResolveTrusted(string value, Uri baseUri)
    {
        if (!Uri.TryCreate(baseUri, value, out var uri)) throw new InvalidOperationException(InvalidAddress);
        ValidateTrusted(uri);
        return uri;
    }

    private static void ValidateTrusted(Uri uri)
    {
        var host = uri.IsAbsoluteUri ? uri.IdnHost : "";
        if (!uri.IsAbsoluteUri || uri.Scheme != Uri.UriSchemeHttps || uri.Port != 443
            || uri.UserInfo.Length != 0 || !(TrustedHost(host, "bilibili.tv") || TrustedHost(host, "biliintl.com")))
            throw new InvalidOperationException(InvalidAddress);
    }

    private static bool TrustedHost(string host, string domain) => host.Equals(domain, StringComparison.OrdinalIgnoreCase)
        || host.EndsWith("." + domain, StringComparison.OrdinalIgnoreCase);

    private static void ValidateCookieDomain(string header)
    {
        var attributes = header.Split(';');
        for (var index = 1; index < attributes.Length; index++)
        {
            var parts = attributes[index].Split('=', 2);
            if (parts.Length != 2 || !parts[0].Trim().Equals("Domain", StringComparison.OrdinalIgnoreCase)) continue;
            var domain = parts[1].Trim().Trim('"').TrimStart('.');
            if (!TrustedHost(domain, "bilibili.tv") && !TrustedHost(domain, "biliintl.com"))
                throw new CookieException();
        }
    }

    public void Dispose() => client.Dispose();
}
