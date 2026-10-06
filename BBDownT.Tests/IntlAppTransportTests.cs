using System.Net;
using System.Security.Cryptography;
using System.Text;
using BBDownT.Core;
using BBDownT.Core.Util;

namespace BBDownT.Tests;

public class IntlAppTransportTests
{
    private const string AppUrl = "https://app.biliintl.com/intl/gateway/v2/app/playurl/player";
    private const string FixtureCookie = "intl_session=synthetic-cookie";

    [Fact]
    public void PlayUrl_UsesSortedEncodedSignedAppParametersAndRequestedEpisode()
    {
        using var config = new ConfigScope();
        Config.TOKEN = "synthetic+token /&=中文";
        var url = IntlBangumiAppApi.BuildPlayUrl("2309571", "26222855", "0", timestamp: 1700000000);
        const string query = "access_key=synthetic%2Btoken%20%2F%26%3D%E4%B8%AD%E6%96%87"
            + "&appkey=7d089525d3611b1c&build=3830100&ep_id=26222855&fnval=16&fnver=0&fourk=1"
            + "&mobi_app=bstar_a&platform=android&prefer_code_type=0&qn=0&sid=2309571&ts=1700000000";
        var expectedSign = Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(
            query + "acd495b248ec528c2eed1e862d393126"))).ToLowerInvariant();

        Assert.Equal(AppUrl + "?" + query + "&sign=" + expectedSign, url);
        Assert.DoesNotContain("ep_id=0", url);
        Assert.DoesNotContain("/ogv/playurl", url);
        Assert.DoesNotContain("s_locale", url);
    }

    [Theory]
    [InlineData("0", "0")]
    [InlineData("64", "1")]
    [InlineData("129", "0")]
    public void PlayUrl_AnonymousAndCodecQualityOverridesRemainExplicit(string quality, string codec)
    {
        using var config = new ConfigScope();
        Config.TOKEN = "synthetic-global-token";
        var query = new Uri(IntlBangumiAppApi.BuildPlayUrl("2309571", "26223058", quality, codec,
            timestamp: 1700000000, accessToken: "")).Query;

        Assert.DoesNotContain("access_key", query);
        Assert.Contains("ep_id=26223058&", query);
        Assert.Contains("prefer_code_type=" + codec + "&", query);
        Assert.Contains("qn=" + quality + "&", query);
    }

    [Theory]
    [InlineData("", "26222855", "0", "0")]
    [InlineData("0", "26222855", "0", "0")]
    [InlineData("000", "26222855", "0", "0")]
    [InlineData("season2309571", "26222855", "0", "0")]
    [InlineData("2309571", "", "0", "0")]
    [InlineData("2309571", "0", "0", "0")]
    [InlineData("2309571", "-1", "0", "0")]
    [InlineData("2309571", "26222855&sid=0", "0", "0")]
    [InlineData("2309571", "26222855", "999", "0")]
    [InlineData("2309571", "26222855", "0", "2")]
    public void PlayUrl_InvalidInputsAreRejectedBeforeFetching(string season, string episode, string quality, string codec)
    {
        using var config = new ConfigScope();
        Assert.Throws<ArgumentException>(() => IntlBangumiAppApi.BuildPlayUrl(season, episode, quality, codec));
    }

    [Theory]
    [InlineData("proxy.example.test:8443")]
    [InlineData("https://proxy.example.test:8443")]
    public void PlayUrl_ExplicitHttpsProxyPreservesAuthorityAndAppRoute(string host)
    {
        using var config = new ConfigScope();
        Config.HOST = host;
        var url = new Uri(IntlBangumiAppApi.BuildPlayUrl("2309571", "26222855", "0", timestamp: 1700000000));
        Assert.Equal("https", url.Scheme);
        Assert.Equal("proxy.example.test", url.Host);
        Assert.Equal(8443, url.Port);
        Assert.Equal("/intl/gateway/v2/app/playurl/player", url.AbsolutePath);
    }

    [Theory]
    [InlineData("http://proxy.example.test")]
    [InlineData("https://synthetic-user@proxy.example.test")]
    [InlineData("https://proxy.example.test/another-path")]
    [InlineData("https://proxy.example.test/?other=1")]
    [InlineData("https://proxy.example.test/#other")]
    public void PlayUrl_InvalidProxyAuthoritiesAreRejected(string host)
    {
        using var config = new ConfigScope();
        Config.HOST = host;
        Assert.Throws<ArgumentException>(() => IntlBangumiAppApi.BuildPlayUrl("2309571", "26222855", "0"));
    }

    [Fact]
    public async Task PlayJson_FetchOverrideReceivesRealAppRouteAndCurrentTimestamp()
    {
        using var config = new ConfigScope();
        var before = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var calls = new List<string>();
        const string fixture = "{\"code\":0,\"data\":{\"video_info\":{\"stream_list\":[],\"dash_audio\":[]}}}";
        var json = await IntlBangumiAppApi.GetPlayJsonAsync("2309571", "26222855", "0", fetch: url =>
        {
            calls.Add(url);
            return Task.FromResult(fixture);
        });
        var request = new Uri(Assert.Single(calls));
        var parameters = request.Query.TrimStart('?').Split('&').Select(pair => pair.Split('=', 2))
            .ToDictionary(pair => pair[0], pair => pair[1]);
        Assert.Equal(AppUrl, request.GetLeftPart(UriPartial.Path));
        Assert.Equal("2309571", parameters["sid"]);
        Assert.Equal("26222855", parameters["ep_id"]);
        Assert.InRange(long.Parse(parameters["ts"]), before, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        Assert.Equal(fixture, json);
    }

    [Theory]
    [InlineData(false, true, false)]
    [InlineData(true, true, true)]
    [InlineData(true, false, false)]
    public async Task AppTransport_FixedIdentityAndCookieRealmAllowlist(bool international, bool allowed, bool expectedCookie)
    {
        using var config = new ConfigScope();
        Config.COOKIE_IS_INTL = international;
        Config.COOKIE_ALLOWED_DOMAINS = allowed ? ["biliintl.com"] : ["other.example.test"];
        using var handler = new MemoryHandler((request, _) =>
        {
            AssertAppHeaders(request);
            Assert.Equal(expectedCookie, request.Headers.Contains("Cookie"));
            if (expectedCookie) Assert.Equal(FixtureCookie, Assert.Single(request.Headers.GetValues("Cookie")));
            return Task.FromResult(Response(request, HttpStatusCode.OK, new StringContent("complete")));
        });
        using var client = new HttpClient(handler);
        Assert.Equal("complete", await HTTPUtil.GetIntlAppSourceAsync(AppUrl, client));
    }

    [Theory]
    [InlineData("https://proxy.example.test:8443/intl/gateway/v2/app/playurl/player", true)]
    [InlineData("https://proxy.example.test/intl/gateway/v2/app/playurl/player", false)]
    [InlineData("http://proxy.example.test:8443/intl/gateway/v2/app/playurl/player", false)]
    [InlineData("https://child.proxy.example.test:8443/intl/gateway/v2/app/playurl/player", false)]
    [InlineData("https://proxy.example.test:8443/media/video.m4s", false)]
    [InlineData("https://api.bilibili.com/intl/gateway/v2/app/playurl/player", false)]
    public async Task AppTransport_InternationalProxyCookieRequiresExactHttpsGateway(string url, bool expectedCookie)
    {
        using var config = new ConfigScope();
        Config.COOKIE_IS_INTL = true;
        Config.HOST = "proxy.example.test:8443";
        Config.COOKIE_ALLOWED_DOMAINS = ["proxy.example.test", "bilibili.com"];
        using var handler = new MemoryHandler((request, _) =>
        {
            Assert.Equal(expectedCookie, request.Headers.Contains("Cookie"));
            return Task.FromResult(Response(request, HttpStatusCode.OK, new StringContent("complete")));
        });
        using var client = new HttpClient(handler);
        Assert.Equal("complete", await HTTPUtil.GetIntlAppSourceAsync(url, client));
    }

    [Fact]
    public async Task AppTransport_RetriesStatusAndInterruptedBodyWithFreshStableRequests()
    {
        using var config = new ConfigScope();
        Config.COOKIE_IS_INTL = true;
        var requests = new List<HttpRequestMessage>();
        var headers = new List<string>();
        var contents = new List<MemoryContent>();
        var waits = new List<TimeSpan>();
        using var handler = new MemoryHandler((request, _) =>
        {
            requests.Add(request);
            headers.Add(request.Headers.ToString());
            AssertAppHeaders(request);
            Assert.Equal(FixtureCookie, Assert.Single(request.Headers.GetValues("Cookie")));
            var attempt = requests.Count;
            var content = new MemoryContent(async (stream, token) =>
            {
                await stream.WriteAsync(Encoding.UTF8.GetBytes(attempt == 2 ? "partial" : "complete"), token);
                if (attempt == 2) throw new HttpIOException(HttpRequestError.ResponseEnded, "synthetic body interruption");
            });
            contents.Add(content);
            return Task.FromResult(Response(request, attempt == 1 ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK, content));
        });
        using var client = new HttpClient(handler);
        Assert.Equal("complete", await HTTPUtil.GetIntlAppSourceAsync(AppUrl, client,
            delay: (wait, _) => { waits.Add(wait); return Task.CompletedTask; }));
        Assert.Equal(3, requests.Count);
        Assert.NotSame(requests[0], requests[1]);
        Assert.NotSame(requests[1], requests[2]);
        Assert.All(headers, header => Assert.Equal(headers[0], header));
        Assert.All(contents, content => Assert.True(content.Disposed));
        Assert.Equal(new[] { TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(3) }, waits);
    }

    [Theory]
    [InlineData(HttpStatusCode.Found)]
    [InlineData(HttpStatusCode.PreconditionFailed)]
    public async Task AppTransport_RedirectAnd412AreNotFollowedOrRotated(HttpStatusCode status)
    {
        using var config = new ConfigScope();
        var calls = 0;
        using var handler = new MemoryHandler((request, _) =>
        {
            calls++;
            AssertAppHeaders(request);
            var response = Response(request, status, new StringContent(""));
            response.Headers.Location = new Uri("https://other.example.test/redirect");
            return Task.FromResult(response);
        });
        using var client = new HttpClient(handler);
        var error = await Assert.ThrowsAsync<HttpRequestException>(() => HTTPUtil.GetIntlAppSourceAsync(AppUrl, client,
            delay: (_, _) => throw new InvalidOperationException("must not retry")));
        Assert.Equal(status, error.StatusCode);
        Assert.Equal(1, calls);
        using var isolatedHandler = HTTPUtil.CreateWebHandler(useCookies: false, allowRedirects: false);
        Assert.False(isolatedHandler.UseCookies);
        Assert.False(isolatedHandler.AllowAutoRedirect);
        Assert.Same(HTTPUtil.IntlApiHttpClient, HTTPUtil.GetWebHttpClient(true));
    }

    [Fact]
    public async Task AppTransport_CallerCancellationStopsBackoffWithoutAnotherAttempt()
    {
        using var config = new ConfigScope();
        using var caller = new CancellationTokenSource();
        var attempts = 0;
        using var handler = new MemoryHandler((request, _) =>
        {
            attempts++;
            return Task.FromResult(Response(request, HttpStatusCode.ServiceUnavailable, new StringContent("")));
        });
        using var client = new HttpClient(handler);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => HTTPUtil.GetIntlAppSourceAsync(AppUrl, client,
            cancellationToken: caller.Token, delay: (_, token) =>
            {
                Assert.Equal(caller.Token, token);
                caller.Cancel();
                return Task.FromCanceled(token);
            }));
        Assert.Equal(1, attempts);
    }

    [Fact]
    public async Task AppTransport_DebugOutputRedactsRequestCredentialsAndOmitsSignedResponseUrls()
    {
        using var config = new ConfigScope();
        Config.DEBUG_LOG = true;
        Config.COOKIE_IS_INTL = true;
        Config.TOKEN = "synthetic-debug-token";
        const string signedUrl = "https://media.example.test/video.m4s?signature=synthetic-media-secret";
        using var handler = new MemoryHandler((request, _) => Task.FromResult(Response(request,
            HttpStatusCode.OK, new StringContent("{\"url\":\"" + signedUrl + "\"}"))));
        using var client = new HttpClient(handler);
        var originalOutput = Console.Out;
        using var output = new StringWriter();
        try
        {
            Console.SetOut(output);
            await HTTPUtil.GetIntlAppSourceAsync(IntlBangumiAppApi.BuildPlayUrl("2309571", "26222855", "0"), client);
        }
        finally { Console.SetOut(originalOutput); }
        var log = output.ToString();
        Assert.DoesNotContain(Config.TOKEN, log);
        Assert.DoesNotContain("synthetic-cookie", log);
        Assert.DoesNotContain(signedUrl, log);
        Assert.DoesNotContain("synthetic-media-secret", log);
        Assert.Contains("APP-KEY: bstar_a", log);
    }

    private static void AssertAppHeaders(HttpRequestMessage request)
    {
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal("Bilibili Freedoooooom/MarkII", string.Join(' ', request.Headers.GetValues("User-Agent")));
        Assert.Equal("bstar_a", Assert.Single(request.Headers.GetValues("APP-KEY")));
        Assert.Equal("prod", Assert.Single(request.Headers.GetValues("ENV")));
        Assert.False(request.Headers.Contains("Sec-CH-UA"));
    }

    private static HttpResponseMessage Response(HttpRequestMessage request, HttpStatusCode status, HttpContent content)
        => new(status) { RequestMessage = request, Content = content };

    private sealed class ConfigScope : IDisposable
    {
        private readonly string cookie = Config.COOKIE;
        private readonly bool international = Config.COOKIE_IS_INTL;
        private readonly string token = Config.TOKEN;
        private readonly string host = Config.HOST;
        private readonly string episodeHost = Config.EPHOST;
        private readonly string[] domains = Config.COOKIE_ALLOWED_DOMAINS;
        private readonly bool debug = Config.DEBUG_LOG;

        internal ConfigScope()
        {
            Config.COOKIE = FixtureCookie;
            Config.COOKIE_IS_INTL = false;
            Config.TOKEN = "";
            Config.HOST = Config.EPHOST = "api.bilibili.com";
            Config.COOKIE_ALLOWED_DOMAINS = ["bilibili.com", "bilibili.tv", "biliintl.com"];
            Config.DEBUG_LOG = false;
        }

        public void Dispose()
        {
            Config.COOKIE = cookie;
            Config.COOKIE_IS_INTL = international;
            Config.TOKEN = token;
            Config.HOST = host;
            Config.EPHOST = episodeHost;
            Config.COOKIE_ALLOWED_DOMAINS = domains;
            Config.DEBUG_LOG = debug;
        }
    }

    private sealed class MemoryHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => send(request, cancellationToken);
    }

    private sealed class MemoryContent(Func<Stream, CancellationToken, Task> write) : HttpContent
    {
        internal bool Disposed { get; private set; }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
            => write(stream, CancellationToken.None);
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken)
            => write(stream, cancellationToken);
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
    }
}
