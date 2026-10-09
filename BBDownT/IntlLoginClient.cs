using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Web;
using BBDownT.Core.Util;
using BBDownT.Core;

namespace BBDownT;

internal sealed record IntlQrCode(Uri Url, string Ticket);
internal sealed record IntlQrPollResult(QrLoginStatus Status, Uri? GoUrl = null);
internal enum IntlLoginStage { Initialize, Import, Generate, Poll, Sync, Verify, Save }
internal sealed class IntlLoginTransportException(string message) : InvalidOperationException(message) { }

internal sealed class IntlLoginClient : IDisposable
{
    private static readonly Uri Passport = new("https://passport.bilibili.tv/");
    private static readonly Uri Api = new("https://api.bilibili.tv/");
    private static readonly Uri UserApi = new(Api, "intl/gateway/web/v2/user?s_locale=en_US&platform=web");
    private static readonly Uri PlaybackApi = new(Api, "intl/gateway/web/playurl");
    private const string Query = "?sLocale=en_US&platform=web";
    private const string LoginFailure = "国际站登录请求失败，请重试或手动导入Cookie";
    private const string InvalidAddress = "国际站登录返回了不受信任的地址";
    private readonly HttpClient client;
    private readonly CookieContainer cookies = new();
    private readonly Action<string> diagnostic;
    internal IntlLoginStage CurrentStage { get; private set; }

    internal IntlLoginClient(HttpMessageHandler? handler = null, bool disposeHandler = true,
        Action<string>? diagnostic = null)
    {
        this.diagnostic = diagnostic ?? (message => Logger.LogDebug("{0}", message));
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
        EnterStage(IntlLoginStage.Generate);
        try
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
        catch (Exception error)
        {
            ReportFailure(error);
            throw;
        }
    }

    internal async Task<IntlQrPollResult> PollAsync(string ticket, CancellationToken cancellationToken = default)
    {
        EnterStage(IntlLoginStage.Poll);
        try
        {
            if (string.IsNullOrWhiteSpace(ticket)) throw new InvalidOperationException(LoginFailure);
            var root = await GetJsonAsync(new Uri(Route("qrcode/auth/fetch").AbsoluteUri
                + "&ticket=" + Uri.EscapeDataString(ticket)), cancellationToken);
            var code = ReadCode(root);
            return code switch
            {
                10018101 => new(QrLoginStatus.Waiting),
                10018102 => new(QrLoginStatus.Scanned),
                10018100 => new(QrLoginStatus.Expired),
                0 => new(QrLoginStatus.Success, ReadGoUrl(root)),
                _ => throw new InvalidOperationException(LoginFailure)
            };
        }
        catch (Exception error)
        {
            ReportFailure(error);
            throw;
        }
    }

    internal async Task<string> CompleteAsync(Uri? goUrl, CancellationToken cancellationToken = default)
    {
        EnterStage(IntlLoginStage.Sync);
        try
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
            if (ReadCode(root) != 0)
                throw new InvalidOperationException("国际站尚未确认登录，请重试或手动导入Cookie");
            foreach (var target in ReadSsoTargets(root)) await TryFollowAsync(target, cancellationToken);
            // A return-page navigation is unnecessary once the API confirms login.
            // If SSO alone has not established it, try that trusted fallback once.
            var verifiedCookie = await GetVerifiedCookieAsync(cancellationToken);
            if (verifiedCookie is null && !followedGoUrl && goUrl is not null)
            {
                EnterStage(IntlLoginStage.Sync);
                await TryFollowAsync(goUrl, cancellationToken);
                verifiedCookie = await GetVerifiedCookieAsync(cancellationToken);
            }
            if (verifiedCookie is null)
                throw new InvalidOperationException("国际站尚未确认登录，请重试或手动导入Cookie");
            return verifiedCookie;
        }
        catch (Exception error)
        {
            ReportFailure(error);
            throw;
        }
    }

    private List<Uri> ReadSsoTargets(JsonElement root)
    {
        var targets = new List<Uri>();
        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object
            || !data.TryGetProperty("sso", out var sso) || sso.ValueKind != JsonValueKind.Array)
        {
            diagnostic($"国际站登录 stage={CurrentStage} sso_list=missing");
            return targets;
        }
        var skipped = new List<string>();
        foreach (var target in sso.EnumerateArray())
        {
            if (target.ValueKind == JsonValueKind.String && Uri.TryCreate(Passport, target.GetString(), out var uri))
            {
                if (IsTrusted(uri)) targets.Add(uri);
                else skipped.Add(uri.IsAbsoluteUri ? uri.IdnHost : "invalid");
            }
            else skipped.Add("invalid");
        }
        diagnostic($"国际站登录 stage={CurrentStage} sso_targets={targets.Count} sso_skipped={skipped.Count}"
            + (skipped.Count > 0 ? $" skipped_hosts={string.Join(",", skipped.Distinct())}" : ""));
        return targets;
    }

    private async Task<string?> GetVerifiedCookieAsync(CancellationToken cancellationToken)
    {
        EnterStage(IntlLoginStage.Verify);
        for (var attempt = 0; attempt < 2; attempt++)
        {
            // Verify exactly the header that can authenticate media requests,
            // rather than /user-only or /v2 cookies from the complete jar.
            var candidate = cookies.GetCookieHeader(PlaybackApi);
            var user = await GetJsonAsync(UserApi, cancellationToken, candidate);
            var loggedIn = ReadCode(user) == 0 && user.TryGetProperty("data", out var data)
                && data.ValueKind == JsonValueKind.Object && data.TryGetProperty("is_login", out var value)
                && value.ValueKind == JsonValueKind.True;
            diagnostic($"国际站登录 stage={CurrentStage} is_login={loggedIn}");
            if (!loggedIn || candidate.Length == 0) return null;
            if (cookies.GetCookieHeader(PlaybackApi) == candidate) return IntlCookieStore.Normalize(candidate);
            // A verification response may rotate credentials. Verify the new
            // candidate before saving it, with a bounded retry for that change.
            diagnostic($"国际站登录 stage={CurrentStage} cookie_changed=reverify");
        }
        throw new InvalidOperationException("国际站登录Cookie验证未稳定，未覆盖现有登录文件，请重试");
    }

    private async Task TryFollowAsync(Uri uri, CancellationToken cancellationToken)
    {
        try { await FollowAsync(uri, cancellationToken); }
        catch (IntlLoginTransportException) when (!cancellationToken.IsCancellationRequested)
        {
            // Like the WEB allSettled flow, a failed trusted endpoint does not
            // veto another successful SSO target. Security failures still abort.
            diagnostic($"国际站登录 stage={CurrentStage} transport_failure=continue");
        }
    }

    private void EnterStage(IntlLoginStage stage)
    {
        CurrentStage = stage;
        diagnostic($"国际站登录 stage={stage} begin");
    }

    private void ReportFailure(Exception error)
        => diagnostic($"国际站登录 stage={CurrentStage} exception={ExceptionCategory(error)}");

    internal static string ExceptionCategory(Exception error) => error switch
    {
        IntlLoginTransportException => "TransportFailure",
        TaskCanceledException => "TaskCanceledException",
        OperationCanceledException => "OperationCanceledException",
        HttpRequestException => "HttpRequestException",
        UnauthorizedAccessException => "UnauthorizedAccessException",
        IOException => "IOException",
        CookieException => "CookieException",
        JsonException => "JsonException",
        ArgumentException => "ArgumentException",
        InvalidOperationException => "InvalidOperationException",
        _ => "Exception"
    };

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

    private async Task<JsonElement> GetJsonAsync(Uri uri, CancellationToken cancellationToken,
        string? cookieOverride = null)
    {
        // HttpClient's timeout ends at headers with ResponseHeadersRead. Keep
        // the same deadline active while reading the JSON response body.
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        using var response = await SendAsync(uri, timeout.Token, cookieOverride);
        if (!response.IsSuccessStatusCode) throw new IntlLoginTransportException(LoginFailure);
        try
        {
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(timeout.Token));
            diagnostic($"国际站登录 stage={CurrentStage} API={ReadCode(document.RootElement)}");
            return document.RootElement.Clone();
        }
        catch (JsonException error) { ReportFailure(error); throw new InvalidOperationException(LoginFailure); }
        catch (OperationCanceledException error)
        {
            ReportFailure(error);
            throw new IntlLoginTransportException("国际站登录请求超时或已取消");
        }
        catch (HttpRequestException error) { ReportFailure(error); throw new IntlLoginTransportException(LoginFailure); }
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
            if (!response.IsSuccessStatusCode) throw new IntlLoginTransportException(LoginFailure);
            return;
        }
    }

    private async Task<HttpResponseMessage> SendAsync(Uri uri, CancellationToken cancellationToken,
        string? cookieOverride = null)
    {
        ValidateTrusted(uri);
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        HTTPUtil.ApplyWebRequestHeaders(request, uri.AbsoluteUri, sendCookie: false, forceAuthenticatedProfile: true);
        request.Headers.Referrer = new Uri("https://www.bilibili.tv/");
        request.Headers.TryAddWithoutValidation("Origin", "https://www.bilibili.tv");
        request.Headers.Remove("Sec-Fetch-Site");
        request.Headers.TryAddWithoutValidation("Sec-Fetch-Site",
            TrustedHost(uri.IdnHost, "bilibili.tv") ? "same-site" : "cross-site");
        var cookie = cookieOverride ?? cookies.GetCookieHeader(uri);
        if (cookie.Length != 0) request.Headers.TryAddWithoutValidation("Cookie", cookie);
        HttpResponseMessage? response = null;
        try
        {
            response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            diagnostic($"国际站登录 stage={CurrentStage} HTTP={(int)response.StatusCode}");
            if (response.Headers.TryGetValues("Set-Cookie", out var values))
                CollectCookies(uri, values);
            return response;
        }
        catch (HttpRequestException error)
        {
            response?.Dispose();
            ReportFailure(error);
            throw new IntlLoginTransportException(LoginFailure);
        }
        catch (OperationCanceledException error)
        {
            response?.Dispose();
            ReportFailure(error);
            throw new IntlLoginTransportException("国际站登录请求超时或已取消");
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
        if (!IsTrusted(uri)) throw new InvalidOperationException(InvalidAddress);
    }

    private static bool IsTrusted(Uri uri)
    {
        var host = uri.IsAbsoluteUri ? uri.IdnHost : "";
        return uri.IsAbsoluteUri && uri.Scheme == Uri.UriSchemeHttps && uri.Port == 443
            && uri.UserInfo.Length == 0 && (TrustedHost(host, "bilibili.tv") || TrustedHost(host, "biliintl.com"));
    }

    private static bool TrustedHost(string host, string domain) => host.Equals(domain, StringComparison.OrdinalIgnoreCase)
        || host.EndsWith("." + domain, StringComparison.OrdinalIgnoreCase);

    private void CollectCookies(Uri uri, IEnumerable<string> headers)
    {
        var accepted = 0;
        var domainRejected = 0;
        var invalid = 0;
        foreach (var header in headers)
        foreach (var value in SplitCookieHeader(header))
        {
            if (!HasValidCookieName(value)) { invalid++; continue; }
            // Browsers ignore an unusable Set-Cookie independently. Never rebase
            // a sibling/cross-domain cookie onto its requested domain to accept it.
            if (!CookieDomainMatches(uri, value)) { domainRejected++; continue; }
            try
            {
                // Parse in an isolated jar first, so a malformed header cannot
                // partially mutate the live session. Reapply unchanged to retain
                // implicit host domains, paths, secure flags and expiry deletions.
                new CookieContainer().SetCookies(uri, value);
                cookies.SetCookies(uri, value);
                accepted++;
            }
            catch (CookieException) { invalid++; }
        }
        diagnostic($"国际站登录 stage={CurrentStage} cookies_accepted={accepted} cookies_domain_rejected={domainRejected} cookies_invalid={invalid}");
    }

    private static bool CookieDomainMatches(Uri uri, string header)
    {
        var hasDomain = false;
        var attributes = header.Split(';');
        for (var index = 1; index < attributes.Length; index++)
        {
            var parts = attributes[index].Split('=', 2);
            if (parts.Length != 2 || !parts[0].Trim().Equals("Domain", StringComparison.OrdinalIgnoreCase)) continue;
            if (hasDomain) return false;
            hasDomain = true;
            var domain = parts[1].Trim().Trim('"').TrimStart('.');
            if (domain.Length == 0 || domain.EndsWith('.')
                || !(TrustedHost(domain, "bilibili.tv") || TrustedHost(domain, "biliintl.com"))
                || !TrustedHost(uri.IdnHost, domain)) return false;
        }
        return true;
    }

    private static IEnumerable<string> SplitCookieHeader(string header)
    {
        // Some servers combine Set-Cookie lines. Only a comma followed by a
        // cookie-name '=' starts another cookie; the comma in Expires does not.
        var start = 0;
        var quoted = false;
        for (var index = 0; index < header.Length; index++)
        {
            if (header[index] == '"') quoted = !quoted;
            if (quoted || header[index] != ',') continue;
            var next = index + 1;
            while (next < header.Length && char.IsWhiteSpace(header[next])) next++;
            var nameStart = next;
            while (next < header.Length && IsCookieNameCharacter(header[next])) next++;
            if (next == nameStart || next >= header.Length || header[next] != '=') continue;
            yield return header[start..index].Trim();
            start = index + 1;
        }
        yield return header[start..].Trim();
    }

    private static bool IsCookieNameCharacter(char value) => char.IsAsciiLetterOrDigit(value)
        || "!#$%&'*+-.^_`|~".Contains(value);

    private static bool HasValidCookieName(string header)
    {
        var end = header.IndexOf(';');
        var pair = end < 0 ? header : header[..end];
        var equals = pair.IndexOf('=');
        if (equals < 1) return false;
        for (var index = 0; index < equals; index++)
            if (!IsCookieNameCharacter(pair[index])) return false;
        return true;
    }

    public void Dispose() => client.Dispose();
}
