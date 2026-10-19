using System.Net;
using BBDownT.Core;
using BBDownT.Core.Util;

namespace BBDownT.Tests;

public sealed class AuthenticatedRiskControlTests : IDisposable
{
    private const string Url = "https://api.bilibili.com/x/player/playurl";
    private const string Cookie = "SESSDATA=test;bili_jct=test";
    private readonly WorkStateScope state = new();
    private readonly BrowserRequestProfile previousProfile = HTTPUtil.AuthenticatedBrowserProfile;
    private readonly BrowserRequestProfile profile = BrowserRequestProfile.CreateChromium(new Random(83));
    private readonly List<BrowserRequestProfile> persisted = [];

    public AuthenticatedRiskControlTests()
    {
        HTTPUtil.UserAgent = "";
        Config.COOKIE = Cookie;
        Config.COOKIE_IS_INTL = false;
        Config.COOKIE_ALLOWED_DOMAINS = ["bilibili.com", "bilibili.tv"];
        HTTPUtil.ConfigureAuthenticatedBrowserProfile(profile, persisted.Add);
    }

    public void Dispose()
    {
        HTTPUtil.ConfigureAuthenticatedBrowserProfile(previousProfile);
        state.Dispose();
    }

    [Fact]
    public async Task Domestic412_KeepsCookieAndTransportIdentityForSubsequentRequests()
    {
        var transport = HTTPUtil.UserAgent;
        var sent = new List<Headers>();
        using var handler = new DelegateHandler(request =>
        {
            sent.Add(Headers.Read(request));
            return Response(request, sent.Count == 1 ? HttpStatusCode.PreconditionFailed : HttpStatusCode.OK);
        });
        using var client = new HttpClient(handler);

        Assert.Equal("complete", await Get(client));
        Assert.Equal("complete", await Get(client));
        Assert.Equal("complete", await HTTPUtil.GetWebSourceAsync(client, "https://example.test/external", log: _ => { }));

        Assert.Equal(4, sent.Count);
        Assert.Equal(profile.UserAgent, sent[0].UserAgent);
        Assert.True(sent[0].BrowserHints);
        Assert.All(sent.Skip(1), headers =>
        {
            Assert.Equal(transport, headers.UserAgent);
            Assert.False(headers.BrowserHints);
        });
        Assert.All(sent.Take(3), headers =>
        {
            Assert.Equal(Cookie, headers.Cookie);
            Assert.Equal("https://www.bilibili.com/", headers.Referer);
        });
        Assert.Null(sent[3].Cookie);
        Assert.Equal(profile, HTTPUtil.AuthenticatedBrowserProfile);
        Assert.Empty(persisted);
    }

    [Fact]
    public async Task Domestic412_NetworkRetryKeepsFallbackAndDoesNotReset412Budget()
    {
        var transport = HTTPUtil.UserAgent;
        var sent = new List<Headers>();
        var waits = new List<TimeSpan>();
        using var handler = new DelegateHandler(request =>
        {
            sent.Add(Headers.Read(request));
            return Response(request, sent.Count == 2 ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.PreconditionFailed);
        });
        using var client = new HttpClient(handler);

        var failure = await Assert.ThrowsAsync<HttpRequestException>(() => HTTPUtil.GetWebSourceAsync(client, Url,
            delay: (wait, _) => { waits.Add(wait); return Task.CompletedTask; }, log: _ => { }));

        Assert.Equal(HttpStatusCode.PreconditionFailed, failure.StatusCode);
        Assert.Equal(new[] { profile.UserAgent, transport, transport }, sent.Select(headers => headers.UserAgent));
        Assert.All(sent, headers => Assert.Equal(Cookie, headers.Cookie));
        Assert.Equal(TimeSpan.FromSeconds(1), Assert.Single(waits));
        Assert.Equal(profile, HTTPUtil.AuthenticatedBrowserProfile);
        Assert.Empty(persisted);
    }

    [Theory]
    [InlineData("BV1fcHD6cEw3", false)]
    [InlineData("BV1fcHD6cEw3", true)]
    [InlineData("https://www.bilibili.com/video/BV1fcHD6cEw3/", false)]
    [InlineData("https://www.bilibili.com/video/BV1fcHD6cEw3/", true)]
    public async Task VideoEntry_Head412KeepsCookieAndSuccessfulFinalLocation(string input, bool bangumi)
    {
        const string videoUrl = "https://www.bilibili.com/video/av117403434486720/";
        var expectedLocation = bangumi ? "https://www.bilibili.com/bangumi/play/ep123" : videoUrl;
        var transport = HTTPUtil.UserAgent;
        var sent = new List<Headers>();
        using var handler = new DelegateHandler(request =>
        {
            Assert.Equal(HttpMethod.Head, request.Method);
            Assert.Equal(videoUrl, request.RequestUri?.AbsoluteUri);
            sent.Add(Headers.Read(request));
            Assert.InRange(sent.Count, 1, 2);
            if (sent.Count == 1) return Response(request, HttpStatusCode.PreconditionFailed);
            request.RequestUri = new Uri(expectedLocation);
            return Response(request, HttpStatusCode.OK);
        });
        using var client = new HttpClient(handler);
        string? finalLocation = null;
        var locationCalls = 0;

        var result = await BBDownTUtil.GetAvIdAsync(input, async url =>
        {
            Assert.Equal(videoUrl, url);
            locationCalls++;
            return finalLocation = await HTTPUtil.GetWebLocationAsync(client, url,
                delay: (_, _) => throw new InvalidOperationException("Unexpected network retry"), log: _ => { });
        });

        Assert.Equal(bangumi ? "ep:123" : "117403434486720", result);
        Assert.Equal(expectedLocation, finalLocation);
        Assert.Equal(1, locationCalls);
        Assert.Equal(new[] { profile.UserAgent, transport }, sent.Select(headers => headers.UserAgent));
        Assert.True(sent[0].BrowserHints);
        Assert.False(sent[1].BrowserHints);
        Assert.All(sent, headers => Assert.Equal(Cookie, headers.Cookie));
        Assert.Equal(profile, HTTPUtil.AuthenticatedBrowserProfile);
        Assert.Empty(persisted);
    }

    [Theory]
    [InlineData("BV1fcHD6cEw3")]
    [InlineData("https://www.bilibili.com/video/BV1fcHD6cEw3/")]
    public async Task VideoEntry_PersistentHead412StopsAfterOneFallback(string input)
    {
        const string videoUrl = "https://www.bilibili.com/video/av117403434486720/";
        var transport = HTTPUtil.UserAgent;
        var sent = new List<Headers>();
        using var handler = new DelegateHandler(request =>
        {
            Assert.Equal(HttpMethod.Head, request.Method);
            Assert.Equal(videoUrl, request.RequestUri?.AbsoluteUri);
            sent.Add(Headers.Read(request));
            Assert.InRange(sent.Count, 1, 2);
            return Response(request, HttpStatusCode.PreconditionFailed);
        });
        using var client = new HttpClient(handler);
        var locationCalls = 0;

        var failure = await Assert.ThrowsAsync<HttpRequestException>(() => BBDownTUtil.GetAvIdAsync(input, url =>
        {
            Assert.Equal(videoUrl, url);
            locationCalls++;
            return HTTPUtil.GetWebLocationAsync(client, url,
                delay: (_, _) => throw new InvalidOperationException("Unexpected network retry"), log: _ => { });
        }));

        Assert.Equal(HttpStatusCode.PreconditionFailed, failure.StatusCode);
        Assert.Equal(1, locationCalls);
        Assert.Equal(new[] { profile.UserAgent, transport }, sent.Select(headers => headers.UserAgent));
        Assert.True(sent[0].BrowserHints);
        Assert.False(sent[1].BrowserHints);
        Assert.All(sent, headers => Assert.Equal(Cookie, headers.Cookie));
        Assert.Equal(profile, HTTPUtil.AuthenticatedBrowserProfile);
        Assert.Empty(persisted);
    }

    [Fact]
    public async Task ConcurrentBrowser412s_ReuseTheSameFallbackIdentity()
    {
        var transport = HTTPUtil.UserAgent;
        var bothStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstAttempts = 0;
        var sent = new List<Headers>();
        using var handler = new DelegateHandler(async (request, _) =>
        {
            var headers = Headers.Read(request);
            lock (sent) sent.Add(headers);
            if (headers.UserAgent == profile.UserAgent)
            {
                if (Interlocked.Increment(ref firstAttempts) == 2) bothStarted.SetResult();
                await bothStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
                return Response(request, HttpStatusCode.PreconditionFailed);
            }
            return Response(request, HttpStatusCode.OK);
        });
        using var client = new HttpClient(handler);

        Assert.All(await Task.WhenAll(Get(client), Get(client)), result => Assert.Equal("complete", result));

        Assert.Equal(2, firstAttempts);
        Assert.Equal(4, sent.Count);
        Assert.Equal(2, sent.Count(headers => headers.UserAgent == transport));
        Assert.All(sent, headers => Assert.Equal(Cookie, headers.Cookie));
        Assert.Equal(profile, HTTPUtil.AuthenticatedBrowserProfile);
        Assert.Empty(persisted);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExplicitUserAgent_412DoesNotChangeIdentityOrRetry(bool perRequest)
    {
        const string custom = "test-explicit/1";
        if (!perRequest) HTTPUtil.UserAgent = custom;
        var sent = new List<Headers>();
        using var handler = new DelegateHandler(request =>
        {
            sent.Add(Headers.Read(request));
            return Response(request, HttpStatusCode.PreconditionFailed);
        });
        using var client = new HttpClient(handler);

        await Assert.ThrowsAsync<HttpRequestException>(() => HTTPUtil.GetWebSourceAsync(client, Url,
            requestedUserAgent: perRequest ? custom : null,
            delay: (_, _) => throw new InvalidOperationException("Unexpected retry"), log: _ => { }));

        var headers = Assert.Single(sent);
        Assert.Equal(custom, headers.UserAgent);
        Assert.Equal(Cookie, headers.Cookie);
        Assert.False(headers.BrowserHints);
        if (!perRequest) Assert.False(HTTPUtil.PrepareRiskControlRetry());
        Assert.Equal(profile, HTTPUtil.AuthenticatedBrowserProfile);
        Assert.Empty(persisted);
    }

    [Fact]
    public async Task Voucher_DefaultRotationKeepsCookieAndFallbackForQualityRefetch()
    {
        const string dash = """
            {"data":{"dash":{"duration":3,"video":[{"id":64,"base_url":"https://cdn.test/video.m4s",
            "backup_url":[],"bandwidth":1000000,"codecid":7,"width":1280,"height":720,"frame_rate":"30"}],
            "audio":[{"id":30280,"base_url":"https://cdn.test/audio.m4s","backup_url":[],"bandwidth":192000,"codecs":"mp4a.40.2"}]}}}
            """;
        var sent = new List<Headers>();
        var qualities = new List<string>();
        var waits = new List<int>();
        var originalTransport = HTTPUtil.UserAgent;

        var result = await Parser.ExtractTracksWithFetcherAsync("BV", "2", "3", false, false, false, "0",
            quality =>
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, Url);
                HTTPUtil.ApplyWebRequestHeaders(request, Url);
                sent.Add(Headers.Read(request));
                qualities.Add(quality);
                return Task.FromResult(sent.Count == 1
                    ? "{\"code\":0,\"data\":{\"v_voucher\":\"voucher-test\"}}" : dash);
            }, (_, _) => throw new InvalidOperationException("Unexpected intl request"),
            riskControlDelay: wait => { waits.Add(wait); return Task.CompletedTask; });

        Assert.Single(result.VideoTracks);
        Assert.Single(result.AudioTracks);
        Assert.Equal(new[] { "0", "0", Config.qualitys.Keys.First() }, qualities);
        Assert.Equal(1000, Assert.Single(waits));
        Assert.Equal(profile.UserAgent, sent[0].UserAgent);
        Assert.NotEqual(originalTransport, sent[1].UserAgent);
        Assert.Equal(sent[1], sent[2]);
        Assert.False(sent[1].BrowserHints);
        Assert.All(sent, headers => Assert.Equal(Cookie, headers.Cookie));
        Assert.Equal(profile, HTTPUtil.AuthenticatedBrowserProfile);
        Assert.Empty(persisted);
    }

    [Fact]
    public void StartingNextTask_RestoresAuthenticatedBrowserIdentity()
    {
        Assert.True(HTTPUtil.PrepareRiskControlRetry());
        using var fallback = new HttpRequestMessage(HttpMethod.Get, Url);
        HTTPUtil.ApplyWebRequestHeaders(fallback, Url);
        Assert.False(Headers.Read(fallback).BrowserHints);

        using var forced = new HttpRequestMessage(HttpMethod.Get, Url);
        HTTPUtil.ApplyWebRequestHeaders(forced, Url, forceAuthenticatedProfile: true);
        Assert.Equal(profile.UserAgent, Headers.Read(forced).UserAgent);
        Assert.True(Headers.Read(forced).BrowserHints);
        Assert.Equal(Cookie, Headers.Read(forced).Cookie);

        HTTPUtil.UserAgent = "";

        using var next = new HttpRequestMessage(HttpMethod.Get, Url);
        HTTPUtil.ApplyWebRequestHeaders(next, Url);
        Assert.Equal(profile.UserAgent, Headers.Read(next).UserAgent);
        Assert.True(Headers.Read(next).BrowserHints);
        Assert.Equal(Cookie, Headers.Read(next).Cookie);
        Assert.Empty(persisted);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task IntlOrForcedBrowser412_RotatesAndPersistsBrowserProfile(bool international)
    {
        Config.COOKIE_IS_INTL = international;
        var url = international ? "https://www.bilibili.tv/example" : Url;
        if (international) Assert.False(HTTPUtil.PrepareRiskControlRetry());
        var sent = new List<Headers>();
        using var handler = new DelegateHandler(request =>
        {
            sent.Add(Headers.Read(request));
            return Response(request, sent.Count == 1 ? HttpStatusCode.PreconditionFailed : HttpStatusCode.OK);
        });
        using var client = new HttpClient(handler);

        Assert.Equal("complete", await HTTPUtil.GetWebSourceAsync(client, url,
            forceAuthenticatedProfile: !international,
            delay: (_, _) => throw new InvalidOperationException("Unexpected network retry"), log: _ => { }));

        Assert.Equal(2, sent.Count);
        Assert.Equal(profile.UserAgent, sent[0].UserAgent);
        var replacement = Assert.Single(persisted);
        Assert.NotEqual(profile, replacement);
        Assert.Equal(replacement, HTTPUtil.AuthenticatedBrowserProfile);
        Assert.Equal(replacement.UserAgent, sent[1].UserAgent);
        Assert.All(sent, headers => Assert.Equal(Cookie, headers.Cookie));
    }

    private static Task<string> Get(HttpClient client) => HTTPUtil.GetWebSourceAsync(client, Url,
        delay: (_, _) => throw new InvalidOperationException("Unexpected network retry"), log: _ => { });

    private static HttpResponseMessage Response(HttpRequestMessage request, HttpStatusCode status)
        => new(status) { RequestMessage = request, Content = new StringContent("complete") };

    private sealed record Headers(string UserAgent, string? Cookie, string? Referer, bool BrowserHints)
    {
        internal static Headers Read(HttpRequestMessage request) => new(
            string.Join(' ', request.Headers.GetValues("User-Agent")),
            request.Headers.TryGetValues("Cookie", out var cookies) ? Assert.Single(cookies) : null,
            request.Headers.Referrer?.ToString(),
            request.Headers.Contains("sec-ch-ua") || request.Headers.Contains("Sec-Fetch-Site"));
    }
}
