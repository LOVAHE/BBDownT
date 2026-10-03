using System.CommandLine;
using System.Net;
using System.Text.Json;
using BBDownT.Core;
using BBDownT.Core.Util;

namespace BBDownT.Tests;

public class IntlCredentialTransportTests
{
    private const string FixtureCookie = "intl_session=synthetic-credential";
    private const string SignedUrl = "https://api.bilibili.tv/media/video.m4s?signature=a%2Bb%2Fc%3D&deadline=123";

    [Theory]
    [InlineData("https://api.bilibili.tv/intl/gateway/web/playurl", true)]
    [InlineData("https://passport.biliintl.com/x/intl/passport-login/web/sso/list", true)]
    [InlineData("http://api.bilibili.tv/intl/gateway/web/playurl", false)]
    [InlineData("https://api.bilibili.com/intl/gateway/web/playurl", false)]
    [InlineData("https://upos.bilivideo.com/media/video.m4s", false)]
    [InlineData("https://upos.bilivideo.cn/media/video.m4s", false)]
    [InlineData("https://subtitle.hdslb.com/subtitle.json", false)]
    [InlineData("https://proxy.biliapi.net/intl/gateway/web/playurl", false)]
    [InlineData("https://api.bilibili.tv.evil.test/intl/gateway/web/playurl", false)]
    [InlineData("https://synthetic-user@api.bilibili.tv/intl/gateway/web/playurl", false)]
    public void InternationalCookieDestination_RequiresTrustedHttpsAndRejectsOtherCredentialRealms(string url, bool allowed)
    {
        using var config = new CredentialConfigScope();

        Assert.Equal(allowed, HTTPUtil.ShouldSendCookie(url));
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        HTTPUtil.TryAddCookieHeader(request, url);
        Assert.Equal(allowed, request.Headers.Contains("Cookie"));
        if (allowed) Assert.Equal(FixtureCookie, Assert.Single(request.Headers.GetValues("Cookie")));
    }

    [Fact]
    public void OfficialInternationalHost_StillRequiresAllowlistMembership()
    {
        using var config = new CredentialConfigScope();
        Config.COOKIE_ALLOWED_DOMAINS = ["proxy.example.test"];

        Assert.False(HTTPUtil.ShouldSendCookie("https://api.bilibili.tv/intl/gateway/web/playurl"));
    }

    [Fact]
    public void AddingProxyToCookieAllowlist_DoesNotAuthorizeCredentialForwarding()
    {
        using var config = new CredentialConfigScope();

        Assert.False(HTTPUtil.ShouldSendCookie("https://proxy.example.test/intl/gateway/web/playurl"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExplicitParsingProxy_RequiresAndHonorsHostOrEpisodeHostConfiguration(bool episodeHost)
    {
        using var config = new CredentialConfigScope();
        if (episodeHost) Config.EPHOST = "proxy.example.test:8443";
        else Config.HOST = "https://proxy.example.test:8443";
        const string url = "https://proxy.example.test:8443/intl/gateway/web/playurl";

        Assert.True(HTTPUtil.ShouldSendCookie(url));
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        HTTPUtil.TryAddCookieHeader(request, url);
        Assert.Equal(FixtureCookie, Assert.Single(request.Headers.GetValues("Cookie")));
        Config.COOKIE_ALLOWED_DOMAINS = ["bilibili.tv", "biliintl.com"];
        Assert.False(HTTPUtil.ShouldSendCookie(url));
    }

    [Theory]
    [InlineData("proxy.example.test:8443", "http://proxy.example.test:8443/intl/gateway/web/playurl")]
    [InlineData("proxy.example.test:8443", "https://proxy.example.test/intl/gateway/web/playurl")]
    [InlineData("proxy.example.test:8443", "https://child.proxy.example.test:8443/intl/gateway/web/playurl")]
    [InlineData("proxy.example.test:8443", "https://proxy.example.test:8443/media/video.m4s")]
    [InlineData("proxy.example.test:8443", "https://proxy.example.test:8443/intl/gatewayevil/web/playurl")]
    [InlineData("http://proxy.example.test:8443", "https://proxy.example.test:8443/intl/gateway/web/playurl")]
    [InlineData("https://synthetic-user@proxy.example.test:8443", "https://proxy.example.test:8443/intl/gateway/web/playurl")]
    [InlineData("api.bilibili.com", "https://api.bilibili.com/intl/gateway/web/playurl")]
    public void ExplicitProxy_DoesNotAuthorizeHttpOtherPortsSubdomainsMediaPathsOrDomesticHosts(string configuredHost, string target)
    {
        using var config = new CredentialConfigScope();
        Config.HOST = configuredHost;

        Assert.False(HTTPUtil.ShouldSendCookie(target));
        using var request = new HttpRequestMessage(HttpMethod.Get, target);
        HTTPUtil.TryAddCookieHeader(request, target);
        Assert.False(request.Headers.Contains("Cookie"));
    }

    [Fact]
    public void EmptyCredential_NeverAddsAnInternationalCookieHeader()
    {
        using var config = new CredentialConfigScope();
        Config.COOKIE = "";
        const string url = "https://api.bilibili.tv/intl/gateway/web/playurl";

        Assert.False(HTTPUtil.ShouldSendCookie(url));
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        HTTPUtil.TryAddCookieHeader(request, url);
        Assert.False(request.Headers.Contains("Cookie"));
    }

    [Theory]
    [InlineData("http://api.bilibili.tv/media/video.m4s?signature=synthetic")]
    [InlineData(SignedUrl)]
    [InlineData("https://upos.bilivideo.com/media/video.m4s?signature=synthetic")]
    [InlineData("https://foreign.example.test/media/video.m4s?signature=synthetic")]
    public void ExplicitInternationalMedia_NeverAddsCookieEvenWhenGlobalRealmIsDomestic(string url)
    {
        using var config = new CredentialConfigScope();
        Config.COOKIE_IS_INTL = false;
        Assert.True(HTTPUtil.ShouldSendCookie(url));

        using var request = MediaRequestPolicy.CreateRequest(url, international: true, fromPosition: 0, toPosition: 99);
        var arguments = BBDownTAria2c.BuildDownloadArguments(url, "/in-memory/video.mp4", "", international: true);

        Assert.False(request.Headers.Contains("Cookie"));
        Assert.Equal(url, request.RequestUri!.OriginalString);
        Assert.NotNull(request.Headers.Range);
        Assert.DoesNotContain("Cookie:", arguments);
        Assert.DoesNotContain("synthetic-credential", arguments);
        Assert.Contains("\"" + url + "\"", arguments);
    }

    [Fact]
    public async Task SizeProbe_SendsAnActualCookieFreeRequestWhenInternationalModeIsExplicit()
    {
        using var config = new CredentialConfigScope();
        Config.COOKIE_IS_INTL = false;
        var requests = 0;
        using var client = new HttpClient(new StubHandler(request =>
        {
            requests++;
            Assert.Equal(SignedUrl, request.RequestUri!.OriginalString);
            Assert.False(request.Headers.Contains("Cookie"));
            Assert.Equal("https://www.bilibili.tv/", Assert.Single(request.Headers.GetValues("Referer")));
            Assert.Null(request.Headers.Range);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[42]) };
        }));

        Assert.Equal(42L, await BBDownTDownloadUtil.GetFileSizeAsync(SignedUrl, client, international: true));
        Assert.Equal(1, requests);
    }

    [Fact]
    public void DomesticMediaIdentity_PreservesAllowedCookieForwardingAndReferer()
    {
        using var config = new CredentialConfigScope();
        Config.COOKIE_IS_INTL = false;
        const string url = "https://upos.bilivideo.com/media/video.m4s";

        using var request = MediaRequestPolicy.CreateRequest(url, international: false);
        var arguments = BBDownTAria2c.BuildDownloadArguments(url, "/in-memory/video.mp4", "", international: false);

        Assert.Equal(FixtureCookie, Assert.Single(request.Headers.GetValues("Cookie")));
        Assert.Equal("https://www.bilibili.com/", request.Headers.Referrer!.AbsoluteUri);
        Assert.Contains("--header=\"Cookie: " + FixtureCookie + "\"", arguments);
        Assert.Contains("--header=\"Referer: https://www.bilibili.com\"", arguments);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void TransportHandlers_PreserveApiMediaAndDomesticCookieRedirectPolicies(bool useCookies, bool allowRedirects)
    {
        using var handler = HTTPUtil.CreateWebHandler(useCookies, allowRedirects);

        Assert.Equal(useCookies, handler.UseCookies);
        Assert.Equal(allowRedirects, handler.AllowAutoRedirect);
    }

    [Fact]
    public void ClientSelectors_IsolateInternationalApiAndMediaFromSharedDomesticCookieJar()
    {
        Assert.Same(HTTPUtil.IntlApiHttpClient, HTTPUtil.GetWebHttpClient(true));
        Assert.Same(HTTPUtil.IntlMediaHttpClient, HTTPUtil.GetMediaHttpClient(true));
        Assert.NotSame(HTTPUtil.GetWebHttpClient(true), HTTPUtil.GetMediaHttpClient(true));
        Assert.NotSame(HTTPUtil.AppHttpClient, HTTPUtil.GetWebHttpClient(true));
        Assert.NotSame(HTTPUtil.AppHttpClient, HTTPUtil.GetMediaHttpClient(true));
        Assert.Same(HTTPUtil.AppHttpClient, HTTPUtil.GetWebHttpClient(false));
        Assert.Same(HTTPUtil.AppHttpClient, HTTPUtil.GetMediaHttpClient(false));
    }

    [Theory]
    [InlineData("", true)]
    [InlineData("-intl", false)]
    [InlineData("-intl --force-http true", true)]
    [InlineData("-intl --force-http false", false)]
    [InlineData("--force-http false", false)]
    [InlineData("--force-http true -intl", true)]
    public async Task ForceHttpCliBinding_UsesRealmDefaultAndPreservesExplicitValues(string options, bool expected)
    {
        MyOption? bound = null;
        var command = CommandLineInvoker.GetRootCommand(option => { bound = option; return Task.CompletedTask; });
        var args = new List<string> { "https://www.bilibili.tv/play/2110869" };
        args.AddRange(options.Split(' ', StringSplitOptions.RemoveEmptyEntries));

        Assert.Equal(0, await command.InvokeAsync(args.ToArray()));

        Assert.NotNull(bound);
        Assert.Equal(expected, bound.ForceHttp);
    }

    [Theory]
    [InlineData("{}", true)]
    [InlineData("{\"UseIntlApi\":true}", false)]
    [InlineData("{\"ForceHttp\":true,\"UseIntlApi\":true}", true)]
    [InlineData("{\"UseIntlApi\":true,\"ForceHttp\":true}", true)]
    [InlineData("{\"ForceHttp\":false,\"UseIntlApi\":false}", false)]
    [InlineData("{\"UseIntlApi\":false,\"ForceHttp\":false}", false)]
    public void SourceGeneratedJsonBinding_UsesRealmDefaultWithoutDependingOnFieldOrder(string json, bool expected)
    {
        var option = JsonSerializer.Deserialize(json, SourceGenerationContext.Default.MyOption)!;
        var serverRequest = JsonSerializer.Deserialize(json, SourceGenerationContext.Default.ServeRequestOptions)!;

        Assert.Equal(expected, option.ForceHttp);
        Assert.Equal(expected, serverRequest.ForceHttp);
    }

    [Fact]
    public void BatchClone_PreservesImplicitForceHttpDefaultWithoutFreezingItsCurrentValue()
    {
        var option = new MyOption { UseIntlApi = true };

        var copy = option.ForBatchVideo("https://www.bilibili.tv/play/2110869/13287667", "/in-memory");

        Assert.False(copy.ForceHttp);
        Assert.False(option.ForceHttp);
        copy.UseIntlApi = false;
        Assert.True(copy.ForceHttp);
        Assert.False(option.ForceHttp);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void BatchClone_PreservesExplicitTransportOverrideWhenRealmChanges(bool forceHttp)
    {
        var option = new MyOption { ForceHttp = forceHttp, UseIntlApi = true };

        var copy = option.ForBatchVideo("https://www.bilibili.tv/play/2110869/13287667", "/in-memory");
        copy.UseIntlApi = false;

        Assert.Equal(forceHttp, copy.ForceHttp);
        Assert.Equal(forceHttp, option.ForceHttp);
    }

    [Fact]
    public void InternationalDefault_PreservesSignedHttpsResourceAcrossCdnAndRequestConstruction()
    {
        using var config = new CredentialConfigScope();
        var option = new MyOption { UseIntlApi = true };
        var video = new BBDownT.Core.Entity.Entity.Video { id = "64", dfn = "720P", codecs = "AVC", baseUrl = SignedUrl };

        Program.HandlePcdn(option, video, null);
        using var request = MediaRequestPolicy.CreateRequest(video.baseUrl, option.UseIntlApi, fromPosition: 0);
        var arguments = BBDownTAria2c.BuildDownloadArguments(video.baseUrl, "/in-memory/video.mp4", "", option.UseIntlApi);

        Assert.False(option.ForceHttp);
        Assert.Equal(SignedUrl, video.baseUrl);
        Assert.Equal(SignedUrl, request.RequestUri!.OriginalString);
        Assert.Equal("https", request.RequestUri.Scheme);
        Assert.Contains("\"" + SignedUrl + "\"", arguments);
        Assert.False(request.Headers.Contains("Cookie"));
        Assert.DoesNotContain("Cookie:", arguments);
    }

    private sealed class CredentialConfigScope : IDisposable
    {
        private readonly string cookie = Config.COOKIE;
        private readonly bool international = Config.COOKIE_IS_INTL;
        private readonly string host = Config.HOST;
        private readonly string episodeHost = Config.EPHOST;
        private readonly string[] allowedDomains = Config.COOKIE_ALLOWED_DOMAINS;

        internal CredentialConfigScope()
        {
            Config.COOKIE = FixtureCookie;
            Config.COOKIE_IS_INTL = true;
            Config.HOST = Config.EPHOST = "api.bilibili.com";
            Config.COOKIE_ALLOWED_DOMAINS =
            [
                "bilibili.com", "bilibili.tv", "biliintl.com", "bilivideo.com", "bilivideo.cn", "hdslb.com", "biliapi.net",
                "proxy.example.test", "foreign.example.test", "evil.test"
            ];
        }

        public void Dispose()
        {
            Config.COOKIE = cookie;
            Config.COOKIE_IS_INTL = international;
            Config.HOST = host;
            Config.EPHOST = episodeHost;
            Config.COOKIE_ALLOWED_DOMAINS = allowedDomains;
        }
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(respond(request));
    }
}
