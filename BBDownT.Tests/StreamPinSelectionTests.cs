using BBDownT.Core.Entity;
using static BBDownT.Core.Entity.Entity;

namespace BBDownT.Tests;

public class StreamPinSelectionTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("120")]
    [InlineData("120:HEVC")]
    [InlineData("80:avc")]
    [InlineData("16:AV1")]
    public void ValidateVideo_AcceptsQualityCodeWithOptionalCodec(string? value)
    {
        Assert.Null(StreamPinSelection.ValidateVideo(value));
    }

    [Theory]
    [InlineData("4K")]
    [InlineData("120:H264")]
    [InlineData("120:HEVC:1")]
    [InlineData("12345")]
    public void ValidateVideo_RejectsUnknownFormats(string value)
    {
        Assert.NotNull(StreamPinSelection.ValidateVideo(value));
    }

    [Theory]
    [InlineData("30280", true)]
    [InlineData("192K", false)]
    [InlineData("-1", false)]
    public void ValidateAudio_AcceptsNumericIds(string value, bool valid)
    {
        Assert.Equal(valid, StreamPinSelection.ValidateAudio(value) is null);
    }

    [Fact]
    public void Apply_MovesExactVideoAndAudioToFront()
    {
        var result = Result();
        var option = new MyOption { VideoStream = "120:hevc", AudioStream = "30232" };

        var misses = StreamPinSelection.Apply(option, result);

        Assert.Empty(misses);
        Assert.Equal("120:HEVC", StreamPinSelection.VideoKey(result.VideoTracks[0]));
        Assert.Equal("30232", result.AudioTracks[0].id);
        // 其余顺序保持排序结果
        Assert.Equal(["120:HEVC", "120:AVC", "80:AVC", "80:HEVC"], result.VideoTracks.Select(StreamPinSelection.VideoKey));
        Assert.Equal(["30232", "30280", "30216"], result.AudioTracks.Select(a => a.id));
    }

    [Fact]
    public void Apply_QualityWithoutCodecKeepsCodecPriorityWithinQuality()
    {
        var result = Result();

        StreamPinSelection.Apply(new MyOption { VideoStream = "80" }, result);

        Assert.Equal("80:AVC", StreamPinSelection.VideoKey(result.VideoTracks[0]));
    }

    [Fact]
    public void Apply_MissingStreamKeepsPriorityOrderAndReportsFallback()
    {
        var result = Result();

        var misses = StreamPinSelection.Apply(new MyOption { VideoStream = "127:AV1", AudioStream = "30251" }, result);

        Assert.Equal(2, misses.Count);
        Assert.Contains("120:AVC", misses[0]);
        Assert.Equal("120:AVC", StreamPinSelection.VideoKey(result.VideoTracks[0]));
        Assert.Equal("30280", result.AudioTracks[0].id);
    }

    [Fact]
    public void Apply_WithoutPinsDoesNothing()
    {
        var result = Result();
        var before = result.VideoTracks.Select(StreamPinSelection.VideoKey).ToList();

        Assert.Empty(StreamPinSelection.Apply(new MyOption(), result));
        Assert.Equal(before, result.VideoTracks.Select(StreamPinSelection.VideoKey));
    }

    [Fact]
    public void Apply_KeepsBackgroundAudioInStepWithMainAudio()
    {
        var result = Result();
        result.BackgroundAudioTracks = [Audio("30280", 190), Audio("30232", 130)];

        StreamPinSelection.Apply(new MyOption { AudioStream = "30232" }, result);

        Assert.Equal("30232", result.BackgroundAudioTracks[0].id);
    }

    [Fact]
    public void DescribeSelectedStreams_NamesQualityCodecAndAudio()
    {
        var page = new Page(2, "1", "2", "", "part", 10, "", 0);
        var text = Program.DescribeSelectedStreams(page, "WEB", Video("120", "AVC", 5000), Audio("30280", 190));

        Assert.Equal("P2 · WEB · 4K 超清 3840x2160 AVC · 192K M4A", text);
    }

    [Fact]
    public void InfoTrackLines_EndWithTheValuesForVideoStreamAndAudioStream()
    {
        var video = Program.FormatVideoTrackLine(0, Video("120", "HEVC", 3400), 100);
        var audio = Program.FormatAudioTrackLine(1, Audio("30280", 190), 100);
        var unknownAudio = Program.FormatAudioTrackLine(2, Audio("30999", 50), 100);
        var appVideo = Video("32", "HEVC", 200);
        appVideo.res = null;
        appVideo.fps = null;

        Assert.StartsWith("0. [4K 超清] [3840x2160] [HEVC] [30] [3400 kbps]", video);
        Assert.EndsWith("(-vs 120:HEVC)", video);
        Assert.StartsWith("1. [192K] [M4A] [190 kbps]", audio);
        Assert.EndsWith("(-as 30280)", audio);
        Assert.StartsWith("2. [M4A] [50 kbps]", unknownAudio);
        Assert.EndsWith("(-as 30999)", unknownAudio);
        // APP/TV 接口没有分辨率和帧率时不留空括号
        Assert.Equal("0. [480P 清晰] [HEVC] [200 kbps] [~2.44 MB] (-vs 32:HEVC)", Program.FormatVideoTrackLine(0, appVideo, 100));
    }

    [Fact]
    public void UnknownCodec_KeyIsJustTheQualityCodeSoItPassesValidation()
    {
        var video = Video("80", "AVC", 1300);
        video.codecs = "UNKNOWN";

        var key = StreamPinSelection.VideoKey(video);

        Assert.Equal("80", key);
        Assert.Null(StreamPinSelection.ValidateVideo(key));
        Assert.EndsWith("(-vs 80)", Program.FormatVideoTrackLine(0, video, 100));
        // 只给画质代码时匹配该画质的任意编码
        var result = new ParsedResult { VideoTracks = [Video("120", "AVC", 5000), video] };
        Assert.Empty(StreamPinSelection.Apply(new MyOption { VideoStream = key }, result));
        Assert.Same(video, result.VideoTracks[0]);
    }

    [Theory]
    [InlineData(false, new[] { "30280", "30232", "30216" })]
    [InlineData(true, new[] { "30216", "30232", "30280" })]
    public void AudioWithTheSameBandwidth_IsOrderedByNominalLevel(bool ascending, string[] expected)
    {
        // APP 接口下 B站 给 192K 和 132K 标了同样的码率，接口顺序是 132K 在前
        var sorted = Program.SortTracks([Audio("30232", 94), Audio("30280", 94), Audio("30216", 65)], new Dictionary<string, byte>(), ascending);

        Assert.Equal(expected, sorted.Select(a => a.id));
    }

    private static ParsedResult Result() => new()
    {
        // 已按默认优先级排好序(画质降序，同画质按码率)
        VideoTracks = [Video("120", "AVC", 5000), Video("120", "HEVC", 3400), Video("80", "AVC", 1300), Video("80", "HEVC", 700)],
        AudioTracks = [Audio("30280", 190), Audio("30232", 130), Audio("30216", 64)]
    };

    internal static Video Video(string id, string codec, long kbps) => new()
    {
        id = id,
        dfn = BBDownT.Core.Config.qualitys[id],
        baseUrl = "https://example.invalid/" + id + codec,
        codecs = codec,
        res = id == "120" ? "3840x2160" : "1920x1080",
        fps = "30",
        bandwith = kbps,
        dur = 100
    };

    internal static Audio Audio(string id, long kbps) => new()
    {
        id = id,
        dfn = id,
        baseUrl = "https://example.invalid/" + id,
        codecs = "M4A",
        bandwith = kbps,
        dur = 100
    };
}
