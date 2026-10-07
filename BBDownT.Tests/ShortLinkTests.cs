using System.Net;
using BBDownT.Core.Util;

namespace BBDownT.Tests;

public class ShortLinkTests
{
    [Theory]
    [InlineData("https://bili.im/lpKlb9c", "https://www.bilibili.tv/en/play/1034193?s_locale=zh-Hans_CN&from=COPY&jump_type=0", "intl:1034193")]
    [InlineData("HTTP://WWW.BILI.IM/share", "https://www.bilibili.tv/en/play/1034193/13287667?from=COPY", "intl:1034193:13287667")]
    [InlineData("https://bili.im/share", "https://bilibili.tv/play/1034193?ep_id=13287667&from=COPY", "intl:1034193:13287667")]
    [InlineData("https://www.bili.im/share", "https://www.biliintl.com/zh/media/1034193?episode_id=13287667", "intl:1034193:13287667")]
    [InlineData("https://b23.tv/share", "https://www.bilibili.tv/en/play/1034193?from=COPY", "intl:1034193")]
    public async Task ShortLinks_ReuseInternationalLongLinkParsing(string input, string location, string expected)
    {
        var requests = new List<string>();

        var id = await BBDownTUtil.GetAvIdAsync(input, url =>
        {
            requests.Add(url);
            return Task.FromResult(location);
        });

        Assert.Equal(expected, id);
        Assert.Equal(new[] { input }, requests);
    }

    [Theory]
    [InlineData("https://b23.tv/share")]
    [InlineData("http://www.b23.tv/share")]
    [InlineData("HTTPS://B23.TV/share")]
    public async Task DomesticShortLinks_PreserveBvConversionAndCopyrightProbe(string input)
    {
        const long aid = 170001;
        var requests = new List<string>();
        var bvid = BilibiliBvConverter.Encode(aid);
        var copyrightProbe = $"https://www.bilibili.com/video/av{aid}/";

        var id = await BBDownTUtil.GetAvIdAsync(input, url =>
        {
            requests.Add(url);
            return Task.FromResult(url == input
                ? $"https://www.bilibili.com/video/{bvid}?share_source=copy_web"
                : "https://www.bilibili.com/bangumi/play/ep13287667");
        });

        Assert.Equal("ep:13287667", id);
        Assert.Equal(new[] { input, copyrightProbe }, requests);
    }

    [Theory]
    [InlineData("https://bili.im/share")]
    [InlineData("https://www.bili.im/share")]
    [InlineData("HTTP://WWW.BILI.IM/share")]
    [InlineData("http://b23.tv/share")]
    [InlineData("HTTPS://WWW.B23.TV/share")]
    public void ShortLinkRecognition_RequiresSupportedHttpHost(string input)
        => Assert.True(BBDownTUtil.IsShortLinkUri(input));

    [Theory]
    [InlineData("https://bili.im.evil.test/share")]
    [InlineData("https://evil.test/bili.im/share")]
    [InlineData("https://evil.test/?url=https://bili.im/share")]
    [InlineData("https://bili.im@evil.test/share")]
    [InlineData("https://user@bili.im/share")]
    [InlineData("https://b23.tv.evil.test/share")]
    [InlineData("https://evil.test/b23.tv/share")]
    [InlineData("https://evil.test/?url=https://b23.tv/share")]
    [InlineData("https://b23.tv@evil.test/share")]
    [InlineData("https://user@b23.tv/share")]
    [InlineData("ftp://bili.im/share")]
    [InlineData("ftp://b23.tv/share")]
    [InlineData("bili.im/share")]
    [InlineData("https://unsupported.test/share")]
    public void OtherHostsPathsQueriesAndUserInfo_DoNotTriggerShortLinkResolution(string input)
        => Assert.False(BBDownTUtil.IsShortLinkUri(input));

    [Theory]
    [InlineData("https://bili.im/share")]
    [InlineData("https://b23.tv/share")]
    public async Task UnchangedLocation_PreservesExistingRedirectLoopError(string input)
    {
        var error = await Assert.ThrowsAsync<Exception>(() =>
            BBDownTUtil.GetAvIdAsync(input, Task.FromResult));

        Assert.Equal("无限重定向", error.Message);
    }

    [Fact]
    public async Task FailedShortLinkRequest_PropagatesOriginalError()
    {
        var failure = new HttpRequestException("Redirect failed.");

        var error = await Assert.ThrowsAsync<HttpRequestException>(() =>
            BBDownTUtil.GetAvIdAsync("https://bili.im/share", _ => Task.FromException<string>(failure)));

        Assert.Same(failure, error);
    }

    [Fact]
    public void LocationClient_UsesInternationalCookieFreeRedirectClientAndPreservesApiClient()
    {
        Assert.Same(HTTPUtil.IntlMediaHttpClient, HTTPUtil.GetWebLocationHttpClient(international: true));
        Assert.Same(HTTPUtil.IntlApiHttpClient, HTTPUtil.GetWebHttpClient(international: true));
        Assert.Same(HTTPUtil.AppHttpClient, HTTPUtil.GetWebLocationHttpClient(international: false));
        using var handler = HTTPUtil.CreateWebHandler(useCookies: false, allowRedirects: true);
        Assert.False(handler.UseCookies);
        Assert.True(handler.AllowAutoRedirect);
    }

    [Fact]
    public async Task HeadResolution_PreservesFullFinalLocationAndSendsNoCredentials()
    {
        const string location = "https://www.bilibili.tv/en/play/1034193?episode_id=13287667&from=COPY";
        var requests = 0;
        using var client = new HttpClient(new StubHandler(request =>
        {
            requests++;
            Assert.Equal(HttpMethod.Head, request.Method);
            Assert.Equal("https://bili.im/lpKlb9c", request.RequestUri!.AbsoluteUri);
            Assert.False(request.Headers.Contains("Cookie"));
            Assert.Null(request.Headers.Authorization);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = new HttpRequestMessage(HttpMethod.Head, location)
            };
        }));

        var resolved = await HTTPUtil.GetWebLocationAsync(client, "https://bili.im/lpKlb9c",
            sendCookie: false, delay: (_, _) => Task.CompletedTask, log: _ => { });

        Assert.Equal(location, resolved);
        Assert.Equal("intl:1034193:13287667", await BBDownTUtil.GetAvIdAsync("https://bili.im/lpKlb9c", _ => Task.FromResult(resolved)));
        Assert.Equal(1, requests);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task HeadAccessDenial_DoesNotFallBackToGet(HttpStatusCode status)
    {
        var requests = 0;
        using var client = new HttpClient(new StubHandler(request =>
        {
            requests++;
            var response = new HttpResponseMessage(status) { RequestMessage = request };
            response.Headers.Location = new Uri("https://www.bilibili.tv/en/play/1034193");
            return response;
        }));

        await Assert.ThrowsAsync<HttpRequestException>(() => HTTPUtil.GetWebLocationAsync(client,
            "https://bili.im/share", sendCookie: false,
            delay: (_, _) => throw new InvalidOperationException("Must not retry a permanent status."), log: _ => { }));

        Assert.Equal(1, requests);
    }

    [Theory]
    [InlineData(HttpStatusCode.MethodNotAllowed)]
    [InlineData(HttpStatusCode.NotImplemented)]
    public async Task UnsupportedHead_FallsBackToGetWithoutReadingBodyOrSendingCredentials(HttpStatusCode status)
    {
        const string input = "https://bili.im/lpKlb9c";
        const string location = "https://www.bilibili.tv/en/play/1034193?s_locale=zh-Hans_CN&from=COPY&jump_type=0";
        var methods = new List<HttpMethod>();
        using var finalRequest = new HttpRequestMessage(HttpMethod.Get, location);
        var content = new MustNotReadContent();
        using var client = new HttpClient(new StubHandler(request =>
        {
            methods.Add(request.Method);
            Assert.Equal(input, request.RequestUri!.AbsoluteUri);
            Assert.False(request.Headers.Contains("Cookie"));
            Assert.Null(request.Headers.Authorization);
            return request.Method == HttpMethod.Head
                ? new HttpResponseMessage(status) { RequestMessage = request }
                : new HttpResponseMessage(HttpStatusCode.OK) { RequestMessage = finalRequest, Content = content };
        }));

        var resolved = await HTTPUtil.GetWebLocationAsync(client, input, sendCookie: false,
            delay: (_, _) => throw new InvalidOperationException("Must not retry unsupported HEAD."), log: _ => { });

        Assert.Equal(location, resolved);
        Assert.Equal(new[] { HttpMethod.Head, HttpMethod.Get }, methods);
        Assert.False(content.ReadAttempted);
        Assert.True(content.Disposed);
        Assert.Equal("intl:1034193", await BBDownTUtil.GetAvIdAsync(input, _ => Task.FromResult(resolved)));
    }

    [Fact]
    public async Task FallbackGet_RetriesTransientStatusAndReturnsFinalLocation()
    {
        const string location = "https://www.bilibili.tv/en/play/1034193?ep_id=13287667";
        var methods = new List<HttpMethod>();
        var waits = 0;
        using var finalRequest = new HttpRequestMessage(HttpMethod.Get, location);
        using var client = new HttpClient(new StubHandler(request =>
        {
            methods.Add(request.Method);
            var status = methods.Count switch
            {
                1 => HttpStatusCode.MethodNotAllowed,
                2 => HttpStatusCode.ServiceUnavailable,
                _ => HttpStatusCode.OK
            };
            return new HttpResponseMessage(status) { RequestMessage = status == HttpStatusCode.OK ? finalRequest : request };
        }));

        var resolved = await HTTPUtil.GetWebLocationAsync(client, "https://bili.im/lpKlb9c", sendCookie: false,
            delay: (_, _) => { waits++; return Task.CompletedTask; }, log: _ => { });

        Assert.Equal(location, resolved);
        Assert.Equal(new[] { HttpMethod.Head, HttpMethod.Get, HttpMethod.Get }, methods);
        Assert.Equal(1, waits);
    }

    [Fact]
    public async Task FallbackGetAccessDenial_PropagatesWithoutFurtherFallback()
    {
        var methods = new List<HttpMethod>();
        using var client = new HttpClient(new StubHandler(request =>
        {
            methods.Add(request.Method);
            return new HttpResponseMessage(request.Method == HttpMethod.Head
                ? HttpStatusCode.MethodNotAllowed : HttpStatusCode.Forbidden) { RequestMessage = request };
        }));

        var error = await Assert.ThrowsAsync<HttpRequestException>(() => HTTPUtil.GetWebLocationAsync(client,
            "https://bili.im/lpKlb9c", sendCookie: false,
            delay: (_, _) => throw new InvalidOperationException("Must not retry access denial."), log: _ => { }));

        Assert.Equal(HttpStatusCode.Forbidden, error.StatusCode);
        Assert.Equal(new[] { HttpMethod.Head, HttpMethod.Get }, methods);
    }

    private sealed class MustNotReadContent : HttpContent
    {
        internal bool ReadAttempted { get; private set; }
        internal bool Disposed { get; private set; }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            ReadAttempted = true;
            throw new InvalidOperationException("Location resolution must not read the response body.");
        }

        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(respond(request));
    }
}
