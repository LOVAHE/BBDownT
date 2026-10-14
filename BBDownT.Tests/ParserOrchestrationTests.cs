using BBDownT.Core;
using BBDownT.Core.Util;
using System.Text.Json.Nodes;

namespace BBDownT.Tests;

public class ParserOrchestrationTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DashPreviewStatus_SurvivesTheQualityRefetch(bool initialPreview)
    {
        var preview = JsonNode.Parse(DashFixture("https://cdn.test/preview-v.m4s", "https://cdn.test/preview-a.m4s"))!;
        preview["data"]!["is_preview"] = 1;
        string[] bodies = initialPreview
            ? [preview.ToJsonString(), "{\"code\":-1}"]
            : [DashFixture("https://cdn.test/full-v.m4s", "https://cdn.test/full-a.m4s"), preview.ToJsonString()];
        var responses = new Queue<string>(bodies);

        var result = await Parser.ExtractTracksWithFetcherAsync("ep:1", "2", "3", false, false, false, "0",
            _ => Task.FromResult(responses.Dequeue()), (_, _) => throw new Exception("No intl requests"));

        Assert.True(result.IsPreviewOnly);
        Assert.Single(result.VideoTracks);
        Assert.Single(result.AudioTracks);
    }

    [Fact]
    public async Task NestedV2Preview_IsMarkedWhenItsTracksAreMapped()
    {
        var initial = JsonNode.Parse(DashFixture("https://cdn.test/preview-v.m4s", "https://cdn.test/preview-a.m4s"))!;
        initial["data"]!["is_preview"] = true;
        var response = new JsonObject { ["result"] = new JsonObject { ["video_info"] = initial["data"]!.DeepClone() } }.ToJsonString();

        var result = await Parser.ExtractTracksWithFetcherAsync("ep:1", "2", "3", false, false, false, "0",
            _ => Task.FromResult(response), (_, _) => throw new Exception("No intl requests"));

        Assert.True(result.IsPreviewOnly);
        Assert.Single(result.VideoTracks);
        Assert.Single(result.AudioTracks);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task DurlReplacement_PreviewStatusFollowsTheAcceptedResponse(bool initialPreview, bool finalPreview)
    {
        string Response(bool preview) => $$$"""
            {"result":{"is_preview":{{{(preview ? 1 : 0)}}},"quality":16,"video_codecid":7,
            "durl":[{"url":"https://cdn.test/clip.mp4","size":100,"length":1000}]}}
            """;
        var responses = new Queue<string>([Response(initialPreview), Response(finalPreview)]);

        var result = await Parser.ExtractTracksWithFetcherAsync("ep:1", "2", "3", false, false, false, "0",
            _ => Task.FromResult(responses.Dequeue()), (_, _) => throw new Exception("No intl requests"));

        Assert.Equal(finalPreview, result.IsPreviewOnly);
        Assert.Single(result.Clips);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task IntlAccumulatedTracks_PreserveEitherVariantsPreviewStatus(bool initialPreview, bool finalPreview)
    {
        var initial = JsonNode.Parse(IntlFixture("https://cdn.test/preview-v.m4s", "https://cdn.test/preview-a.m4s"))!;
        initial["data"]!["video_info"]!["is_preview"] = initialPreview;
        var final = JsonNode.Parse(IntlFixture("https://cdn.test/full-v.m4s", "https://cdn.test/full-a.m4s", 80))!;
        final["data"]!["video_info"]!["is_preview"] = finalPreview;

        var result = await Parser.ExtractTracksWithFetcherAsync("ep:1", "2", "3", false, true, false, "0",
            _ => Task.FromResult(initial.ToJsonString()),
            (_, _) => Task.FromResult(final.ToJsonString()));

        Assert.True(result.IsPreviewOnly);
        Assert.Equal(2, result.VideoTracks.Count);
        Assert.Equal(2, result.AudioTracks.Count);
    }

    private const string RiskControlVoucher =
        "{\"code\":0,\"message\":\"OK\",\"data\":{\"v_voucher\":\"voucher-test\"}}";

    [Fact]
    public async Task RiskControlVoucher_RotatesUserAgentAndRetriesPrimaryRequest()
    {
        var requestedQns = new List<string>();
        var delays = new List<int>();
        var rotations = 0;
        var responses = new Queue<string>(
        [
            RiskControlVoucher,
            DashFixture("https://cdn.test/video-first.m4s", "https://cdn.test/audio-first.m4s"),
            DashFixture("https://cdn.test/video-final.m4s", "https://cdn.test/audio-final.m4s")
        ]);

        var result = await Parser.ExtractTracksWithFetcherAsync(
            "BV", "2", "3", false, false, false, "0",
            qn =>
            {
                requestedQns.Add(qn);
                return Task.FromResult(responses.Dequeue());
            },
            (_, _) => throw new InvalidOperationException("Intl fetch should not run"),
            riskControlDelay: milliseconds =>
            {
                delays.Add(milliseconds);
                return Task.CompletedTask;
            },
            rotateUserAgent: () =>
            {
                rotations++;
                return true;
            });

        Assert.Equal(new[] { "0", "0", Config.qualitys.Keys.First() }, requestedQns);
        Assert.Equal(new[] { 1000 }, delays);
        Assert.Equal(1, rotations);
        Assert.Equal("https://cdn.test/video-first.m4s", Assert.Single(result.VideoTracks).baseUrl);
        Assert.Equal("https://cdn.test/audio-final.m4s", Assert.Single(result.AudioTracks).baseUrl);
    }

    [Fact]
    public async Task RiskControlVoucher_StopsAfterBoundedRetries()
    {
        var requests = 0;
        var delays = 0;
        var rotations = 0;

        var response = await Parser.FetchPlayResponseWithRiskControlRetryAsync(
            () =>
            {
                requests++;
                return Task.FromResult(RiskControlVoucher);
            },
            _ =>
            {
                delays++;
                return Task.CompletedTask;
            },
            () =>
            {
                rotations++;
                return true;
            });

        Assert.Equal(RiskControlVoucher, response);
        Assert.Equal(3, requests);
        Assert.Equal(2, delays);
        Assert.Equal(2, rotations);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HdrVivid_MapsReturnedTrackAndRequestsNewMaximum(bool tvApi)
    {
        var requestedQns = new List<string>();
        var result = await Parser.ExtractTracksWithFetcherAsync(
            "BV", "2", "3", tvApi, false, false, "0",
            qn =>
            {
                requestedQns.Add(qn);
                return Task.FromResult(DashFixture(
                    "https://cdn.test/hdr-vivid.m4s",
                    "https://cdn.test/audio.m4s",
                    quality: 129));
            },
            (_, _) => throw new InvalidOperationException("Intl fetch should not run"));

        Assert.Equal(new[] { "0", "129" }, requestedQns);
        var video = Assert.Single(result.VideoTracks);
        Assert.Equal("129", video.id);
        Assert.Equal("HDR Vivid", video.dfn);
        Assert.Equal("https://cdn.test/hdr-vivid.m4s", video.baseUrl);
        Assert.Single(result.AudioTracks);
    }

    [Fact]
    public async Task Intl_RequestsDefaultThenCodeOneAndAccumulatesTracks()
    {
        var primaryQns = new List<string>();
        var variants = new List<(string Qn, string Code)>();

        var result = await Parser.ExtractTracksWithFetcherAsync(
            "ep:1", "2", "3", false, true, false, "0",
            qn =>
            {
                primaryQns.Add(qn);
                return Task.FromResult(IntlFixture("https://cdn.test/video-1.m4s", "https://cdn.test/audio-1.m4s"));
            },
            (qn, code) =>
            {
                variants.Add((qn, code));
                return Task.FromResult(IntlFixture("https://cdn.test/video-2.m4s", "https://cdn.test/audio-2.m4s", quality: 80));
            });

        Assert.Equal(new[] { "0" }, primaryQns);
        Assert.Equal(new[] { ("0", "1") }, variants);
        Assert.Equal(2, result.VideoTracks.Count);
        Assert.Equal(2, result.AudioTracks.Count);
    }

    [Fact]
    public async Task WebDash_RefetchesMaximumQualityAndMapsAudioAndClipsFromFinalResponse()
    {
        var requestedQns = new List<string>();
        var responses = new Queue<string>(
        [
            DashFixture("https://cdn.test/video-first.m4s", "https://cdn.test/audio-first.m4s"),
            DashFixture(
                "https://cdn.test/video-final.m4s",
                "https://cdn.test/audio-final.m4s",
                includeClip: true)
        ]);

        var result = await Parser.ExtractTracksWithFetcherAsync(
            "ep:1", "2", "3", false, false, false, "0",
            qn =>
            {
                requestedQns.Add(qn);
                return Task.FromResult(responses.Dequeue());
            },
            (_, _) => throw new InvalidOperationException("Intl fetch should not run"));

        Assert.Equal(new[] { "0", Config.qualitys.Keys.First() }, requestedQns);
        Assert.Equal("https://cdn.test/audio-final.m4s", Assert.Single(result.AudioTracks).baseUrl);
        Assert.Contains(result.ExtraPoints, point => point.title == "片头");
    }

    [Fact]
    public async Task WebDash_InvalidRefetch_KeepsInitialVideoAndAudio()
    {
        var responses = new Queue<string>(
        [
            DashFixture("https://cdn.test/video-first.m4s", "https://cdn.test/audio-first.m4s"),
            "{\"code\":-1}"
        ]);

        var result = await Parser.ExtractTracksWithFetcherAsync(
            "BV", "2", "3", false, false, false, "0",
            _ => Task.FromResult(responses.Dequeue()),
            (_, _) => throw new InvalidOperationException("Intl fetch should not run"));

        Assert.Equal("https://cdn.test/video-first.m4s", Assert.Single(result.VideoTracks).baseUrl);
        Assert.Equal("https://cdn.test/audio-first.m4s", Assert.Single(result.AudioTracks).baseUrl);
    }

    [Fact]
    public async Task AppDash_UsesSinglePrimaryResponse()
    {
        var requestCount = 0;

        var result = await Parser.ExtractTracksWithFetcherAsync(
            "BV", "2", "3", false, false, true, "64",
            qn =>
            {
                requestCount++;
                Assert.Equal("64", qn);
                return Task.FromResult(AppDashFixture());
            },
            (_, _) => throw new InvalidOperationException("Intl fetch should not run"));

        Assert.Equal(1, requestCount);
        Assert.Single(result.VideoTracks);
        Assert.Single(result.AudioTracks);
    }

    [Fact]
    public async Task Durl_MapsOnlyMaximumQualityRefetch()
    {
        var requestedQns = new List<string>();
        var responses = new Queue<string>(
        [
            "{\"data\":{\"durl\":[]}}",
            """
            {
              "data": {
                "quality": 64,
                "video_codecid": 7,
                "accept_quality": [64],
                "durl": [{
                  "url": "https://cdn.test/final.flv",
                  "size": 100,
                  "length": 1000
                }]
              }
            }
            """
        ]);

        var result = await Parser.ExtractTracksWithFetcherAsync(
            "BV", "2", "3", false, false, false, "0",
            qn =>
            {
                requestedQns.Add(qn);
                return Task.FromResult(responses.Dequeue());
            },
            (_, _) => throw new InvalidOperationException("Intl fetch should not run"));

        Assert.Equal(new[] { "0", Config.qualitys.Keys.First() }, requestedQns);
        Assert.Equal(new[] { "https://cdn.test/final.flv" }, result.Clips);
    }

    [Fact]
    public async Task Durl_ExplicitQualityMapsRequestedResponseWithoutRefetch()
    {
        var requestedQns = new List<string>();

        var result = await Parser.ExtractTracksWithFetcherAsync(
            "BV", "2", "3", false, false, false, "32",
            qn =>
            {
                requestedQns.Add(qn);
                return Task.FromResult(DurlFixture("https://cdn.test/480p.flv", 32));
            },
            (_, _) => throw new InvalidOperationException("Intl fetch should not run"));

        Assert.Equal(new[] { "32" }, requestedQns);
        Assert.Equal(new[] { "https://cdn.test/480p.flv" }, result.Clips);
        Assert.Equal("32", Assert.Single(result.VideoTracks).id);
    }

    [Fact]
    public async Task Durl_InvalidMaximumRefetchKeepsInitialResponse()
    {
        var responses = new Queue<string>([DurlFixture("https://cdn.test/first.flv", 64), "{\"code\":-1}"]);

        var result = await Parser.ExtractTracksWithFetcherAsync(
            "BV", "2", "3", false, false, false, "0",
            _ => Task.FromResult(responses.Dequeue()),
            (_, _) => throw new InvalidOperationException("Intl fetch should not run"));

        Assert.Empty(responses);
        Assert.Equal(new[] { "https://cdn.test/first.flv" }, result.Clips);
        Assert.Equal("64", Assert.Single(result.VideoTracks).id);
    }

    [Theory]
    [InlineData("{\"code\":-352,\"message\":\"风控校验失败\"}", -352)]
    [InlineData("{\"code\":-10403,\"message\":\"抱歉您所在地区不可观看！\",\"result\":null}", -10403)]
    public async Task PlayApiRefusal_IsRaisedInsteadOfAnEmptyResult(string response, int code)
    {
        var requests = 0;

        var error = await Assert.ThrowsAsync<BilibiliApiException>(() => Parser.ExtractTracksWithFetcherAsync(
            "ep:1", "2", "3", false, false, false, "0",
            _ =>
            {
                requests++;
                return Task.FromResult(response);
            },
            (_, _) => throw new InvalidOperationException("Intl fetch should not run")));

        Assert.Equal(code, error.Code);
        Assert.Equal(1, requests);
    }

    [Fact]
    public async Task RiskControlVoucher_AfterBoundedRetriesIsReportedAsRateLimited()
    {
        var error = await Assert.ThrowsAsync<BilibiliApiException>(() => Parser.ExtractTracksWithFetcherAsync(
            "BV", "2", "3", false, false, false, "0",
            _ => Task.FromResult(RiskControlVoucher),
            (_, _) => throw new InvalidOperationException("Intl fetch should not run"),
            riskControlDelay: _ => Task.CompletedTask,
            rotateUserAgent: () => true));

        Assert.Equal(-352, error.Code);
        Assert.True(new PageDownloadRetry().TryGetDelay(error, out _));
    }

    private static string DurlFixture(string url, int quality) => $$$"""
        {"data":{"quality":{{{quality}}},"video_codecid":7,"accept_quality":[80,64,32],
          "durl":[{"url":"{{{url}}}","size":100,"length":1000}]}}
        """;

    private static string IntlFixture(string videoUrl, string audioUrl, int quality = 64)
    {
        return $$"""
            {
              "data": {
                "video_info": {
                  "timelength": 1000,
                  "stream_list": [{
                    "stream_info": { "quality": {{quality}} },
                    "dash_video": {
                      "base_url": "{{videoUrl}}",
                      "backup_url": [],
                      "bandwidth": 1000000,
                      "codecid": 7
                    }
                  }],
                  "dash_audio": [{
                    "id": {{30200 + quality}},
                    "base_url": "{{audioUrl}}",
                    "backup_url": [],
                    "bandwidth": 192000
                  }]
                }
              }
            }
            """;
    }

    private static string DashFixture(string videoUrl, string audioUrl, bool includeClip = false, int quality = 64)
    {
        var clip = includeClip
            ? ",\"clip_info_list\":[{\"toastText\":\"即将跳过片头\",\"start\":1,\"end\":2}]"
            : string.Empty;
        return $$"""
            {
              "data": {
                "dash": {
                  "duration": 3,
                  "video": [{
                    "id": {{quality}},
                    "base_url": "{{videoUrl}}",
                    "backup_url": [],
                    "bandwidth": 1000000,
                    "codecid": 7,
                    "width": 1280,
                    "height": 720,
                    "frame_rate": "30"
                  }],
                  "audio": [{
                    "id": 30280,
                    "base_url": "{{audioUrl}}",
                    "backup_url": [],
                    "bandwidth": 192000,
                    "codecs": "mp4a.40.2"
                  }]
                }
                {{clip}}
              }
            }
            """;
    }

    private static string AppDashFixture()
    {
        return """
            {
              "data": {
                "dash": {
                  "duration": 3,
                  "video": [{
                    "id": 64,
                    "base_url": "https://cdn.test/video.m4s",
                    "backup_url": [],
                    "bandwidth": 1000000,
                    "codecid": 7
                  }],
                  "audio": [{
                    "id": 30280,
                    "base_url": "https://cdn.test/audio.m4s",
                    "backup_url": [],
                    "bandwidth": 192000,
                    "codecs": "mp4a.40.2"
                  }]
                }
              }
            }
            """;
    }
}
