using BBDownT.Core.Fetcher;
using BBDownT.Core.Util;
using BBDownT.Core;

namespace BBDownT.Tests;

// Explicit opt-in: BBDOWN_INTL_LIVE_TESTS=1 dotnet test --filter FullyQualifiedName~IntlProtocolLiveTests
// These probes use anonymous public endpoints, never read stored credentials,
// never authenticate an account, and never save cookies or QR images.
public class IntlProtocolLiveTests
{
    [IntlLiveFact]
    public async Task Issue24_AppOnlyFreeEpisodeUsesAppPlaybackAndMapsGrantedStreams()
    {
        var host = Config.HOST;
        var token = Config.TOKEN;
        var cookie = Config.COOKIE;
        var international = Config.COOKIE_IS_INTL;
        try
        {
            Config.HOST = "api.bilibili.com";
            Config.TOKEN = "";
            Config.COOKIE = "";
            Config.COOKIE_IS_INTL = true;
            var result = await Parser.ExtractTracksAsync("intl:2309571:26222855", "", "", "26222855",
                false, true, true, "");
            Assert.NotEmpty(result.VideoTracks);
            Assert.NotEmpty(result.AudioTracks);
            Assert.Contains(result.VideoTracks, video => video.id == "16");
            Assert.All(result.VideoTracks, video => Assert.True(int.Parse(video.id) <= 16));
        }
        finally
        {
            Config.HOST = host;
            Config.TOKEN = token;
            Config.COOKIE = cookie;
            Config.COOKIE_IS_INTL = international;
        }
    }

    [IntlLiveFact]
    public async Task Issue24_AppOnlyPremiumEpisodeReportsPermissionAfterAppFallback()
    {
        var host = Config.HOST;
        var token = Config.TOKEN;
        var cookie = Config.COOKIE;
        var international = Config.COOKIE_IS_INTL;
        try
        {
            Config.HOST = "api.bilibili.com";
            Config.TOKEN = "";
            Config.COOKIE = "";
            Config.COOKIE_IS_INTL = true;
            var error = await Assert.ThrowsAsync<IntlApiException>(() => Parser.ExtractTracksAsync(
                "intl:2309571:26223058", "", "", "26223058", false, true, false, ""));
            Assert.Equal(10015002, error.ApiCode);
            Assert.Contains("权限不足", NetworkRetry.Describe(error));
        }
        finally
        {
            Config.HOST = host;
            Config.TOKEN = token;
            Config.COOKIE = cookie;
            Config.COOKIE_IS_INTL = international;
        }
    }

    [IntlLiveFact]
    public async Task OfficialQrProtocol_GeneratesATicketAndReportsWaitingForScan()
    {
        using var client = new IntlLoginClient();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var qr = await client.GenerateAsync(deadline.Token);
        Assert.Equal("https", qr.Url.Scheme);
        Assert.Equal("/h5/en/qrcode/login", qr.Url.AbsolutePath);
        Assert.NotEmpty(qr.Ticket);
        var poll = await client.PollAsync(qr.Ticket, deadline.Token);
        Assert.Equal(QrLoginStatus.Waiting, poll.Status);
    }

    [IntlLiveFact]
    public async Task Issue24_AnonymousCatalogueResolvesSeasonAndFirstEpisode()
    {
        // This delegate uses an isolated client rather than global cookies.
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        client.DefaultRequestHeaders.Referrer = new Uri("https://www.bilibili.tv/");
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Dart/3.8 (dart:io)");
        var info = await IntlBangumiInfoFetcher.FetchAsync("intl:2110869", url => client.GetStringAsync(url));
        Assert.Equal("LINK CLICK", info.Title);
        Assert.Equal(24, info.PagesInfo.Count);
        Assert.Equal("13287667", info.PagesInfo[0].epid);
        Assert.Equal("1", info.Index);
    }

    [IntlLiveFact]
    public async Task Issue24_AnonymousSubtitleVariantsSelectOneFormatPerTrack()
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        client.DefaultRequestHeaders.Referrer = new Uri("https://www.bilibili.tv/");
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Dart/3.8 (dart:io)");
        var resources = await SubUtil.GetIntlSubtitlesAsync("intl_13287667", "", "13287667", 1,
            url => client.GetStringAsync(url));
        var selected = SubtitleSelection.Filter(resources, new MyOption { UseIntlApi = true });

        Assert.Equal(4, resources.Count);
        Assert.Equal(2, selected.Count);
        Assert.Equal(new[] { "en", "th" }, selected.Select(subtitle => subtitle.lan).OrderBy(language => language));
        Assert.All(selected, subtitle => Assert.Equal("srt", SubtitleSelection.OutputFormat(subtitle)));
    }
}

public sealed class IntlLiveFactAttribute : FactAttribute
{
    public IntlLiveFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("BBDOWN_INTL_LIVE_TESTS") != "1")
            Skip = "设置 BBDOWN_INTL_LIVE_TESTS=1 才执行匿名国际站协议测试";
    }
}
