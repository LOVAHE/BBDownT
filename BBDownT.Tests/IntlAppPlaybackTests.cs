using System.Text.Json;
using System.Text.Json.Nodes;
using System.Web;
using BBDownT.Core;
using BBDownT.Core.Entity;
using BBDownT.Core.Util;
using static BBDownT.Core.Entity.Entity;

namespace BBDownT.Tests;

public class IntlAppPlaybackTests
{
    [Fact]
    public async Task AppOnlyWebResponseFallsBackToTheExactSelectedEpisode()
    {
        var requests = new List<Uri>();
        var app = AppResponse().ToJsonString();
        var response = await Parser.GetPlayJsonAsync("", "intl:2309571:26223058", "", "", "26222980",
            false, true, false, fetchWeb: url =>
            {
                var uri = new Uri(url);
                requests.Add(uri);
                return Task.FromResult(uri.AbsolutePath.Contains("/web/")
                    ? "{\"code\":10023014,\"message\":\"App only\"}" : app);
            });

        Assert.Equal(app, response);
        Assert.Equal(2, requests.Count);
        Assert.Equal("/intl/gateway/web/playurl", requests[0].AbsolutePath);
        Assert.Equal("/intl/gateway/v2/app/playurl/player", requests[1].AbsolutePath);
        var query = HttpUtility.ParseQueryString(requests[1].Query);
        Assert.Equal("2309571", query["sid"]);
        Assert.Equal("26222980", query["ep_id"]);
        Assert.Equal("0", query["qn"]);
    }

    [Fact]
    public async Task ExplicitIntlAppModeUsesTheAppEndpointWithoutWebProbe()
    {
        var calls = 0;
        await Parser.GetPlayJsonAsync("", "intl:2309571", "", "", "26222855", false, true, true,
            fetchWeb: url =>
            {
                calls++;
                Assert.Equal("/intl/gateway/v2/app/playurl/player", new Uri(url).AbsolutePath);
                Assert.Equal("26222855", HttpUtility.ParseQueryString(new Uri(url).Query)["ep_id"]);
                return Task.FromResult(AppResponse().ToJsonString());
            });
        Assert.Equal(1, calls);
    }

    [Theory]
    [InlineData(10004001)]
    [InlineData(10004004)]
    [InlineData(-101)]
    [InlineData(10015002)]
    public async Task OtherWebErrorsDoNotTriggerAnAppFallback(int code)
    {
        var calls = 0;
        var expected = $"{{\"code\":{code}}}";
        Assert.Equal(expected, await Parser.GetPlayJsonAsync("", "intl:2309571", "", "", "26223058",
            false, true, false, fetchWeb: url =>
            {
                calls++;
                Assert.Contains("/intl/gateway/web/", url);
                return Task.FromResult(expected);
            }));
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task AppResponseReusesIntlMapperAndDoesNotExposeUngrantableQualityTiers()
    {
        var requests = new List<string>();
        var result = await Parser.ExtractTracksWithFetcherAsync("intl:2309571", "", "", "26222855",
            false, true, true, "0", _ => Task.FromResult(AppResponse().ToJsonString()),
            (quality, codec) =>
            {
                requests.Add(codec);
                var response = AppResponse();
                response["data"]!["video_info"]!["stream_list"]![1]!["dash_video"]!["codecid"] = 12;
                return Task.FromResult(response.ToJsonString());
            });

        Assert.Equal(new[] { "1" }, requests);
        Assert.Equal(2, result.VideoTracks.Count);
        Assert.All(result.VideoTracks, track => Assert.Equal("16", track.id));
        Assert.Equal(new[] { "AVC", "HEVC" }, result.VideoTracks.Select(track => track.codecs));
        Assert.Single(result.AudioTracks);
    }

    [Fact]
    public async Task PremiumPermissionFailureIsActionableAndDoesNotRequestCodecVariants()
    {
        var error = await Assert.ThrowsAsync<IntlApiException>(() => Parser.ExtractTracksWithFetcherAsync(
            "intl:2309571", "", "", "26223058", false, true, true, "0",
            _ => Task.FromResult("{\"code\":10015002,\"message\":\"raw secret access_key=private\"}"),
            (_, _) => throw new Exception("Permission failures must not fetch codec variants")));

        Assert.Equal(10015002, error.ApiCode);
        Assert.Contains("权限不足", error.Message);
        Assert.Contains("Premium", error.Message);
        Assert.DoesNotContain("private", error.Message);
        Assert.Equal(error.Message, NetworkRetry.Describe(new IOException("wrapped", error)));
        Assert.False(NetworkRetry.IsTransient(error));
    }

    [Fact]
    public async Task SuccessfulCodeWithCopyrightDialogDoesNotBecomeAnEmptyTrackSuccess()
    {
        const string response = "{\"code\":0,\"data\":{\"video_info\":null,\"dialog\":{\"action\":2,\"type\":3,\"title\":\"raw signed URL\"}}}";
        var error = await Assert.ThrowsAsync<IntlApiException>(() => Parser.ExtractTracksWithFetcherAsync(
            "intl:34613", "", "", "341736", false, true, true, "0",
            _ => Task.FromResult(response), (_, _) => throw new Exception("No variant for region errors")));

        Assert.Contains("地区限制", error.Message);
        Assert.Null(error.ApiCode);
        Assert.DoesNotContain("raw signed", NetworkRetry.Describe(error));
    }

    [Fact]
    public void MissingVideoWithoutKnownDialogIsAnExplicitDataFailure()
    {
        using var document = JsonDocument.Parse("{\"code\":0,\"data\":{\"video_info\":null,\"dialog\":null}}");
        Assert.Throws<InvalidDataException>(() => IntlBangumiWebApi.EnsurePlaybackSuccess(document.RootElement));
    }

    [Fact]
    public void SafeInternationalReasonSurvivesThePageBatchSummary()
    {
        var original = IntlApiException.FromCode(10004001);
        var batch = new PageDownloadBatchException([(1, original)]);
        Assert.Contains("地区限制", batch.Message);
        Assert.Contains("10004001", batch.Message);
        Assert.Same(original, batch.InnerExceptions[0].InnerException);
        Assert.Equal(nameof(IntlApiException), NetworkRetry.Describe(new IntlApiException("secret cookie-value")));
    }

    [Fact]
    public void UnknownQualityUsesTheApiDescriptionWithoutAKeyLookupFailure()
    {
        var response = AppResponse();
        var stream = response["data"]!["video_info"]!["stream_list"]![1]!;
        stream["stream_info"]!["quality"] = 777;
        stream["stream_info"]!["description"] = " Future quality ";
        using var document = JsonDocument.Parse(response.ToJsonString());
        var mapped = new ParsedResult();
        PlayResponseMapper.MapIntl(document.RootElement, mapped, _ => false);
        Assert.Contains(mapped.VideoTracks, track => track.id == "777" && track.dfn == "Future quality");
    }

    [Fact]
    public async Task ParserDebugOutputDoesNotDumpSignedAppMediaUrls()
    {
        var previousOutput = Console.Out;
        var previousDebug = Config.DEBUG_LOG;
        using var output = new StringWriter();
        try
        {
            Console.SetOut(output);
            Config.DEBUG_LOG = true;
            var response = AppResponse();
            response["data"]!["video_info"]!["stream_list"]![1]!["dash_video"]!["base_url"] =
                "https://cdn.test/video.m4s?signature=synthetic-private-media-signature";
            var json = response.ToJsonString();
            await Parser.ExtractTracksWithFetcherAsync("intl:2309571", "", "", "26222855",
                false, true, true, "0", _ => Task.FromResult(json), (_, _) => Task.FromResult(json));
            Assert.Contains("国际站播放响应已接收", output.ToString());
            Assert.DoesNotContain("synthetic-private-media-signature", output.ToString());
        }
        finally
        {
            Console.SetOut(previousOutput);
            Config.DEBUG_LOG = previousDebug;
        }
    }

    private static JsonObject AppResponse() => JsonNode.Parse("""
        {"code":0,"data":{"video_info":{"quality":16,"timelength":10000,
          "stream_list":[
            {"stream_info":{"quality":80,"need_login":true},"dash_video":{"base_url":"https://cdn.test/high.m4s","backup_url":[],"bandwidth":1000000,"codecid":7,"size":1000}},
            {"stream_info":{"quality":16,"description":"360P","need_login":false},"dash_video":{"base_url":"https://cdn.test/video.m4s","backup_url":[],"bandwidth":400000,"codecid":7,"size":400}}
          ],
          "dash_audio":[{"id":30216,"base_url":"https://cdn.test/audio.m4s","backup_url":[],"bandwidth":64000}]
        },"dialog":null}}
        """)!.AsObject();
}
