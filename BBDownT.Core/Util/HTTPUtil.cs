using System.Net;
using System.Net.Http.Headers;
using System.Net.Security;
using static BBDownT.Core.Logger;

namespace BBDownT.Core.Util;

public static class HTTPUtil
{
    public static readonly HttpClient AppHttpClient = new(new HttpClientHandler
    {
        AllowAutoRedirect = true,
        AutomaticDecompression = DecompressionMethods.All,
        MaxConnectionsPerServer = 2048,
        ServerCertificateCustomValidationCallback = (_, _, _, sslPolicyErrors) =>
            Config.ALLOW_INSECURE_TLS || sslPolicyErrors == SslPolicyErrors.None
    })
    {
        Timeout = TimeSpan.FromMinutes(2)
    };

    private static readonly object UserAgentLock = new();
    private static readonly string[] AndroidDevices =
        ["Pixel 4", "Pixel 5", "Pixel 6", "Pixel 7", "Pixel 8", "Pixel 9", "SM-S9080", "SM-S9180",
         "SM-S9280", "SM-S9380", "M2012K11AC", "2210132C", "23127PN0CC", "24031PN0DC", "V2307A", "V2408A"];
    private static readonly string[] CurlUserAgents =
        ["curl/8.10.1", "curl/8.11.1", "curl/8.12.1", "curl/8.13.0", "curl/8.14.1", "curl/8.15.0", "curl/8.16.0"];
    private static string userAgent = GenerateDefaultUserAgent(Random.Shared);
    private static bool automaticUserAgent = true;

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

    private static string? RotateAutomaticUserAgent(string failedUserAgent)
    {
        lock (UserAgentLock)
        {
            if (!automaticUserAgent) return null;
            if (userAgent != failedUserAgent) return userAgent;

            string replacement;
            do
            {
                replacement = GenerateTransportUserAgent(Random.Shared);
            } while (replacement == failedUserAgent);
            userAgent = replacement;
            return replacement;
        }
    }

    public static bool ShouldSendCookie(string url)
    {
        if (string.IsNullOrEmpty(Config.COOKIE) || !Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return false;
        }

        var host = uri.Host;
        return Config.COOKIE_ALLOWED_DOMAINS.Any(domain =>
            string.Equals(host, domain, StringComparison.OrdinalIgnoreCase)
            || host.EndsWith("." + domain, StringComparison.OrdinalIgnoreCase));
    }

    public static string GetCookieHeaderValue(string url)
    {
        return (url.Contains("/ep") || url.Contains("/ss")) ? Config.COOKIE + ";CURRENT_FNVAL=4048;" : Config.COOKIE;
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
        using var webResponse = (await SendWebRequestAsync(HttpMethod.Get, url, userAgent, sendCookie: true)).EnsureSuccessStatusCode();
        string htmlCode = await webResponse.Content.ReadAsStringAsync();
        LogDebug("Response: {0}", htmlCode);
        return htmlCode;
    }

    private static async Task<HttpResponseMessage> SendWebRequestAsync(HttpMethod method, string url, string? requestedUserAgent, bool sendCookie)
    {
        string firstUserAgent = requestedUserAgent ?? UserAgent;
        using var webRequest = CreateWebRequest(method, url, firstUserAgent, sendCookie);
        LogDebug("获取网页内容: Url: {0}, Headers: {1}", url, webRequest.Headers);
        var response = await AppHttpClient.SendAsync(webRequest, HttpCompletionOption.ResponseHeadersRead);
        if (response.StatusCode != HttpStatusCode.PreconditionFailed || requestedUserAgent is not null)
        {
            return response;
        }

        string? retryUserAgent = RotateAutomaticUserAgent(firstUserAgent);
        if (retryUserAgent is null) return response;

        response.Dispose();
        LogDebug("服务端返回HTTP 412，自动更换User-Agent后重试");
        using var retryRequest = CreateWebRequest(method, url, retryUserAgent, sendCookie);
        LogDebug("重试获取网页内容: Url: {0}, Headers: {1}", url, retryRequest.Headers);
        return await AppHttpClient.SendAsync(retryRequest, HttpCompletionOption.ResponseHeadersRead);
    }

    private static HttpRequestMessage CreateWebRequest(HttpMethod method, string url, string requestUserAgent, bool sendCookie)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.TryAddWithoutValidation("User-Agent", requestUserAgent);
        request.Headers.TryAddWithoutValidation("Accept-Encoding", "gzip, deflate");
        if (sendCookie) TryAddCookieHeader(request, url);
        if (method == HttpMethod.Get && url.Contains("api.bilibili.com"))
            request.Headers.TryAddWithoutValidation("Referer", "https://www.bilibili.com/");
        request.Headers.CacheControl = CacheControlHeaderValue.Parse("no-cache");
        request.Headers.Connection.Clear();
        return request;
    }

    // 重写重定向处理, 自动跟随多次重定向
    public static async Task<string> GetWebLocationAsync(string url)
    {
        using var webResponse = (await SendWebRequestAsync(HttpMethod.Head, url, null, sendCookie: false)).EnsureSuccessStatusCode();
        string location = webResponse.RequestMessage?.RequestUri?.AbsoluteUri ?? url;
        LogDebug("Location: {0}", location);
        return location;
    }

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
