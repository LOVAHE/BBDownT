using static BBDownT.Core.Entity.Entity;

namespace BBDownT.Tests;

public class MuxArgumentTests
{
    private static List<string> FFmpegArgs(string videoPath, string audioPath, List<Subtitle>? subs = null,
        bool audioOnly = false, bool videoOnly = false, bool isHevc = false) =>
        BBDownTMuxer.FFmpegArgs(videoPath, audioPath, [], "out.mp4", "", "title", "", "", "", "", subs,
            audioOnly, videoOnly, null, 0, false, isHevc);

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Hevc_IsTaggedHvc1OnEveryPlatform(bool isHevc)
    {
        Assert.Equal(isHevc, ContainsPair(FFmpegArgs("video.mp4", "audio.m4a", isHevc: isHevc), "-tag:v:0", "hvc1"));
    }

    private static bool ContainsPair(List<string> args, string option, string value) =>
        args.Zip(args.Skip(1)).Any(pair => pair.First == option && pair.Second == value);

    [Fact]
    public void SubtitleMetadata_TargetsTheSubtitleStreamsActuallyAdded()
    {
        using var files = new MediaTestDirectory();
        List<Subtitle> subs =
        [
            new() { lan = "zh-CN", url = "", path = files.Write("v.zh-CN.srt", "") },
            new() { lan = "en-US", url = "", path = files.Write("v.en-US.srt", "1\n00:00:00,000 --> 00:00:01,000\nhi\n") }
        ];

        var args = FFmpegArgs("video.mp4", "audio.m4a", subs);

        Assert.DoesNotContain(subs[0].path, args);
        Assert.True(ContainsPair(args, "-metadata:s:s:0", "language=eng"));
        Assert.True(ContainsPair(args, "-metadata:s:s:0", "title=English(USA)"));
        Assert.DoesNotContain("-metadata:s:s:1", args);
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(false, true, true)]
    public void VideoOnly_DropsAudioFromProgressiveSources(bool audioOnly, bool videoOnly, bool dropsAudio)
    {
        var args = FFmpegArgs("merged.mp4", "", audioOnly: audioOnly, videoOnly: videoOnly);

        Assert.Equal(dropsAudio, args.Contains("-an"));
        Assert.Equal(audioOnly, args.Contains("-vn"));
    }

    [Fact]
    public void FFmpegArgs_SetStrictOnlyOnce()
    {
        var args = FFmpegArgs("video.mp4", "audio.m4a");

        Assert.Single(args, arg => arg == "-strict");
        Assert.True(ContainsPair(args, "-strict", "-2"));
    }

    [Fact]
    public void Mp4box_KeepsDubAndBackgroundAudioTracks()
    {
        List<AudioMaterial> materials =
        [
            new("背景音频", "", "10/10.20.P1.back_ground.m4a"),
            new("中配", "配音演员", "10/10.20.30.m4a")
        ];

        var args = BBDownTMuxer.Mp4boxArgs("video.mp4", "audio.m4a", materials, "out.mp4", "", null, false, null, "itags");

        Assert.True(ContainsPair(args, "-add", "10/10.20.P1.back_ground.m4a"));
        Assert.True(ContainsPair(args, "-add", "10/10.20.30.m4a"));
        Assert.True(ContainsPair(args, "-udta", "2:type=name:str=原音频"));
        Assert.True(ContainsPair(args, "-udta", "3:type=name:str=背景音频"));
        Assert.True(ContainsPair(args, "-udta", "4:type=name:str=中配"));
    }

    [Fact]
    public void Mp4box_SimplyMuxKeepsOnlyTheCover()
    {
        Assert.Equal(new[] { "cover=cover.jpg" }, BBDownTMuxer.Mp4boxTags("desc", "title", "author", "", "cover.jpg", simplyMux: true));
        Assert.Equal(new[] { "cover=cover.jpg", "title=title", "sdesc=desc", "comment=desc", "artist=author" },
            BBDownTMuxer.Mp4boxTags("desc", "title", "author", "", "cover.jpg", simplyMux: false));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FailedMux_RemovesItsScratchFiles(bool useMp4box)
    {
        using var files = new MediaTestDirectory();
        var video = files.Write("video.mp4", "video");
        var chapters = files.FilePath("chapters");
        var tags = files.FilePath("itags");
        var missingTool = files.FilePath("missing-tool");
        var previousFfmpeg = BBDownTMuxer.FFMPEG;
        var previousMp4box = BBDownTMuxer.MP4BOX;
        try
        {
            BBDownTMuxer.FFMPEG = missingTool;
            BBDownTMuxer.MP4BOX = missingTool;
            Assert.ThrowsAny<Exception>(() => BBDownTMuxer.MuxAV(useMp4box, video, "", [], files.FilePath("out.mp4"),
                points: [new ViewPoint { title = "片头", start = 0, end = 1 }]));
        }
        finally
        {
            BBDownTMuxer.FFMPEG = previousFfmpeg;
            BBDownTMuxer.MP4BOX = previousMp4box;
        }

        Assert.False(File.Exists(chapters));
        Assert.False(File.Exists(tags));
        Assert.True(File.Exists(video));
    }
}
