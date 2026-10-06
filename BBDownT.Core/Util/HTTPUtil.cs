using System.Net;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Security.Authentication;
using static BBDownT.Core.Logger;

namespace BBDownT.Core.Util;

public static class HTTPUtil
{
    public static readonly HttpClient AppHttpClient = CreateClient(useCookies: true, allowRedirects: true);
    internal static readonly HttpClient IntlApiHttpClient = CreateClient(useCookies: false, allowRedirects: false);
    internal static readonly HttpClient IntlMediaHttpClient = CreateClient(useCookies: false, allowRedirects: true);

    private static HttpClient CreateClient(bool useCookies, bool allowRedirects)
        => new(CreateWebHandler(useCookies, allowRedirects)) { Timeout = TimeSpan.FromMinutes(2) };

    internal static HttpClientHandler CreateWebHandler(bool useCookies, bool allowRedirects) => new()
    {
        AllowAutoRedirect = allowRedirects,
        UseCookies = useCookies,
        AutomaticDecompression = DecompressionMethods.All,
        MaxConnectionsPerServer = 2048,
        ServerCertificateCustomValidationCallback = (_, _, _, sslPolicyErrors) =>
            Config.ALLOW_INSECURE_TLS || sslPolicyErrors == SslPolicyErrors.None
    };

    internal static HttpClient GetWebHttpClient(bool international) => international ? IntlApiHttpClient : AppHttpClient;
    internal static HttpClient GetMediaHttpClient(bool international) => international ? IntlMediaHttpClient : AppHttpClient;

    private static readonly object UserAgentLock = new();
    private static readonly string[] AndroidDevices =
        ["Pixel 4", "Pixel 5", "Pixel 6", "Pixel 7", "Pixel 8", "Pixel 9", "SM-S9080", "SM-S9180",
         "SM-S9280", "SM-S9380", "M2012K11AC", "2210132C", "23127PN0CC", "24031PN0DC", "V2307A", "V2408A"];
    private static readonly string[] CurlUserAgents =
        ["curl/8.10.1", "curl/8.11.1", "curl/8.12.1", "curl/8.13.0", "curl/8.14.1", "curl/8.15.0", "curl/8.16.0"];
    private static string userAgent = GenerateDefaultUserAgent(Random.Shared);
    private static bool automaticUserAgent = true;
    private static BrowserRequestProfile authenticatedBrowserProfile = BrowserRequestProfile.Create(Random.Shared);
    private static Action<BrowserRequestProfile>? persistAuthenticatedBrowserProfile;

    public static string UserAgent
    {
        get
        {
            lock (UserAgentLock) return userAgent;
        }
        set
        {
            lock (UserAgentLock)
            {
                userAgent = value;
                automaticUserAgent = false;
            }
        }
    }

    internal static bool IsAutomaticUserAgent
    {
        get
        {
            lock (UserAgentLock) return automaticUserAgent;
        }
    }

    internal static BrowserRequestProfile AuthenticatedBrowserProfile
    {
        get
        {
            lock (UserAgentLock) return authenticatedBrowserProfile;
        }
    }

    internal static void ConfigureAuthenticatedBrowserProfile(
        BrowserRequestProfile profile,
        Action<BrowserRequestProfile>? persistProfile = null)
    {
        if (!profile.IsValid()) throw new ArgumentException("浏览器请求配置无效", nameof(profile));
        lock (UserAgentLock)
        {
            authenticatedBrowserProfile = profile;
            persistAuthenticatedBrowserProfile = persistProfile;
        }
    }

    internal static string GenerateDefaultUserAgent(Random random)
    {
        return GenerateTransportUserAgent(random);
    }

    internal static string GenerateTransportUserAgent(Random random)
    {
        return random.Next(3) switch
        {
            0 => $"Dart/3.{random.Next(6, 10)} (dart:io)",
            1 => CurlUserAgents[random.Next(CurlUserAgents.Length)],
            _ => BuildDalvikUserAgent(random)
        };
    }

    private static string BuildDalvikUserAgent(Random random)
    {
        string device = AndroidDevices[random.Next(AndroidDevices.Length)];
        int android = random.Next(device == "SM-S9380" ? 15 : 14, 17);
        return $"Dalvik/2.1.0 (Linux; U; Android {android}; {device})";
    }

    internal static bool PrepareRiskControlRetry()
    {
        lock (UserAgentLock)
        {
            if (!automaticUserAgent || !string.IsNullOrEmpty(Config.COOKIE)) return false;
            userAgent = GenerateDifferentTransportUserAgent(userAgent);
            return true;
        }
    }

    private static string GenerateDifferentTransportUserAgent(string previous)
    {
        string replacement;
        do
        {
            replacement = GenerateTransportUserAgent(Random.Shared);
        } while (replacement == previous);
        return replacement;
    }

    public static bool ShouldSendCookie(string url)
    {
        if (string.IsNullOrEmpty(Config.COOKIE) || !Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return false;
        }

        var host = uri.Host;
        if (Config.COOKIE_IS_INTL && !IsIntlCookieDestination(uri))
            return false;
        return Config.COOKIE_ALLOWED_DOMAINS.Any(domain =>
            string.Equals(host, domain, StringComparison.OrdinalIgnoreCase)
            || host.EndsWith("." + domain, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsIntlCookieDestination(Uri uri)
    {
        if (uri.Scheme != Uri.UriSchemeHttps || uri.UserInfo.Length != 0) return false;
        if (IsDomain(uri.Host, "bilibili.tv") || IsDomain(uri.Host, "biliintl.com")) return true;
        // A custom parsing host is an explicit credential-forwarding choice;
        // an allowed media/CDN host alone is not such authorization.
        return uri.AbsolutePath.StartsWith("/intl/gateway/", StringComparison.Ordinal)
            && (MatchesIntlProxy(uri, Config.HOST) || MatchesIntlProxy(uri, Config.EPHOST));
    }

    private static bool IsDomain(string host, string domain)
        => host.Equals(domain, StringComparison.OrdinalIgnoreCase)
            || host.EndsWith("." + domain, StringComparison.OrdinalIgnoreCase);

    private static bool MatchesIntlProxy(Uri target, string configuredHost)
    {
        if (!Uri.TryCreate(configuredHost.Contains("://", StringComparison.Ordinal) ? configuredHost : "https://" + configuredHost,
            UriKind.Absolute, out var configured) || configured.Scheme != Uri.UriSchemeHttps
            || IsDomain(configured.Host, "bilibili.com") || configured.UserInfo.Length != 0)
            return false;
        return target.Host.Equals(configured.Host, StringComparison.OrdinalIgnoreCase) && target.Port == configured.Port;
    }

    public static string GetCookieHeaderValue(string url)
    {
        return !Config.COOKIE_IS_INTL && (url.Contains("/ep") || url.Contains("/ss")) ? Config.COOKIE + ";CURRENT_FNVAL=4048;" : Config.COOKIE;
    }

    public static void TryAddCookieHeader(HttpRequestMessage request, string url)
    {
        if (ShouldSendCookie(url))
        {
            request.Headers.TryAddWithoutValidation("Cookie", GetCookieHeaderValue(url));
        }
    }

    public static async Task<string> GetWebSourceAsync(string url, string? userAgent = null)
    {
        string htmlCode = await GetWebSourceAsync(GetWebHttpClient(Config.COOKIE_IS_INTL), url, userAgent);
        LogDebug("Response: {0}", htmlCode);
        return htmlCode;
    }

    internal static async Task<string> GetAuthenticatedWebSourceAsync(string url)
    {
        string htmlCode = await GetWebSourceAsync(GetWebHttpClient(Config.COOKIE_IS_INTL), url,
            forceAuthenticatedProfile: true);
        LogDebug("Response: {0}", htmlCode);
        return htmlCode;
    }

    internal static async Task<string> GetIntlAppSourceAsync(
        string url,
        HttpClient? httpClient = null,
        CancellationToken cancellationToken = default,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        // App credentials never share the domestic cookie jar or redirect policy.
        var json = await ExecuteWebRequestAsync(httpClient ?? IntlApiHttpClient, HttpMethod.Get, url,
            "Bilibili Freedoooooom/MarkII", Config.COOKIE_IS_INTL, false,
            (response, token) => response.Content.ReadAsStringAsync(token), cancellationToken, delay, null,
            configureRequest: request =>
            {
                request.Headers.TryAddWithoutValidation("APP-KEY", "bstar_a");
                request.Headers.TryAddWithoutValidation("ENV", "prod");
            });
        // Successful responses include signed media URLs; leave their contents out of logs.
        LogDebug("国际站 App 响应: {0} 字符", json.Length);
        return json;
    }

    internal static Task<string> GetWebSourceAsync(
        HttpClient httpClient,
        string url,
        string? requestedUserAgent = null,
        bool sendCookie = true,
        bool forceAuthenticatedProfile = false,
        CancellationToken cancellationToken = default,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        Action<string>? log = null,
        Func<RequestIdentity, RequestIdentity?>? rotateIdentity = null)
        => ExecuteWebRequestAsync(httpClient, HttpMethod.Get, url, requestedUserAgent, sendCookie,
            forceAuthenticatedProfile, (response, token) => response.Content.ReadAsStringAsync(token),
            cancellationToken, delay, log, rotateIdentity);

    private static Task<T> ExecuteWebRequestAsync<T>(
        HttpClient httpClient,
        HttpMethod method,
        string url,
        string? requestedUserAgent,
        bool sendCookie,
        bool forceAuthenticatedProfile,
        Func<HttpResponseMessage, CancellationToken, Task<T>> readResponse,
        CancellationToken cancellationToken,
        Func<TimeSpan, CancellationToken, Task>? delay,
        Action<string>? log,
        Func<RequestIdentity, RequestIdentity?>? rotateIdentity = null,
        Action<HttpRequestMessage>? configureRequest = null)
    {
        // Capture once per logical request; ordinary network retries keep all identity headers.
        var identity = ResolveRequestIdentity(url, requestedUserAgent, sendCookie, forceAuthenticatedProfile);
        var riskControlRetried = false;
        rotateIdentity ??= RotateAutomaticIdentity;
        return NetworkRetry.ExecuteAsync(async token =>
        {
            // ResponseHeadersRead leaves body reads outside HttpClient.Timeout. Apply the same
            // deadline to the entire attempt, including the body and the one permitted 412 retry.
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            if (httpClient.Timeout != Timeout.InfiniteTimeSpan) deadline.CancelAfter(httpClient.Timeout);
            try
            {
                while (true)
                {
                    using var request = CreateWebRequest(method, url, identity, sendCookie);
                    configureRequest?.Invoke(request);
                    LogDebug("获取网页内容: Url: {0}, Headers: {1}", url, request.Headers);
                    using var response = await httpClient.SendAsync(request,
                        HttpCompletionOption.ResponseHeadersRead, deadline.Token);
                    if (response.StatusCode == HttpStatusCode.PreconditionFailed
                        && requestedUserAgent is null && !riskControlRetried)
                    {
                        riskControlRetried = true;
                        var replacement = rotateIdentity(identity);
                        if (replacement is not null)
                        {
                            LogDebug(identity.BrowserProfile is null
                                ? "服务端返回HTTP 412，自动更换User-Agent后重试"
                                : "服务端返回HTTP 412，自动更换完整浏览器请求配置后重试");
                            identity = replacement.Value;
                            continue;
                        }
                    }
                    NetworkRetry.EnsureSuccessStatusCode(response);
                    try
                    {
                        return await readResponse(response, deadline.Token);
                    }
                    catch (HttpRequestException error) when (IsUnclassifiedResponseReadError(error))
                    {
                        // HttpContent wraps a plain transport IOException as Unknown. Only this
                        // remote body-read boundary can identify it without retrying local IO.
                        throw new DownloadInterruptedException("HTTP response body was interrupted.", error);
                    }
                }
            }
            catch (OperationCanceledException error) when (!token.IsCancellationRequested && deadline.IsCancellationRequested)
            {
                throw new OperationCanceledException("HTTP request timed out.",
                    new TimeoutException("HTTP request timed out.", error), deadline.Token);
            }
        }, NetworkRetry.RequestDelays, "获取网页", cancellationToken, delay, log);
    }

    private static bool IsUnclassifiedResponseReadError(HttpRequestException error)
    {
        if (error.HttpRequestError != HttpRequestError.Unknown || error.StatusCode.HasValue
            || error.InnerException?.GetType() != typeof(IOException)) return false;
        for (Exception? cause = error.InnerException; cause is not null; cause = cause.InnerException)
        {
            // InvalidDataException includes deterministic gzip/deflate corruption. Explicit
            // protocol errors and cancellation retain their original classification as well.
            if (cause is InvalidDataException or HttpIOException or HttpRequestException
                or AuthenticationException or OperationCanceledException) return false;
        }
        return true;
    }

    internal static void ApplyWebRequestHeaders(
        HttpRequestMessage request,
        string url,
        bool sendCookie = true,
        bool forceAuthenticatedProfile = false)
    {
        var identity = ResolveRequestIdentity(url, null, sendCookie, forceAuthenticatedProfile);
        ApplyRequestIdentity(request, identity, url);
        if (sendCookie) TryAddCookieHeader(request, url);
        if (request.Method == HttpMethod.Get && url.Contains("api.bilibili.com", StringComparison.OrdinalIgnoreCase))
            request.Headers.TryAddWithoutValidation("Referer", "https://www.bilibili.com/");
        AddIntlReferer(request, url);
    }

    private static HttpRequestMessage CreateWebRequest(
        HttpMethod method,
        string url,
        RequestIdentity identity,
        bool sendCookie)
    {
        var request = new HttpRequestMessage(method, url);
        ApplyRequestIdentity(request, identity, url);
        if (sendCookie) TryAddCookieHeader(request, url);
        if (method == HttpMethod.Get && url.Contains("api.bilibili.com"))
            request.Headers.TryAddWithoutValidation("Referer", "https://www.bilibili.com/");
        AddIntlReferer(request, url);
        request.Headers.CacheControl = CacheControlHeaderValue.Parse("no-cache");
        request.Headers.Connection.Clear();
        return request;
    }

    private static RequestIdentity ResolveRequestIdentity(
        string url,
        string? requestedUserAgent,
        bool sendCookie,
        bool forceAuthenticatedProfile)
    {
        lock (UserAgentLock)
        {
            if (requestedUserAgent is not null)
                return new RequestIdentity(requestedUserAgent, null);

            bool useBrowserProfile = automaticUserAgent
                && (forceAuthenticatedProfile || (sendCookie && ShouldSendCookie(url)));
            return useBrowserProfile
                ? new RequestIdentity(authenticatedBrowserProfile.UserAgent, authenticatedBrowserProfile)
                : new RequestIdentity(userAgent, null);
        }
    }

    private static void ApplyRequestIdentity(HttpRequestMessage request, RequestIdentity identity, string url)
    {
        if (identity.BrowserProfile is not null)
        {
            identity.BrowserProfile.Apply(request);
            request.Headers.TryAddWithoutValidation("Sec-Fetch-Dest", "empty");
            request.Headers.TryAddWithoutValidation("Sec-Fetch-Mode", "cors");
            request.Headers.TryAddWithoutValidation("Sec-Fetch-Site", IsBilibiliSameSite(url) ? "same-site" : "cross-site");
            return;
        }

        request.Headers.TryAddWithoutValidation("User-Agent", identity.UserAgent);
        request.Headers.TryAddWithoutValidation("Accept-Encoding", "gzip, deflate");
    }

    private static void AddIntlReferer(HttpRequestMessage request, string url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri)
            && (uri.Host.Equals("bilibili.tv", StringComparison.OrdinalIgnoreCase)
                || uri.Host.EndsWith(".bilibili.tv", StringComparison.OrdinalIgnoreCase)
                || uri.Host.Equals("biliintl.com", StringComparison.OrdinalIgnoreCase)
                || uri.Host.EndsWith(".biliintl.com", StringComparison.OrdinalIgnoreCase)))
            request.Headers.TryAddWithoutValidation("Referer", "https://www.bilibili.tv/");
    }

    private static bool IsBilibiliSameSite(string url)
    {
        var domain = Config.COOKIE_IS_INTL ? "bilibili.tv" : "bilibili.com";
        return Uri.TryCreate(url, UriKind.Absolute, out var uri)
            && (uri.Host.Equals(domain, StringComparison.OrdinalIgnoreCase)
                || uri.Host.EndsWith("." + domain, StringComparison.OrdinalIgnoreCase));
    }

    private static RequestIdentity? RotateAutomaticIdentity(RequestIdentity failedIdentity)
    {
        BrowserRequestProfile? changedProfile = null;
        Action<BrowserRequestProfile>? persistProfile = null;
        RequestIdentity retryIdentity;

        lock (UserAgentLock)
        {
            if (!automaticUserAgent) return null;
            if (failedIdentity.BrowserProfile is not null)
            {
                if (authenticatedBrowserProfile != failedIdentity.BrowserProfile)
                    return new RequestIdentity(authenticatedBrowserProfile.UserAgent, authenticatedBrowserProfile);

                do
                {
                    changedProfile = BrowserRequestProfile.Create(Random.Shared);
                } while (changedProfile == authenticatedBrowserProfile);
                authenticatedBrowserProfile = changedProfile;
                persistProfile = persistAuthenticatedBrowserProfile;
                retryIdentity = new RequestIdentity(changedProfile.UserAgent, changedProfile);
            }
            else
            {
                if (userAgent != failedIdentity.UserAgent)
                    return new RequestIdentity(userAgent, null);
                userAgent = GenerateDifferentTransportUserAgent(userAgent);
                retryIdentity = new RequestIdentity(userAgent, null);
            }
        }

        if (changedProfile is not null && persistProfile is not null)
        {
            try
            {
                persistProfile(changedProfile);
            }
            catch (Exception ex)
            {
                LogWarn($"保存浏览器请求配置失败，将仅在本次运行中使用新配置。原因：{ex.Message}");
            }
        }
        return retryIdentity;
    }

    internal readonly record struct RequestIdentity(
        string UserAgent,
        BrowserRequestProfile? BrowserProfile);

    // 重写重定向处理, 自动跟随多次重定向
    public static async Task<string> GetWebLocationAsync(string url)
    {
        bool sendCookie = ShouldSendCookie(url);
        // International credentials stay on the checked HTTPS destination;
        // automatic redirects must never carry the explicit Cookie elsewhere.
        string location = await GetWebLocationAsync(GetWebHttpClient(Config.COOKIE_IS_INTL), url, sendCookie);
        LogDebug("Location: {0}", location);
        return location;
    }

    internal static Task<string> GetWebLocationAsync(
        HttpClient httpClient,
        string url,
        bool sendCookie = true,
        CancellationToken cancellationToken = default,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        Action<string>? log = null)
        => ExecuteWebRequestAsync(httpClient, HttpMethod.Head, url, null, sendCookie, false,
            (response, _) => Task.FromResult(response.RequestMessage?.RequestUri?.AbsoluteUri ?? url),
            cancellationToken, delay, log);

    public static async Task<byte[]> GetPostResponseAsync(string Url, byte[] postData, Dictionary<string, string>? headers = null)
    {
        LogDebug("Post to: {0}, data: {1}", Url, Convert.ToBase64String(postData));

        using ByteArrayContent content = new(postData);
        content.Headers.ContentType = MediaTypeHeaderValue.Parse("application/grpc");

        using HttpRequestMessage request = new()
        {
            RequestUri = new Uri(Url),
            Method = HttpMethod.Post,
            Content = content,
            //Version = HttpVersion.Version20
        };

        if (headers != null)
        {
            foreach (KeyValuePair<string, string> header in headers)
                request.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }
        else
        {
            request.Headers.TryAddWithoutValidation("User-Agent", "Dalvik/2.1.0 (Linux; U; Android 6.0.1; oneplus a5010 Build/V417IR) 6.10.0 os/android model/oneplus a5010 mobi_app/android build/6100500 channel/bili innerVer/6100500 osVer/6.0.1 network/2");
            request.Headers.TryAddWithoutValidation("grpc-encoding", "gzip");
        }

        using HttpResponseMessage response = await AppHttpClient.SendAsync(request);
        byte[] bytes = await response.Content.ReadAsByteArrayAsync();

        return bytes;
    }
}
