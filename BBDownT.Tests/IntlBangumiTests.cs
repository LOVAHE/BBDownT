using System.Text.Json;
using System.Text.Json.Nodes;
using System.Web;
using BBDownT.Core;
using BBDownT.Core.Entity;
using BBDownT.Core.Fetcher;
using BBDownT.Core.Util;

namespace BBDownT.Tests;

public class IntlBangumiTests
{
    [Theory]
    [InlineData("avc1.64001f", "AVC")]
    [InlineData("hev1.1.6.L120", "HEVC")]
    [InlineData("hvc1.1.6.L120", "HEVC")]
    [InlineData("av01.0.08M.08", "AV1")]
    public void CurrentWebCodecStringsAndStreamInfoQuality_MapWithoutAppFields(string codec, string expected)
    {
        var response = PlayResponse();
        var stream = response["data"]!["playurl"]!["video"]![0]!;
        stream["stream_info"]!["quality"] = 80;
        var resource = stream["video_resource"]!.AsObject();
        resource.Remove("quality");
        resource.Remove("codec_id");
        resource["codecs"] = codec;
        using var document = JsonDocument.Parse(response.ToJsonString());
        var result = new ParsedResult();

        PlayResponseMapper.MapIntlWeb(document.RootElement.GetProperty("data").GetProperty("playurl"), result, _ => false);

        var video = Assert.Single(result.VideoTracks);
        Assert.Equal("80", video.id);
        Assert.Equal(expected, video.codecs);
    }

    [Theory]
    [InlineData(10004001)]
    [InlineData(10004004)]
    [InlineData(10004005)]
    public async Task SubtitlePermissions_PropagateThroughFetchWrapperWithoutAppFallback(int code)
    {
        var calls = 0;
        var response = new JsonObject { ["code"] = code }.ToJsonString();
        await Assert.ThrowsAsync<IntlApiException>(() => SubUtil.GetIntlSubtitlesAsync(
            "intl_13287667", "", "13287667", 1, _ =>
            {
                calls++;
                return Task.FromResult(response);
            }));
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task LegacyAppRegionRestrictedCatalogue_FallsBackToCurrentWebMetadata()
    {
        var responses = new Queue<string>([
            """
            {"code":0,"result":{"season_id":2110869,"title":"LINK CLICK","evaluate":"Description",
              "cover":"https://example.test/cover.jpg","publish":{"pub_time":""},"modules":[],
              "limit":{"content":"版权地区限制"}}}
            """,
            Season().ToJsonString(), Episodes().ToJsonString()
        ]);
        var requests = new List<Uri>();
        var info = await IntlBangumiInfoFetcher.FetchAsync("ep:13287667", url =>
        {
            requests.Add(new Uri(url));
            return Task.FromResult(responses.Dequeue());
        });
        Assert.Equal(3, requests.Count);
        Assert.EndsWith("/ogv/view/app/season", requests[0].AbsolutePath);
        Assert.EndsWith("/ogv/play/season_info", requests[1].AbsolutePath);
        Assert.Equal(3, info.PagesInfo.Count);
        Assert.Equal("1", info.Index);
        Assert.Equal("intl_13287667", info.PagesInfo[0].DownloadId);
    }

    [Fact]
    public async Task NativeInternationalEpisodes_HaveDistinctArchiveKeysWithoutDomesticIds()
    {
        var info = await FetchInfo("intl:2110869", Season(), Episodes());
        var archives = new List<string>();
        var runner = new PageDownloadRunner(_ => false, archives.Add, _ => Task.CompletedTask, _ => { });
        await runner.RunAsync(info.PagesInfo, true, 0, _ => Task.FromResult(DownloadPageOutcome.Completed), info.PagesInfo);
        Assert.Equal(new[] { "intl_13287667:", "intl_13287745:", "intl_13287800:" }, archives);
    }

    [Fact]
    public void InternationalCookies_StayOutOfDomesticRequestsAndKeepExplicitProxySupport()
    {
        var originalCookie = Config.COOKIE;
        var originalMode = Config.COOKIE_IS_INTL;
        var originalDomains = Config.COOKIE_ALLOWED_DOMAINS;
        var originalHost = Config.HOST;
        try
        {
            Config.COOKIE = "session=synthetic";
            Config.COOKIE_IS_INTL = true;
            Config.HOST = "proxy.example.test";
            Config.COOKIE_ALLOWED_DOMAINS = ["bilibili.com", "bilibili.tv", "biliintl.com", "proxy.example.test"];
            Assert.False(HTTPUtil.ShouldSendCookie("https://api.bilibili.com/x/web-interface/view"));
            Assert.False(HTTPUtil.ShouldSendCookie("https://comment.bilibili.com/.xml"));
            Assert.True(HTTPUtil.ShouldSendCookie("https://api.bilibili.tv/intl/gateway/web/playurl"));
            Assert.True(HTTPUtil.ShouldSendCookie("https://api.biliintl.com/intl/gateway/web/v2/subtitle"));
            Assert.True(HTTPUtil.ShouldSendCookie("https://proxy.example.test/intl/gateway/web/playurl"));
            Assert.Equal("session=synthetic", HTTPUtil.GetCookieHeaderValue("https://api.bilibili.tv/intl/gateway/web/v2/ogv/play/episodes"));
            Config.COOKIE_IS_INTL = false;
            Assert.True(HTTPUtil.ShouldSendCookie("https://api.bilibili.com/x/web-interface/view"));
        }
        finally
        {
            Config.COOKIE = originalCookie;
            Config.COOKIE_IS_INTL = originalMode;
            Config.COOKIE_ALLOWED_DOMAINS = originalDomains;
            Config.HOST = originalHost;
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void InternationalDanmaku_IsRejectedBeforeDomesticCidRequests(bool only)
    {
        var error = Assert.Throws<ArgumentException>(() => Program.SetUpWork(new MyOption
        {
            UseIntlApi = true, DownloadDanmaku = !only, DanmakuOnly = only
        }));
        Assert.Contains("国际站弹幕", error.Message);
    }

    [Theory]
    [InlineData("https://www.bilibili.tv/play/2110869", "intl:2110869")]
    [InlineData("https://bilibili.tv/en/play/2110869/13287667", "intl:2110869:13287667")]
    [InlineData("https://www.biliintl.com/th/play/2110869/13287745/", "intl:2110869:13287745")]
    [InlineData("http://biliintl.com/play/2110869", "intl:2110869")]
    [InlineData("https://WWW.BILIBILI.TV/id/play/2110869?ep_id=13287745#episode", "intl:2110869:13287745")]
    [InlineData("https://bilibili.tv/play/2110869?episode_id=13287667&from=search", "intl:2110869:13287667")]
    [InlineData("https://bilibili.tv/play/2110869/13287667?ep_id=13287745#player", "intl:2110869:13287667")]
    [InlineData("https://bilibili.tv/play/2110869?from=search#13287667", "intl:2110869")]
    public async Task InternationalUrls_PreserveSeasonAndOptionalEpisodeWithoutNetwork(string url, string expected)
    {
        Assert.True(IntlBangumiUrl.TryParse(url, out var id));
        Assert.Equal(expected, id);
        Assert.Equal(expected, await BBDownTUtil.GetAvIdAsync(url));
    }

    [Theory]
    [InlineData("https://bilibili.tv.example.test/play/2110869")]
    [InlineData("https://example.test/bilibili.tv/play/2110869")]
    [InlineData("https://bilibili.tv@example.test/play/2110869")]
    [InlineData("ftp://bilibili.tv/play/2110869")]
    [InlineData("https://bilibili.tv/play/not-a-number")]
    [InlineData("https://bilibili.tv/play/2110869/13287667/extra")]
    [InlineData("https://bilibili.tv/play/2110869?ep_id=invalid")]
    [InlineData("https://www.bilibili.com/bangumi/play/ep13287667")]
    [InlineData("https://www.bilibili.com/video/BV17x411w7KC")]
    public void OtherHostsAndMalformedPaths_AreNotInternationalUrls(string input)
    {
        Assert.False(IntlBangumiUrl.TryParse(input, out var id));
        Assert.Equal("", id);
    }

    [Theory]
    [InlineData("ep13287667")]
    [InlineData("https://www.bilibili.com/bangumi/play/ep13287667?from=search")]
    public async Task DomesticEpisodeInputs_KeepTheirExistingIds(string input)
    {
        Assert.Equal("ep:13287667", await BBDownTUtil.GetAvIdAsync(input));
    }

    [Fact]
    public void FetcherSelection_RequiresIntlFlagAndPreservesDomesticTypes()
    {
        Assert.IsType<IntlBangumiInfoFetcher>(FetcherFactory.CreateFetcher("intl:2110869", true));
        Assert.IsType<IntlBangumiInfoFetcher>(FetcherFactory.CreateFetcher("ep:13287667", true));
        Assert.Contains("-intl", Assert.Throws<ArgumentException>(
            () => FetcherFactory.CreateFetcher("intl:2110869:13287667", false)).Message);
        Assert.IsType<BangumiInfoFetcher>(FetcherFactory.CreateFetcher("ep:13287667", false));
        Assert.IsType<NormalInfoFetcher>(FetcherFactory.CreateFetcher("BV17x411w7KC", false));
    }

    [Fact]
    public async Task WebSeasonAndSections_MapMetadataAndNamespacedPagesInSectionOrder()
    {
        var requests = new List<Uri>();
        var result = await FetchInfo("intl:2110869:13287745", Season(), Episodes(), requests);

        Assert.Collection(requests,
            request => Assert.Equal("/intl/gateway/web/v2/ogv/play/season_info", request.AbsolutePath),
            request => Assert.Equal("/intl/gateway/web/v2/ogv/play/episodes", request.AbsolutePath));
        Assert.All(requests, request =>
        {
            var query = HttpUtility.ParseQueryString(request.Query);
            Assert.Equal("2110869", query["season_id"]);
            Assert.Equal("web", query["platform"]);
        });
        Assert.Equal("Season", result.Title);
        Assert.Equal("Description", result.Desc);
        Assert.Equal("https://example.test/horizontal.jpg", result.Pic);
        Assert.True(result.IsBangumi);
        Assert.True(result.IsBangumiEnd);
        Assert.False(result.IsCheese);
        Assert.Equal("2", result.Index);
        Assert.Equal(new[] { "13287667", "13287745", "13287800" }, result.PagesInfo.Select(page => page.epid));
        Assert.Equal(new[] { 1, 2, 3 }, result.PagesInfo.Select(page => page.index));
        Assert.All(result.PagesInfo, page =>
        {
            Assert.Equal("", page.aid);
            Assert.Equal("", page.cid);
            Assert.Equal("", page.bvid);
            Assert.Equal("intl_" + page.epid, page.DownloadId);
        });
        Assert.Equal("E1 - First", result.PagesInfo[0].title);
        Assert.Equal("E2 Second", result.PagesInfo[1].title);
        Assert.Equal(1704067200L, result.PagesInfo[0].pubTime);
        Assert.Equal("https://example.test/13287667.jpg", result.PagesInfo[0].cover);
    }

    [Fact]
    public async Task ExplicitEpisodeSelection_WinsOverSeasonFirstEpisode()
    {
        var result = await FetchInfo("intl:2110869:13287800", Season("13287745"), Episodes());

        Assert.Equal("3", result.Index);
        Assert.Equal(3, result.PagesInfo.Count);
    }

    [Theory]
    [InlineData("13287745", "2")]
    [InlineData(null, "1")]
    [InlineData("99999999", "1")]
    public async Task SeasonOnlySelection_UsesAvailableFirstEpisodeOrFirstListedFallback(string? firstEpisode, string expectedIndex)
    {
        var result = await FetchInfo("intl:2110869", Season(firstEpisode), Episodes());

        Assert.Equal(expectedIndex, result.Index);
    }

    [Fact]
    public async Task MissingHorizontalCover_UsesVerticalCover()
    {
        var season = Season();
        season["data"]!["season"]!["horizontal_cover"] = "";

        var result = await FetchInfo("intl:2110869", season, Episodes());

        Assert.Equal("https://example.test/vertical.jpg", result.Pic);
    }

    [Fact]
    public async Task PremiumEpisodes_RemainListedAndDuplicateEpisodeIdsAreCollapsed()
    {
        var episodes = Episodes();
        episodes["data"]!["sections"]![1]!["episodes"]!.AsArray().Add(Episode("13287667", "Duplicate"));

        var result = await FetchInfo("intl:2110869:13287745", Season(), episodes);

        Assert.Equal(3, result.PagesInfo.Count);
        Assert.Equal("13287745", result.PagesInfo[1].epid);
        Assert.Equal("2", result.Index);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"sections\":null}")]
    [InlineData("{\"sections\":[{\"episodes\":[]}]} ")]
    public async Task MissingOrEmptyEpisodes_ReportAnErrorInsteadOfAnEmptyDownload(string data)
    {
        var response = new JsonObject { ["code"] = 0, ["data"] = JsonNode.Parse(data) };

        await Assert.ThrowsAsync<InvalidDataException>(() => FetchInfo("intl:2110869", Season(), response));
    }

    [Fact]
    public async Task ExplicitEpisodeOutsideSeason_IsRejected()
    {
        var error = await Assert.ThrowsAsync<InvalidDataException>(
            () => FetchInfo("intl:2110869:99999999", Season(), Episodes()));

        Assert.Contains("不在此番剧", error.Message);
    }

    [Fact]
    public async Task SeasonApiError_StopsBeforeFetchingEpisodes()
    {
        var requests = 0;
        var error = await Assert.ThrowsAnyAsync<InvalidOperationException>(() => IntlBangumiInfoFetcher.FetchAsync(
            "intl:2110869", _ =>
            {
                requests++;
                return Task.FromResult("{\"code\":10004001}");
            }));

        Assert.Equal(1, requests);
        Assert.Contains("地区限制", error.Message);
    }

    [Theory]
    [InlineData(10004001, "地区限制")]
    [InlineData(10015001, "地区限制")]
    [InlineData(10004004, "Premium")]
    [InlineData(10004006, "Premium")]
    [InlineData(10004005, "登录")]
    [InlineData(-404, "不存在")]
    public void ApiPermissionAndNotFoundCodes_ProduceActionableErrors(int code, string expectedMessage)
    {
        using var document = JsonDocument.Parse(new JsonObject
        {
            ["code"] = code,
            ["message"] = "upstream message"
        }.ToJsonString());

        var error = Assert.ThrowsAny<InvalidOperationException>(() => IntlBangumiWebApi.EnsureSuccess(document.RootElement));

        Assert.Contains(expectedMessage, error.Message);
        Assert.Contains(code.ToString(), error.Message);
    }

    [Theory]
    [InlineData("0", "64")]
    [InlineData("80", "80")]
    public async Task WebPlayRequest_UsesEpisodeIdAndWebPlatformWithoutDomesticIds(string qn, string expectedQn)
    {
        Uri? request = null;
        const string response = "{\"code\":0,\"data\":{\"playurl\":{}}}";
        var result = await Parser.GetPlayJsonAsync("", "intl:2110869:13287667", "", "", "13287667",
            false, true, false, qn, fetchWeb: url =>
            {
                request = new Uri(url);
                return Task.FromResult(response);
            });

        Assert.Equal(response, result);
        Assert.Equal("/intl/gateway/web/playurl", request!.AbsolutePath);
        var query = HttpUtility.ParseQueryString(request.Query);
        Assert.Equal("13287667", query["ep_id"]);
        Assert.Equal(expectedQn, query["qn"]);
        Assert.Equal("web", query["platform"]);
        Assert.Null(query["aid"]);
        Assert.Null(query["cid"]);
        Assert.Null(query["prefer_code_type"]);
    }

    [Fact]
    public async Task LegacyInternationalPlayRequest_KeepsAndroidEndpointWhenAidExists()
    {
        Uri? request = null;
        await Parser.GetPlayJsonAsync("", "ep:13287667", "170001", "222", "13287667", false, true, false,
            fetchWeb: url => { request = new Uri(url); return Task.FromResult("{}"); });

        Assert.Equal("/intl/gateway/v2/ogv/playurl", request!.AbsolutePath);
        var query = HttpUtility.ParseQueryString(request.Query);
        Assert.Equal("170001", query["aid"]);
        Assert.Equal("222", query["cid"]);
        Assert.Equal("android", query["platform"]);
        Assert.Equal("0", query["prefer_code_type"]);
    }

    [Fact]
    public async Task WebPlayResponse_MapsOfficialResourceShapeAndDoesNotRequestAppVariant()
    {
        var calls = 0;
        var response = PlayResponse().ToJsonString();
        var result = await Extract(response, () => calls++);

        Assert.Equal(1, calls);
        Assert.Equal(response, result.WebJsonString);
        var video = Assert.Single(result.VideoTracks);
        Assert.Equal("64", video.id);
        Assert.Equal("720P HD", video.dfn);
        Assert.Equal("AVC", video.codecs);
        Assert.Equal("1280x720", video.res);
        Assert.Equal("30", video.fps);
        Assert.Equal(9, video.dur);
        Assert.Equal(1500L, video.bandwith);
        Assert.Equal(4096d, video.size);
        var audio = Assert.Single(result.AudioTracks);
        Assert.Equal("30280", audio.id);
        Assert.Equal("M4A", audio.codecs);
        Assert.Equal(192L, audio.bandwith);
        Assert.Equal(9, audio.dur);
        Assert.Empty(result.Clips);
    }

    [Fact]
    public void WebResourceUrlSelection_UsesBackupWhenPrimaryIsExcluded()
    {
        var response = PlayResponse();
        response["data"]!["playurl"]!["video"]![0]!["video_resource"]!["url"] = "https://cdn.test:448/video.m4s";
        using var document = JsonDocument.Parse(response.ToJsonString());
        var result = new ParsedResult();

        PlayResponseMapper.MapIntlWeb(document.RootElement.GetProperty("data").GetProperty("playurl"), result,
            url => url.Contains(":448"));

        Assert.Equal("https://backup.test/video.m4s", Assert.Single(result.VideoTracks).baseUrl);
        Assert.Equal("https://cdn.test/audio.m4s", Assert.Single(result.AudioTracks).baseUrl);
    }

    [Theory]
    [InlineData("ec-3", "E-AC-3")]
    [InlineData("fLaC", "FLAC")]
    public async Task WebAudioResources_PreserveCodecFamilies(string codec, string expected)
    {
        var response = PlayResponse();
        response["data"]!["playurl"]!["audio_resource"]![0]!["codecs"] = codec;

        var result = await Extract(response.ToJsonString());

        Assert.Equal(expected, Assert.Single(result.AudioTracks).codecs);
    }

    [Fact]
    public async Task WebPlayResponse_WithoutUsableResourcesFailsClearly()
    {
        var response = PlayResponse();
        response["data"]!["playurl"]!["video"]![0]!["video_resource"]!["url"] = "";
        response["data"]!["playurl"]!["audio_resource"]![0]!["url"] = "";

        var error = await Assert.ThrowsAsync<InvalidDataException>(() => Extract(response.ToJsonString()));

        Assert.Contains("未返回可用音视频流", error.Message);
    }

    [Theory]
    [InlineData(10004001, "地区限制")]
    [InlineData(10004004, "Premium")]
    [InlineData(10004005, "登录")]
    public async Task PlayApiPermissionErrors_ArePropagatedBeforeMapping(int code, string expected)
    {
        var response = new JsonObject
        {
            ["code"] = code,
            ["data"] = new JsonObject { ["playurl"] = new JsonObject() }
        }.ToJsonString();
        var error = await Assert.ThrowsAnyAsync<InvalidOperationException>(
            () => Extract(response));

        Assert.Contains(expected, error.Message);
    }

    [Theory]
    [InlineData("true", true)]
    [InlineData("1", true)]
    [InlineData("false", false)]
    [InlineData("0", false)]
    public async Task NestedWebPlayurlPreviewFlag_IsPropagated(string preview, bool expected)
    {
        var response = PlayResponse();
        response["data"]!["playurl"]!["is_preview"] = JsonNode.Parse(preview);

        var result = await Extract(response.ToJsonString());

        Assert.Equal(expected, result.IsPreviewOnly);
        Assert.Single(result.VideoTracks);
        Assert.Single(result.AudioTracks);
    }

    [Fact]
    public void VideoSubtitleOnly_MapsAssAndJsonWithNamespacedOutputPaths()
    {
        var source = SubUtil.ParseIntlSubtitleResponse("""
            {"code":0,"data":{"video_subtitle":[
              {"lang_key":"en","lang":"English","ass":{"url":"https://cdn.test/en.ass"},"srt":{"url":"https://cdn.test/en.json"}},
              {"lang_key":"th","lang":"Thai","ass":{"url":"https://cdn.test/th.ass"}}
            ]}}
            """);

        var result = SubUtil.MergeSubtitleSources([source], "intl_13287667", "");

        Assert.Equal(3, result.Count);
        Assert.Equal(new[] { "en", "en", "th" }, result.Select(subtitle => subtitle.lan));
        Assert.Equal("English", result[0].lanDoc);
        Assert.EndsWith(".ass", result[0].path);
        Assert.EndsWith(".srt", result[1].path);
        Assert.EndsWith(".ass", result[2].path);
        Assert.All(result, subtitle =>
        {
            Assert.StartsWith("intl_13287667/intl_13287667.", subtitle.path);
            Assert.False(Path.IsPathRooted(subtitle.path));
        });
        Assert.Equal(3, result.Select(subtitle => subtitle.path).Distinct().Count());
    }

    [Fact]
    public void DuplicateSubtitleStructures_DeduplicateAssButRetainJsonAlternative()
    {
        var source = SubUtil.ParseIntlSubtitleResponse("""
            {"code":0,"data":{
              "subtitles":[{"lang_key":"en","url":"https://cdn.test/en.ass"}],
              "video_subtitle":[{"lang_key":"en","ass":{"url":"https://cdn.test/en.ass"},"srt":{"url":"https://cdn.test/en.json"}}]
            }}
            """);

        var result = SubUtil.MergeSubtitleSources([source], "intl_13287667", "");

        Assert.Equal(new[] { "https://cdn.test/en.ass", "https://cdn.test/en.json" }, result.Select(subtitle => subtitle.url));
        Assert.EndsWith(".ass", result[0].path);
        Assert.EndsWith(".srt", result[1].path);
    }

    [Fact]
    public void EmptySubtitleUrlsAndEntriesWithoutLanguage_AreSkipped()
    {
        var result = SubUtil.ParseIntlSubtitleResponse("""
            {"code":0,"data":{"video_subtitle":[
              {"lang_key":"en","url":"","ass":{"url":" "},"srt":{"url":null}},
              {"ass":{"url":"https://cdn.test/no-language.ass"}}
            ]}}
            """);

        Assert.Empty(result);
    }

    [Fact]
    public void SubtitlePermissionError_IsNotTreatedAsAnEmptySubtitleList()
    {
        var error = Assert.ThrowsAny<InvalidOperationException>(
            () => SubUtil.ParseIntlSubtitleResponse("{\"code\":10004005}"));

        Assert.Contains("登录", error.Message);
    }

    private static Task<VInfo> FetchInfo(string id, JsonObject season, JsonObject episodes, List<Uri>? requests = null)
    {
        var responses = new Queue<string>([season.ToJsonString(), episodes.ToJsonString()]);
        return IntlBangumiInfoFetcher.FetchAsync(id, url =>
        {
            requests?.Add(new Uri(url));
            return Task.FromResult(responses.Dequeue());
        });
    }

    private static Task<ParsedResult> Extract(string response, Action? onPrimaryFetch = null)
        => Parser.ExtractTracksWithFetcherAsync("intl:2110869:13287667", "", "", "13287667", false, true, false, "0",
            _ => { onPrimaryFetch?.Invoke(); return Task.FromResult(response); },
            (_, _) => throw new InvalidOperationException("The WEB response must not request an APP codec variant"));

    // Successful fixtures follow the current public WEB/yt-dlp field shape;
    // these are synthetic responses, not live streams captured in an allowed region.
    private static JsonObject Season(string? firstEpisode = "13287667") => new()
    {
        ["code"] = 0,
        ["data"] = new JsonObject
        {
            ["season"] = new JsonObject
            {
                ["season_id"] = "2110869",
                ["title"] = " Season ",
                ["description"] = " Description ",
                ["horizontal_cover"] = "https://example.test/horizontal.jpg",
                ["vertical_cover"] = "https://example.test/vertical.jpg",
                ["is_finished"] = true,
                ["first_episode"] = firstEpisode is null ? null : new JsonObject { ["episode_id"] = firstEpisode }
            }
        }
    };

    private static JsonObject Episodes()
    {
        var second = Episode("13287745", "");
        second["short_title_display"] = "E2";
        second["long_title_display"] = "Second";
        second["is_premium"] = true;
        return new JsonObject
        {
            ["code"] = 0,
            ["data"] = new JsonObject
            {
                ["sections"] = new JsonArray(
                    new JsonObject { ["episodes"] = new JsonArray(Episode("13287667", "E1 - First"), second) },
                    new JsonObject { ["episodes"] = new JsonArray(Episode("13287800", "Extra")) })
            }
        };
    }

    private static JsonObject Episode(string id, string title) => new()
    {
        ["episode_id"] = id,
        ["title_display"] = title,
        ["publish_time"] = "2024-01-01T00:00:00Z",
        ["cover"] = $"https://example.test/{id}.jpg"
    };

    private static JsonObject PlayResponse() => JsonNode.Parse("""
        {"code":0,"data":{"playurl":{
          "duration":9000,
          "video":[{"stream_info":{"desc_words":"720P HD"},"video_resource":{
            "url":"https://cdn.test/video.m4s","backup_url":["https://backup.test/video.m4s"],
            "quality":64,"codec_id":7,"codecs":"avc1.64001f","width":1280,"height":720,
            "frame_rate":"30","bandwidth":1500000,"size":4096
          }}],
          "audio_resource":[{
            "url":"https://cdn.test/audio.m4s","backup_url":[],"quality":30280,
            "codecs":"mp4a.40.2","bandwidth":192000
          }]
        }}}
        """)!.AsObject();
}
