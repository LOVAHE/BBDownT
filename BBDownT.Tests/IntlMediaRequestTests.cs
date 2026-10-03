using System.Net;
using BBDownT.Core;
using static BBDownT.Core.Entity.Entity;

namespace BBDownT.Tests;

public class IntlMediaRequestTests
{
    private const string BackupHost = "upos-sz-mirrorcoso1.bilivideo.com";
    private const string SignedUrl = "https://intl-cdn.example.test/media/video.m4s?deadline=123&signature=a%2Bb%2Fc%3D";

    [Theory]
    [InlineData(SignedUrl)]
    [InlineData("https://intl-cdn.example.test:448/media/video.m4s?signature=a%2Bb%2Fc%3D")]
    [InlineData("https://intl-cdn.akamaized.net/media/video.m4s?signature=a%2Bb%2Fc%3D")]
    public void InternationalDefaultCdnPolicy_PreservesSignedVideoAndAudioUrls(string videoUrl)
    {
        var originalArea = Config.AREA;
        try
        {
            Config.AREA = "th";
            var option = new MyOption { UseIntlApi = true };
            var video = CreateVideo(videoUrl);
            var audio = CreateAudio(SignedUrl);
            Assert.True(option.ForceReplaceHost);
            Assert.False(option.AllowPcdn);

            Program.HandlePcdn(option, video, audio);

            Assert.Equal(videoUrl, video.baseUrl);
            Assert.Equal(SignedUrl, audio.baseUrl);
            Assert.Equal("", option.UposHost);
        }
        finally { Config.AREA = originalArea; }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void InternationalExplicitHostOverride_IsRetainedRegardlessOfForceReplaceDefault(bool forceReplace)
    {
        var option = new MyOption
        {
            UseIntlApi = true, ForceReplaceHost = forceReplace, UposHost = "custom-cdn.example.test"
        };
        var video = CreateVideo(SignedUrl);
        var audio = CreateAudio(SignedUrl.Replace("video.m4s", "audio.m4s"));

        Program.HandlePcdn(option, video, audio);

        Assert.Equal(SignedUrl.Replace("intl-cdn.example.test", "custom-cdn.example.test"), video.baseUrl);
        Assert.Equal(SignedUrl.Replace("intl-cdn.example.test", "custom-cdn.example.test").Replace("video.m4s", "audio.m4s"), audio.baseUrl);
        Assert.Equal("custom-cdn.example.test", option.UposHost);
    }

    [Fact]
    public void DomesticDefaultCdnPolicy_StillReplacesVideoAndAudioWithBackupHost()
    {
        var option = new MyOption();
        var video = CreateVideo(SignedUrl);
        var audio = CreateAudio(SignedUrl);

        Program.HandlePcdn(option, video, audio);

        Assert.Equal(BackupHost, option.UposHost);
        Assert.Equal(SignedUrl.Replace("intl-cdn.example.test", BackupHost), video.baseUrl);
        Assert.Equal(video.baseUrl, audio.baseUrl);
    }

    [Theory]
    [InlineData(false, BackupHost)]
    [InlineData(true, "pcdn.example.test:448")]
    public void DomesticPcdnPolicy_StillHonorsAllowPcdnWhenForcedReplacementIsDisabled(bool allowPcdn, string expectedHost)
    {
        const string url = "https://pcdn.example.test:448/media/video.m4s?signature=a%2Bb";
        var option = new MyOption { ForceReplaceHost = false, AllowPcdn = allowPcdn };
        var video = CreateVideo(url);
        var audio = CreateAudio(url);

        Program.HandlePcdn(option, video, audio);

        Assert.Equal(url.Replace("pcdn.example.test:448", expectedHost), video.baseUrl);
        Assert.Equal(video.baseUrl, audio.baseUrl);
        Assert.Equal("", option.UposHost);
    }

    [Theory]
    [InlineData("", "foreign.akamaized.net")]
    [InlineData("th", BackupHost)]
    public void DomesticForeignCdnPolicy_StillUsesConfiguredArea(string area, string expectedHost)
    {
        const string url = "https://foreign.akamaized.net/media/video.m4s?signature=a%2Bb";
        var originalArea = Config.AREA;
        try
        {
            Config.AREA = area;
            var option = new MyOption { ForceReplaceHost = false };
            var video = CreateVideo(url);
            var audio = CreateAudio(url);

            Program.HandlePcdn(option, video, audio);

            Assert.Equal(url.Replace("foreign.akamaized.net", expectedHost), video.baseUrl);
            Assert.Equal(video.baseUrl, audio.baseUrl);
        }
        finally { Config.AREA = originalArea; }
    }

    [Fact]
    public void DomesticExplicitHostOverride_StillWinsOverBackupHost()
    {
        var video = CreateVideo(SignedUrl);
        var option = new MyOption { UposHost = "custom-cdn.example.test" };

        Program.HandlePcdn(option, video, null);

        Assert.Equal(SignedUrl.Replace("intl-cdn.example.test", "custom-cdn.example.test"), video.baseUrl);
        Assert.Equal("custom-cdn.example.test", option.UposHost);
    }

    [Theory]
    [InlineData(true, "https://www.bilibili.tv/")]
    [InlineData(false, "https://www.bilibili.com")]
    public void RangeRequests_UseTheDownloadRealmOnUnrelatedCdnHosts(bool international, string referer)
    {
        using var request = MediaRequestPolicy.CreateRequest(SignedUrl, international, 500, 999);

        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal(SignedUrl, request.RequestUri!.OriginalString);
        Assert.Equal(new Uri(referer).AbsoluteUri, request.Headers.Referrer!.AbsoluteUri);
        Assert.Equal("Mozilla/5.0", Assert.Single(request.Headers.GetValues("User-Agent")));
        var range = Assert.Single(request.Headers.Range!.Ranges);
        Assert.Equal(500L, range.From);
        Assert.Equal(999L, range.To);
    }

    [Fact]
    public void UnboundedSingleThreadRequest_PreservesSignatureAndUsesInternationalReferer()
    {
        using var request = MediaRequestPolicy.CreateRequest(SignedUrl, true, 0);

        Assert.Equal(SignedUrl, request.RequestUri!.OriginalString);
        Assert.Equal("https://www.bilibili.tv/", Assert.Single(request.Headers.GetValues("Referer")));
        var range = Assert.Single(request.Headers.Range!.Ranges);
        Assert.Equal(0L, range.From);
        Assert.Null(range.To);
    }

    [Theory]
    [InlineData(true, "android_tv_yst")]
    [InlineData(false, "android_tv_yst")]
    [InlineData(true, "android")]
    [InlineData(false, "android")]
    public void AppAndTvMediaRequests_KeepTheirExistingLackOfReferer(bool international, string platform)
    {
        using var request = MediaRequestPolicy.CreateRequest(SignedUrl + "&platform=" + platform, international, 0, 99);

        Assert.False(request.Headers.Contains("Referer"));
        Assert.NotNull(request.Headers.Range);
        Assert.Equal("Mozilla/5.0", Assert.Single(request.Headers.GetValues("User-Agent")));
    }

    [Theory]
    [InlineData(true, "https://www.bilibili.tv/")]
    [InlineData(false, "https://www.bilibili.com")]
    public async Task SizeProbe_SendsTheActualRequestWithRealmRefererAndOriginalUrl(bool international, string referer)
    {
        var calls = 0;
        using var client = new HttpClient(new StubHandler(request =>
        {
            calls++;
            Assert.Equal(SignedUrl, request.RequestUri!.OriginalString);
            Assert.Equal(new Uri(referer).AbsoluteUri, request.Headers.Referrer!.AbsoluteUri);
            Assert.Equal("Mozilla/5.0", Assert.Single(request.Headers.GetValues("User-Agent")));
            Assert.Null(request.Headers.Range);
            return SizeResponse();
        }));

        var size = await BBDownTDownloadUtil.GetFileSizeAsync(SignedUrl, client, international);

        Assert.Equal(123L, size);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task DefaultSizeProbe_UsesExistingInternationalDownloadContext()
    {
        var originalInternational = Config.COOKIE_IS_INTL;
        try
        {
            Config.COOKIE_IS_INTL = true;
            using var client = new HttpClient(new StubHandler(request =>
            {
                Assert.Equal("https://www.bilibili.tv/", Assert.Single(request.Headers.GetValues("Referer")));
                return SizeResponse();
            }));

            Assert.Equal(123L, await BBDownTDownloadUtil.GetFileSizeAsync(SignedUrl, client));
        }
        finally { Config.COOKIE_IS_INTL = originalInternational; }
    }

    [Fact]
    public async Task AppSizeProbe_DoesNotAddAWebReferer()
    {
        using var client = new HttpClient(new StubHandler(request =>
        {
            Assert.False(request.Headers.Contains("Referer"));
            return SizeResponse();
        }));

        Assert.Equal(123L, await BBDownTDownloadUtil.GetFileSizeAsync(SignedUrl + "&platform=android", client, true));
    }

    [Theory]
    [InlineData(true, "https://www.bilibili.tv/")]
    [InlineData(false, "https://www.bilibili.com")]
    public void Aria2Arguments_UseRealmRefererAndPreserveSignedUrlAndOutputArguments(bool international, string referer)
    {
        var args = BBDownTAria2c.BuildDownloadArguments(SignedUrl, "/in-memory/output directory/video.mp4",
            "--max-tries=2", international);

        Assert.Contains("--header=\"Referer: " + referer + "\"", args);
        Assert.Contains("--header=\"User-Agent: Mozilla/5.0\"", args);
        Assert.Contains("\"" + SignedUrl + "\"", args);
        Assert.Contains("--max-tries=2", args);
        Assert.Contains("-d \"/in-memory/output directory\" -o \"video.mp4\"", args);
        Assert.Contains("--auto-file-renaming=false", args);
        Assert.Contains("--allow-overwrite=true", args);
    }

    [Theory]
    [InlineData("android_tv_yst")]
    [InlineData("android")]
    public void AppAndTvAria2Arguments_KeepTheirExistingLackOfReferer(string platform)
    {
        var url = SignedUrl + "&platform=" + platform;

        var args = BBDownTAria2c.BuildDownloadArguments(url, "/in-memory/video.mp4", "", true);

        Assert.DoesNotContain("Referer", args);
        Assert.Contains("--header=\"User-Agent: Mozilla/5.0\"", args);
        Assert.Contains("\"" + url + "\"", args);
    }

    [Fact]
    public void DefaultAria2Arguments_UseExistingInternationalDownloadContext()
    {
        var originalInternational = Config.COOKIE_IS_INTL;
        try
        {
            Config.COOKIE_IS_INTL = true;

            var args = BBDownTAria2c.BuildDownloadArguments(SignedUrl, "/in-memory/video.mp4", "");

            Assert.Contains("--header=\"Referer: https://www.bilibili.tv/\"", args);
            Assert.DoesNotContain("www.bilibili.com", args);
        }
        finally { Config.COOKIE_IS_INTL = originalInternational; }
    }

    private static Video CreateVideo(string url) => new() { id = "64", dfn = "720P", codecs = "AVC", baseUrl = url };
    private static Audio CreateAudio(string url) => new() { id = "30280", dfn = "30280", codecs = "M4A", baseUrl = url, bandwith = 192, dur = 9 };
    private static HttpResponseMessage SizeResponse() => new(HttpStatusCode.OK)
    {
        Content = new ByteArrayContent(new byte[123])
    };

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(respond(request));
    }
}
