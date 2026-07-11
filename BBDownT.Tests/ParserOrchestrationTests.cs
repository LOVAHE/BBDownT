using BBDownT.Core;

namespace BBDownT.Tests;

public class ParserOrchestrationTests
{
    [Fact]
    public async Task Intl_RequestsDefaultThenCodeOneAndAccumulatesTracks()
    {
        var primaryQns = new List<string>();
        var variants = new List<(string Qn, string Code)>();

        var result = await Parser.ExtractTracksWithFetcherAsync(
            "ep:1", "2", "3", "4", false, true, false, "0",
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
            "ep:1", "2", "3", "4", false, false, false, "0",
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
            "BV", "2", "3", "4", false, false, false, "0",
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
            "BV", "2", "3", "4", false, false, true, "64",
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
            "BV", "2", "3", "4", false, false, false, "0",
            qn =>
            {
                requestedQns.Add(qn);
                return Task.FromResult(responses.Dequeue());
            },
            (_, _) => throw new InvalidOperationException("Intl fetch should not run"));

        Assert.Equal(new[] { "0", Config.qualitys.Keys.First() }, requestedQns);
        Assert.Equal(new[] { "https://cdn.test/final.flv" }, result.Clips);
    }

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

    private static string DashFixture(string videoUrl, string audioUrl, bool includeClip = false)
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
                    "id": 64,
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
