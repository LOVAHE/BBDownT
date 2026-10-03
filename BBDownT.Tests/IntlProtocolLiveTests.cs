using BBDownT.Core.Fetcher;

namespace BBDownT.Tests;

// Explicit opt-in: BBDOWN_INTL_LIVE_TESTS=1 dotnet test --filter FullyQualifiedName~IntlProtocolLiveTests
// These probes use anonymous public endpoints, never read stored credentials,
// never authenticate an account, and never save cookies or QR images.
public class IntlProtocolLiveTests
{
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
        Assert.Equal(IntlQrStatus.Waiting, poll.Status);
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
}

public sealed class IntlLiveFactAttribute : FactAttribute
{
    public IntlLiveFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("BBDOWN_INTL_LIVE_TESTS") != "1")
            Skip = "设置 BBDOWN_INTL_LIVE_TESTS=1 才执行匿名国际站协议测试";
    }
}
