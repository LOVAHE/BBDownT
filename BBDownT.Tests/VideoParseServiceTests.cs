using System.Text.Json;
using BBDownT.Core;
using BBDownT.Core.Entity;
using static BBDownT.Core.Entity.Entity;

namespace BBDownT.Tests;

public class VideoParseServiceTests : IDisposable
{
    private readonly string workDir = Path.Combine(Path.GetTempPath(), "bbdownt-parse-" + Guid.NewGuid().ToString("N"));
    private readonly string originalCookie = Config.COOKIE;
    private readonly string originalToken = Config.TOKEN;

    public VideoParseServiceTests()
    {
        Directory.CreateDirectory(workDir);
    }

    public void Dispose()
    {
        Config.COOKIE = originalCookie;
        Config.TOKEN = originalToken;
        Directory.Delete(workDir, true);
    }

    [Fact]
    public async Task WebParse_ListsStreamsWithoutDownloadingOrChangingGlobalCredentials()
    {
        Config.COOKIE = "SESSDATA=global";
        Config.TOKEN = "running-task-token";
        var fake = new FakeBili { Account = new WebAccount(true, "user", true, "大会员") };
        var request = new ParseRequest { Url = "BV1xx", WorkDir = workDir };

        var result = await fake.Service().ParseAsync(request);

        Assert.Equal("WEB", result.Api);
        Assert.Equal(["120:AVC", "120:HEVC", "80:AVC"], result.Videos.Select(v => v.Key));
        Assert.Equal("120:AVC", result.DefaultVideo);
        Assert.Equal("30280", result.DefaultAudio);
        Assert.Equal("192K", result.Audios[0].Label);
        Assert.True(result.Account.WebLoggedIn);
        Assert.True(result.Account.ApiAuthenticated);
        Assert.DoesNotContain(result.Hints, h => h.Level == "warn");
        // 解析期间看到的是数据目录里保存的登录，结束后全局值不变
        Assert.Equal("SESSDATA=saved", fake.CookieSeenByFetch);
        Assert.Equal("", fake.TokenSeenByFetch);
        Assert.Equal("SESSDATA=global", Config.COOKIE);
        Assert.Equal("running-task-token", Config.TOKEN);
        Assert.Equal(1, fake.WebProfileConfigured);
        Assert.Empty(Directory.EnumerateFileSystemEntries(workDir));
    }

    [Fact]
    public async Task Parse_NeverBorrowsTheGlobalCookieOfAnotherTask()
    {
        // 另一个API任务带着自己的Cookie正在运行，数据目录里没有保存登录
        Config.COOKIE = "SESSDATA=other-task";
        var fake = new FakeBili { SavedCookie = null, Account = WebAccount.Anonymous };

        var result = await fake.Service().ParseAsync(new ParseRequest { Url = "BV1xx" });

        Assert.Equal("", fake.CookieSeenByFetch);
        Assert.Equal("", fake.CookieSeenByAccount);
        Assert.Equal(0, fake.WebProfileConfigured);
        Assert.Contains(result.Hints, h => h.Code == "not-logged-in");
        Assert.Equal("SESSDATA=other-task", Config.COOKIE);
    }

    [Fact]
    public async Task IntlParse_UsesTheSavedInternationalCookieWithoutTokensOrTheDomesticAccount()
    {
        var fake = new FakeBili();
        fake.Files["BBDownTIntl.data"] = "Cookie: SESSDATA=intl;bstar-web-lang=en";
        fake.Files["BBDownTApp.data"] = "access_token=app";

        // 与下载任务相同：国际站优先于 APP 接口
        var result = await fake.Service().ParseAsync(new ParseRequest { Url = "BV1xx", UseIntlApi = true, UseAppApi = true });

        Assert.Equal("INTL", result.RequestedApi);
        Assert.Equal("SESSDATA=intl; bstar-web-lang=en", fake.CookieSeenByFetch);
        Assert.Equal("", fake.TokenSeenByFetch);
        Assert.True(fake.IntlSeenByFetch);
        Assert.False(fake.AccountFetched);
        Assert.True(result.Account.ApiAuthenticated);
        Assert.False(Config.COOKIE_IS_INTL);
    }

    [Fact]
    public async Task Parse_UsesExplicitRequestCookieInsteadOfSavedLogin()
    {
        var fake = new FakeBili();

        await fake.Service().ParseAsync(new ParseRequest { Url = "BV1xx", Cookie = "SESSDATA=request" });

        Assert.Equal("SESSDATA=request", fake.CookieSeenByFetch);
        Assert.Equal("SESSDATA=request", fake.CookieSeenByAccount);
    }

    [Fact]
    public async Task AppParseWithoutToken_ExplainsWhyQualityIsLimited()
    {
        var fake = new FakeBili { Account = new WebAccount(true, "user", true, "大会员"), Videos = [StreamPinSelectionTests.Video("32", "HEVC", 200)] };

        var result = await fake.Service().ParseAsync(new ParseRequest { Url = "BV1xx", UseAppApi = true });

        Assert.Equal("APP", result.Api);
        Assert.False(result.Account.ApiAuthenticated);
        Assert.False(result.Account.AppTokenSaved);
        var hint = Assert.Single(result.Hints, h => h.Level == "warn");
        Assert.Equal("app-no-token", hint.Code);
        Assert.Contains("通常最高 480P，且一次只返回一种编码", hint.Message);
        // 未登录的提示已经说明只有一种编码，不再重复
        Assert.DoesNotContain(result.Hints, h => h.Code == "app-one-codec");
    }

    [Fact]
    public async Task TvParseWithoutToken_SaysVipQualitiesAreMissingButDoesNotClaim480P()
    {
        var fake = new FakeBili
        {
            Videos = [StreamPinSelectionTests.Video("80", "AVC", 1300), StreamPinSelectionTests.Video("80", "HEVC", 900)]
        };

        var result = await fake.Service().ParseAsync(new ParseRequest { Url = "BV1xx", UseTvApi = true });

        Assert.Equal("TV", result.Api);
        var hint = Assert.Single(result.Hints, h => h.Code == "tv-no-token");
        Assert.Contains("拿不到 1080P 及以上画质（未登录通常最高 720P）", hint.Message);
        Assert.DoesNotContain("480P", hint.Message);
        Assert.DoesNotContain("logintv", hint.Message);
        Assert.DoesNotContain(result.Hints, h => h.Code == "low-quality");
    }

    [Fact]
    public async Task AppParseWithSavedToken_UsesScopedTokenOnlyAndExplainsSingleCodec()
    {
        Config.TOKEN = "tv-token-of-running-task";
        var fake = new FakeBili
        {
            Files = { ["BBDownTApp.data"] = "access_token=app-token" },
            Videos = [StreamPinSelectionTests.Video("80", "HEVC", 900)]
        };

        var result = await fake.Service().ParseAsync(new ParseRequest { Url = "BV1xx", UseAppApi = true });

        Assert.True(result.Account.ApiAuthenticated);
        Assert.Equal("app-token", fake.TokenSeenByFetch);
        Assert.Equal("tv-token-of-running-task", Config.TOKEN);
        var codec = Assert.Single(result.Hints, h => h.Code == "app-one-codec");
        Assert.Contains("HEVC", codec.Message);
        Assert.Contains("视频编码优先", codec.Message);
    }

    [Fact]
    public async Task NotLoggedIn_InvalidCookie_AndVipOnlyQualities_AreReported()
    {
        var anonymous = new FakeBili { SavedCookie = null, Account = WebAccount.Anonymous };
        var expired = new FakeBili { SavedCookie = "SESSDATA=expired", Account = WebAccount.Anonymous };
        var notVip = new FakeBili
        {
            Account = new WebAccount(true, "user", false, null),
            Videos = [StreamPinSelectionTests.Video("80", "AVC", 1300)],
            WebJson = """{"code":0,"data":{"support_formats":[{"quality":120,"need_login":true,"need_vip":true},{"quality":80,"need_login":true}]}}"""
        };

        var anonymousResult = await anonymous.Service().ParseAsync(new ParseRequest { Url = "BV1xx" });
        var expiredResult = await expired.Service().ParseAsync(new ParseRequest { Url = "BV1xx" });
        var notVipResult = await notVip.Service().ParseAsync(new ParseRequest { Url = "BV1xx" });

        Assert.Contains(anonymousResult.Hints, h => h.Code == "not-logged-in");
        var invalid = Assert.Single(expiredResult.Hints, h => h.Code == "cookie-invalid");
        Assert.Contains("下载时会自动尝试刷新，若仍失败再扫码登录", invalid.Message);
        var vip = Assert.Single(notVipResult.Hints, h => h.Code == "need-vip");
        Assert.Contains("4K 超清", vip.Message);
        Assert.Equal([("120", false, true), ("80", true, false)],
            notVipResult.AcceptQualities.Select(q => (q.Id, q.Available, q.NeedVip)));
    }

    [Fact]
    public async Task AccountLookupFailure_DoesNotClaimTheLoginIsInvalid()
    {
        var fake = new FakeBili { AccountError = new HttpRequestException("network down") };

        var result = await fake.Service().ParseAsync(new ParseRequest { Url = "BV1xx" });

        Assert.Contains(result.Hints, h => h.Code == "account-unknown");
        Assert.DoesNotContain(result.Hints, h => h.Code is "cookie-invalid" or "not-logged-in");
    }

    [Fact]
    public async Task PinnedStreamsBecomeDefaultsAndPageCanBeChosen()
    {
        var fake = new FakeBili { PageCount = 3 };

        var result = await fake.Service().ParseAsync(new ParseRequest
        {
            Url = "BV1xx", Page = 2, VideoStream = "120:HEVC", AudioStream = "30232"
        });

        Assert.Equal(2, result.StreamsPage);
        Assert.Equal("2", fake.PageSeenByFetch?.index.ToString());
        Assert.Equal("120:HEVC", result.DefaultVideo);
        Assert.Equal("30232", result.DefaultAudio);
        Assert.Contains(result.Hints, h => h.Code == "streams-one-page");
        Assert.Equal([false, true, false], result.Pages.Select(p => p.Selected));
        // 没有指定分P时任务会下载全部分P
        Assert.Null(result.DownloadPages);
    }

    [Fact]
    public async Task DownloadPages_FollowTheSamePageSelectionAsTheTask()
    {
        var fake = new FakeBili { PageCount = 5 };

        var linked = await fake.Service().ParseAsync(new ParseRequest { Url = "https://www.bilibili.com/video/BV1xx?p=3" });
        var selected = await fake.Service().ParseAsync(new ParseRequest { Url = "BV1xx", SelectPage = "2,4-5" });

        Assert.Equal([3], linked.DownloadPages);
        Assert.Equal(3, linked.StreamsPage);
        Assert.Equal([2, 4, 5], selected.DownloadPages);
        Assert.Equal(2, selected.StreamsPage);
    }

    [Fact]
    public async Task InvalidSelectPage_IsRejectedWithPageCountBeforeFetchingStreams()
    {
        var fake = new FakeBili { PageCount = 1 };

        var error = await Assert.ThrowsAsync<ArgumentException>(
            () => fake.Service().ParseAsync(new ParseRequest { Url = "BV1xx", SelectPage = "1,2-3" }));

        Assert.Contains("共 1 个分P", error.Message);
        Assert.Contains("ALL", error.Message);
        Assert.DoesNotContain("Arg_ParamName_Name", error.Message);
        Assert.Null(fake.PageSeenByFetch);
    }

    [Fact]
    public async Task HugeSelectPageRange_IsCheckedAgainstThePageCountWithoutExpandingIt()
    {
        var fake = new FakeBili { PageCount = 3 };
        var watch = System.Diagnostics.Stopwatch.StartNew();

        await Assert.ThrowsAsync<ArgumentException>(
            () => fake.Service().ParseAsync(new ParseRequest { Url = "BV1xx", SelectPage = "1-2147483647" }));

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task InvalidPage_IsRejectedBeforeFetchingStreams()
    {
        var fake = new FakeBili { PageCount = 2 };

        await Assert.ThrowsAsync<ArgumentException>(() => fake.Service().ParseAsync(new ParseRequest { Url = "BV1xx", Page = 5 }));

        Assert.Null(fake.PageSeenByFetch);
    }

    [Theory]
    [InlineData("mid:123", "UP主空间")]
    [InlineData("favId:1:2", "收藏夹")]
    [InlineData("listBizId:42", "合集")]
    [InlineData("seriesBizId:42", "系列")]
    public async Task ListLinks_AreRejectedBeforeAnyListPageIsFetched(string aidOri, string name)
    {
        var fake = new FakeBili { AidOri = aidOri };

        var error = await Assert.ThrowsAsync<ParseRejectedException>(
            () => fake.Service().ParseAsync(new ParseRequest { Url = "https://space.bilibili.com/123" }));

        Assert.StartsWith(name + "链接是视频列表", error.Message);
        Assert.Contains("「下载」", error.Message);
        Assert.False(fake.InfoFetched);
        Assert.False(fake.AccountFetched);
    }

    [Fact]
    public async Task UnrecognizedInput_IsABadRequest()
    {
        var fake = new FakeBili { AidError = new Exception("输入有误") };

        var error = await Assert.ThrowsAsync<ArgumentException>(
            () => fake.Service().ParseAsync(new ParseRequest { Url = "hello" }));

        Assert.Equal(VideoParseService.UnrecognizedInputMessage, error.Message);
        Assert.False(fake.InfoFetched);
    }

    [Theory]
    [InlineData("http://127.0.0.1:8080/admin")]
    [InlineData("https://example.com/video/BV1xx411c7mD")]
    [InlineData("https://bilibili.com.evil.example/video/BV1xx411c7mD")]
    [InlineData("HTTPS://intranet.local/")]
    public async Task NonBilibiliLinks_AreRejectedWithoutTheServerFetchingThem(string url)
    {
        var fake = new FakeBili();

        var error = await Assert.ThrowsAsync<ArgumentException>(() => fake.Service().ParseAsync(new ParseRequest { Url = url }));

        Assert.Equal(VideoParseService.UnrecognizedInputMessage, error.Message);
        Assert.Equal(0, fake.AidRequests);
    }

    [Theory]
    [InlineData("https://www.bilibili.com/video/BV1xx411c7mD", true)]
    [InlineData("https://m.bilibili.com/video/BV1xx411c7mD", true)]
    [InlineData("https://b23.tv/abc", true)]
    [InlineData("https://bili.im/abc", true)]
    [InlineData("https://user@b23.tv/abc", false)]
    [InlineData("https://www.bilibili.tv/en/play/1/2", true)]
    [InlineData("https://www.biliintl.com/en/play/34613/341736", true)]
    [InlineData("https://biliintl.com.evil.example/en/play/1/2", false)]
    [InlineData("BV1xx411c7mD", true)]
    [InlineData("cheese/ep123", true)]
    [InlineData("https://b23.tv.evil.example/abc", false)]
    [InlineData("https://notbilibili.com/video/BV1xx411c7mD", false)]
    public void IsSupportedInput_OnlyAcceptsBilibiliHosts(string input, bool expected)
    {
        Assert.Equal(expected, VideoParseService.IsSupportedInput(input));
    }

    [Theory]
    [InlineData(typeof(JsonException))]
    [InlineData(typeof(KeyNotFoundException))]
    [InlineData(typeof(InvalidOperationException))]
    public async Task PagesThatAreNotVideos_AreABadRequestInsteadOfARawParserError(Type error)
    {
        // 如活动页：GetAvIdAsync 的兜底分支把它当作番剧页面解析失败；NativeAOT 下异常消息只剩资源键
        var fake = new FakeBili { AidError = (Exception)Activator.CreateInstance(error, "ExpectedJsonTokens LineNumber: 0")! };

        var e = await Assert.ThrowsAsync<ArgumentException>(
            () => fake.Service().ParseAsync(new ParseRequest { Url = "https://www.bilibili.com/festival/2021bnj" }));

        Assert.Equal(VideoParseService.UnrecognizedInputMessage, e.Message);
    }

    [Fact]
    public async Task NetworkErrorsWhileResolvingTheLink_StillPropagate()
    {
        var fake = new FakeBili { AidError = new HttpRequestException("connection refused") };

        await Assert.ThrowsAsync<HttpRequestException>(() => fake.Service().ParseAsync(new ParseRequest { Url = "https://b23.tv/abc" }));
    }

    [Fact]
    public async Task OldFavoritesLink_IsRecognizedAsAFavoritesListWithoutAnyRequest()
    {
        var aid = await BBDownTUtil.GetAvIdAsync("https://www.bilibili.com/medialist/detail/ml1052622027?type=1");

        Assert.Equal("favId:1052622027:", aid);
        Assert.Equal("收藏夹", VideoParseService.ListLinkName(aid));
    }

    [Theory]
    [InlineData("https://example.com/page")]
    [InlineData("http://127.0.0.1:1/x")]
    public async Task GetAvId_FallbackNeverFetchesNonBilibiliPages(string url)
    {
        var error = await Assert.ThrowsAsync<Exception>(() => BBDownTUtil.GetAvIdAsync(url));

        Assert.Equal("输入有误", error.Message);
    }

    [Fact]
    public async Task Parse_UsesDefaultHostsAndAreaInsteadOfThoseLeftByTheLastTask()
    {
        var (host, epHost, tvHost, area) = (Config.HOST, Config.EPHOST, Config.TVHOST, Config.AREA);
        try
        {
            // 上一个任务带着 Area=hk 和自定义Host运行过
            Config.AREA = "hk";
            Config.HOST = "custom.example";
            Config.EPHOST = "custom-ep.example";
            Config.TVHOST = "custom-tv.example";
            var fake = new FakeBili();

            await fake.Service().ParseAsync(new ParseRequest { Url = "BV1xx" });

            Assert.Equal(("", "api.bilibili.com", "api.bilibili.com", "api.snm0516.aisee.tv"), fake.NetworkSettingsSeenByFetch);
            Assert.Equal("hk", Config.AREA);
            Assert.Equal("custom.example", Config.HOST);
        }
        finally
        {
            (Config.HOST, Config.EPHOST, Config.TVHOST, Config.AREA) = (host, epHost, tvHost, area);
        }
    }

    [Fact]
    public async Task Parse_FlowsTheCancellationTokenToTheHttpHelpers()
    {
        using var cts = new CancellationTokenSource();
        var fake = new FakeBili();

        await fake.Service().ParseAsync(new ParseRequest { Url = "BV1xx" }, cts.Token);

        Assert.Equal(cts.Token, fake.CancellationSeenByFetch);
        Assert.Equal(CancellationToken.None, BBDownT.Core.Util.HTTPUtil.FlowCancellation);
    }

    [Fact]
    public async Task CancelledParse_DoesNotReturnAResultEvenIfSubtitleErrorsWereSwallowed()
    {
        using var cts = new CancellationTokenSource();
        var fake = new FakeBili { OnSubtitles = cts.Cancel };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => fake.Service().ParseAsync(new ParseRequest { Url = "BV1xx" }, cts.Token));
    }

    [Fact]
    public async Task CourseLink_ParsesOnlyOneEpisodeFromASingleSeasonRequest()
    {
        var fake = new FakeBili { AidOri = "cheese:7", PageCount = 40, Cheese = true };

        var result = await fake.Service().ParseAsync(new ParseRequest { Url = "https://www.bilibili.com/cheese/play/ss1" });

        Assert.Equal("cheese", result.Kind);
        Assert.Equal(1, fake.InfoFetchCount);
        Assert.Equal(1, fake.TrackFetchCount);
        Assert.Equal(40, result.PageCount);
    }

    [Fact]
    public async Task RequestIsNotMutatedByParse()
    {
        var fake = new FakeBili { FlipTvToWeb = true };
        var request = new ParseRequest { Url = "BV1xx", UseTvApi = true };

        var result = await fake.Service().ParseAsync(request);

        Assert.True(request.UseTvApi);
        Assert.Equal("TV", result.RequestedApi);
        Assert.Equal("WEB", result.Api);
        Assert.Contains(result.Hints, h => h.Code == "steingate-web");
    }

    [Fact]
    public void ParseResponse_UsesSourceGeneratedContract()
    {
        var response = VideoParseService.Build(new MyOption { Url = "BV1xx" }, FakeBili.Info(1), "1", FakeBili.Info(1).PagesInfo[0],
            new ParsedResult { VideoTracks = [StreamPinSelectionTests.Video("80", "AVC", 1300)], AudioTracks = [StreamPinSelectionTests.Audio("30280", 190)] },
            new(), new(), [], new ParseAccount(true, "u", true, "大会员", false, false, true), "c", "WEB", "WEB", 12,
            downloadPages: [1]);

        var json = JsonSerializer.Serialize(response, AppJsonSerializerContext.Default.ParseResponse);
        using var doc = JsonDocument.Parse(json);

        Assert.Equal("80:AVC", doc.RootElement.GetProperty("Videos")[0].GetProperty("Key").GetString());
        Assert.Equal(VideoParseService.EstimateSize(100, 1300), doc.RootElement.GetProperty("Videos")[0].GetProperty("Size").GetInt64());
        Assert.True(doc.RootElement.GetProperty("Videos")[0].GetProperty("SizeIsEstimate").GetBoolean());
        Assert.Equal("normal", doc.RootElement.GetProperty("Audios")[0].GetProperty("Kind").GetString());
        Assert.Equal("BV1xx", doc.RootElement.GetProperty("Url").GetString());
        Assert.Equal(1, doc.RootElement.GetProperty("DownloadPages")[0].GetInt32());
    }

    private sealed class FakeBili
    {
        public string AidOri { get; set; } = "1";
        public Exception? AidError { get; set; }
        public int PageCount { get; set; } = 1;
        public bool Cheese { get; set; }
        public bool FlipTvToWeb { get; set; }
        public string? SavedCookie { get; set; } = "SESSDATA=saved";
        public WebAccount Account { get; set; } = new(true, "user", true, "大会员");
        public Exception? AccountError { get; set; }
        public List<Video> Videos { get; set; } =
        [
            StreamPinSelectionTests.Video("80", "AVC", 1300),
            StreamPinSelectionTests.Video("120", "HEVC", 3400),
            StreamPinSelectionTests.Video("120", "AVC", 5000),
        ];
        public string WebJson { get; set; } = "{}";
        public Dictionary<string, string> Files { get; } = [];
        public string? CookieSeenByFetch { get; private set; }
        public string? CookieSeenByAccount { get; private set; }
        public string? TokenSeenByFetch { get; private set; }
        public bool IntlSeenByFetch { get; private set; }
        public Page? PageSeenByFetch { get; private set; }
        public bool InfoFetched => InfoFetchCount > 0;
        public int InfoFetchCount { get; private set; }
        public int TrackFetchCount { get; private set; }
        public bool AccountFetched { get; private set; }
        public int WebProfileConfigured { get; private set; }
        public int AidRequests { get; private set; }
        public (string Area, string Host, string EpHost, string TvHost) NetworkSettingsSeenByFetch { get; private set; }
        public CancellationToken CancellationSeenByFetch { get; private set; }
        public Action? OnSubtitles { get; set; }

        public static VInfo Info(int pages, bool cheese = false) => new()
        {
            Title = "title",
            Desc = "",
            Pic = "cover.jpg",
            PubTime = 1,
            IsBangumi = cheese,
            IsCheese = cheese,
            PagesInfo = Enumerable.Range(1, pages).Select(i => new Page(i, "1", (100 + i).ToString(), "", $"P{i}", 100, "", 1)).ToList()
        };

        public VideoParseService Service() => new(new VideoParseService.Dependencies(
            _ =>
            {
                AidRequests++;
                return AidError is null ? Task.FromResult(AidOri) : Task.FromException<string>(AidError);
            },
            (option, _) =>
            {
                InfoFetchCount++;
                var api = Program.GetApiType(option);
                if (FlipTvToWeb && option.UseTvApi)
                {
                    option.UseTvApi = false;
                    api = "WEB";
                }
                return Task.FromResult(("1", Info(PageCount, Cheese), api));
            },
            (_, _, page, _) =>
            {
                TrackFetchCount++;
                NetworkSettingsSeenByFetch = (Config.AREA, Config.HOST, Config.EPHOST, Config.TVHOST);
                CancellationSeenByFetch = BBDownT.Core.Util.HTTPUtil.FlowCancellation;
                CookieSeenByFetch = Config.COOKIE;
                TokenSeenByFetch = Config.TOKEN;
                IntlSeenByFetch = Config.COOKIE_IS_INTL;
                PageSeenByFetch = page;
                return Task.FromResult(new ParsedResult
                {
                    WebJsonString = WebJson,
                    VideoTracks = [.. Videos],
                    AudioTracks = [StreamPinSelectionTests.Audio("30216", 64), StreamPinSelectionTests.Audio("30280", 190), StreamPinSelectionTests.Audio("30232", 130)]
                });
            },
            (_, _) =>
            {
                OnSubtitles?.Invoke();
                return Task.FromResult(new List<Subtitle>());
            },
            () =>
            {
                AccountFetched = true;
                CookieSeenByAccount = Config.COOKIE;
                return AccountError is null ? Task.FromResult(Account) : Task.FromException<WebAccount>(AccountError);
            },
            name => name == "BBDownT.data" ? SavedCookie : Files.GetValueOrDefault(name),
            () => WebProfileConfigured++));
    }
}
