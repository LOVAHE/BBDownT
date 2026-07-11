using System.Text.Json;
using BBDownT.Core;
using BBDownT.Core.Entity;

namespace BBDownT.Tests;

public class PlayResponseMapperTests
{
    [Fact]
    public void IntlFixture_MapsVideoAndAudioWithoutNetwork()
    {
        using var document = JsonDocument.Parse("""
            {
              "data": {
                "video_info": {
                  "timelength": 9000,
                  "stream_list": [{
                    "stream_info": { "quality": 64 },
                    "dash_video": {
                      "base_url": "https://cdn.test:448/video.m4s",
                      "backup_url": ["https://backup.test/video.m4s"],
                      "bandwidth": 1500000,
                      "codecid": 7,
                      "size": 4096
                    }
                  }],
                  "dash_audio": [{
                    "id": 30280,
                    "base_url": "https://cdn.test/audio.m4s",
                    "backup_url": [],
                    "bandwidth": 192000
                  }]
                }
              }
            }
            """);
        var result = new ParsedResult();

        PlayResponseMapper.MapIntl(
            document.RootElement,
            result,
            url => url.Contains(":448"));

        var video = Assert.Single(result.VideoTracks);
        Assert.Equal("64", video.id);
        Assert.Equal("AVC", video.codecs);
        Assert.Equal(9, video.dur);
        Assert.Equal("https://backup.test/video.m4s", video.baseUrl);
        var audio = Assert.Single(result.AudioTracks);
        Assert.Equal("M4A", audio.codecs);
        Assert.Equal(9, audio.dur);
    }

    [Fact]
    public void WebDashFixture_MapsVideoAndAllAudioFamilies()
    {
        using var document = JsonDocument.Parse("""
            {
              "timelength": 5000,
              "dash": {
                "duration": 4,
                "video": [{
                  "id": 80,
                  "base_url": "https://cdn.test:448/video.m4s",
                  "backup_url": ["https://backup.test/video.m4s"],
                  "bandwidth": 3000000,
                  "codecid": 12,
                  "width": 1920,
                  "height": 1080,
                  "frame_rate": "60",
                  "size": 12345
                }],
                "audio": [{
                  "id": 30280,
                  "base_url": "https://cdn.test/audio.m4s",
                  "backup_url": [],
                  "bandwidth": 192000,
                  "codecs": "mp4a.40.2"
                }],
                "dolby": { "audio": [{
                  "id": 30250,
                  "base_url": "https://cdn.test/dolby.m4s",
                  "backup_url": [],
                  "bandwidth": 448000,
                  "codecs": "ec-3"
                }] },
                "flac": { "audio": {
                  "id": 30251,
                  "base_url": "https://cdn.test/flac.m4s",
                  "backup_url": [],
                  "bandwidth": 900000,
                  "codecs": "fLaC"
                } }
              }
            }
            """);
        var result = new ParsedResult();
        var duration = PlayResponseMapper.GetDurationSeconds(document.RootElement);

        PlayResponseMapper.MapDashVideos(
            document.RootElement, result, duration, false, false, url => url.Contains(":448"));
        PlayResponseMapper.MapDashAudioAndDubbing(
            document.RootElement,
            document.RootElement,
            result,
            duration,
            "2",
            "3",
            false,
            false,
            false,
            _ => false);

        Assert.Equal(5, duration);
        var video = Assert.Single(result.VideoTracks);
        Assert.Equal("https://backup.test/video.m4s", video.baseUrl);
        Assert.Equal("1920x1080", video.res);
        Assert.Equal("60", video.fps);
        Assert.Equal("HEVC", video.codecs);
        Assert.Equal(new[] { "M4A", "E-AC-3", "FLAC" }, result.AudioTracks.Select(audio => audio.codecs));
    }

    [Fact]
    public void AppDashFixture_MapsBackgroundAndRoleAudio()
    {
        using var document = JsonDocument.Parse("""
            {
              "result": {
                "video_info": {
                  "dash": {
                    "duration": 6,
                    "video": [],
                    "audio": []
                  }
                }
              },
              "dubbing_info": {
                "background_audio": [{
                  "id": 1,
                  "base_url": "https://cdn.test/background.m4s",
                  "backup_url": [],
                  "bandwidth": 128000,
                  "codecs": "mp4a.40.2"
                }],
                "role_audio_list": [{
                  "title": "角色/甲",
                  "person_name": "配音员",
                  "audio_id": "role/1",
                  "audio": [{
                    "id": 2,
                    "base_url": "https://cdn.test/role.m4s",
                    "backup_url": [],
                    "bandwidth": 128000,
                    "codecs": "mp4a.40.2"
                  }]
                }]
              }
            }
            """);
        var root = Parser.SelectResponseRoot(document.RootElement);
        var result = new ParsedResult();

        PlayResponseMapper.MapDashAudioAndDubbing(
            document.RootElement,
            root,
            result,
            6,
            "2",
            "3",
            false,
            true,
            true,
            _ => false);

        Assert.Single(result.BackgroundAudioTracks);
        var role = Assert.Single(result.RoleAudioList);
        Assert.Equal("角色/甲", role.title);
        Assert.Equal("配音员", role.personName);
        Assert.Equal("2/2.3.role_1.m4a", role.path);
        Assert.Equal("mp4a.40.2", Assert.Single(role.audio).codecs);
    }

    [Fact]
    public void DashFixture_WithoutRegularAudio_DoesNotPromoteOptionalAudioFamilies()
    {
        using var document = JsonDocument.Parse("""
            {
              "dash": {
                "duration": 1,
                "video": [],
                "dolby": { "audio": [{
                  "id": 30250,
                  "base_url": "https://cdn.test/dolby.m4s",
                  "backup_url": [],
                  "bandwidth": 448000,
                  "codecs": "ec-3"
                }] }
              }
            }
            """);
        var result = new ParsedResult();

        PlayResponseMapper.MapDashAudioAndDubbing(
            document.RootElement,
            document.RootElement,
            result,
            1,
            "2",
            "3",
            false,
            false,
            false,
            _ => false);

        Assert.Empty(result.AudioTracks);
    }

    [Fact]
    public void BangumiClipFixture_MapsSortedMainAndSkipSegments()
    {
        using var document = JsonDocument.Parse("""
            {
              "clip_info_list": [
                { "toastText": "即将跳过片尾", "start": 30, "end": 40 },
                { "toastText": "即将跳过片头", "start": 10, "end": 20 }
              ]
            }
            """);
        var result = new ParsedResult();

        PlayResponseMapper.MapClipInfo(document.RootElement, result);

        Assert.Collection(
            result.ExtraPoints,
            point => AssertPoint(point, "正片", 0, 10),
            point => AssertPoint(point, "片头", 10, 20),
            point => AssertPoint(point, "正片", 20, 30),
            point => AssertPoint(point, "片尾", 30, 40));
    }


    [Fact]
    public void DurlFixture_MapsClipsQualitiesDurationSizeAndCodec()
    {
        using var document = JsonDocument.Parse("""
            {
              "quality": 80,
              "video_codecid": 12,
              "accept_quality": [80, 64, 32],
              "durl": [
                { "url": "https://cdn.test/part-1.flv", "size": 1000, "length": 1500 },
                { "url": "https://cdn.test/part-2.flv", "size": 2000, "length": 2500 }
              ]
            }
            """);
        var result = new ParsedResult();

        PlayResponseMapper.MapDurl(document.RootElement, result);

        Assert.Equal(
            new[] { "https://cdn.test/part-1.flv", "https://cdn.test/part-2.flv" },
            result.Clips);
        Assert.Equal(new[] { "80", "64", "32" }, result.Dfns);
        var video = Assert.Single(result.VideoTracks);
        Assert.Equal("80", video.id);
        Assert.Equal(Config.qualitys["80"], video.dfn);
        Assert.Equal("HEVC", video.codecs);
        Assert.Equal(4, video.dur);
        Assert.Equal(3000, video.size);
        Assert.Equal(string.Empty, video.baseUrl);
    }

    [Fact]
    public void TvDurlFixture_UsesQnExtras()
    {
        using var document = JsonDocument.Parse("""
            {
              "quality": 64,
              "video_codecid": 7,
              "qn_extras": [{ "qn": 64 }, { "qn": 32 }],
              "durl": [
                { "url": "https://cdn.test/video.flv", "size": 512, "length": 1000 }
              ]
            }
            """);
        var result = new ParsedResult();

        PlayResponseMapper.MapDurl(document.RootElement, result);

        Assert.Equal(new[] { "64", "32" }, result.Dfns);
        Assert.Equal("AVC", Assert.Single(result.VideoTracks).codecs);
    }

    private static void AssertPoint(
        BBDownT.Core.Entity.Entity.ViewPoint point,
        string title,
        int start,
        int end)
    {
        Assert.Equal(title, point.title);
        Assert.Equal(start, point.start);
        Assert.Equal(end, point.end);
    }
}
