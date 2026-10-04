using System.Diagnostics;
using System.Text.Json;
using static BBDownT.Core.Entity.Entity;

namespace BBDownT.Tests;

public class ChapterMetadataTests
{
    [Fact]
    public void BothMetadataFormats_PreserveChineseChapterNamesAndTimes()
    {
        List<ViewPoint> points =
        [
            new() { title = "2021甲卷", start = 0, end = 151 },
            new() { title = "片尾", start = 151, end = 200 }
        ];

        var ffmpeg = BBDownTUtil.GetFFmpegMetaString(points);
        var mp4box = BBDownTUtil.GetMp4boxMetaString(points);

        Assert.Contains("START=151000", ffmpeg);
        Assert.Contains("END=200000", ffmpeg);
        Assert.Contains("title=2021甲卷", ffmpeg);
        Assert.Contains("title=片尾", ffmpeg);
        Assert.Equal($"00:00:00 2021甲卷{Environment.NewLine}00:02:31 片尾{Environment.NewLine}", mp4box);
    }

    [Fact]
    public void FFmpegMetadata_EscapesSpecialCharactersAndLineBreaks()
    {
        var metadata = BBDownTUtil.GetFFmpegMetaString(
            [new ViewPoint { title = "A=B;#C\\D\r\n第二行", start = 0, end = 10 }]);

        Assert.StartsWith(";FFMETADATA1" + Environment.NewLine, metadata);
        Assert.Contains("title=A\\=B\\;\\#C\\\\D\\\n第二行", metadata);
    }

    [Fact]
    public void FFmpegMetadata_ConvertsLongTimesWithoutIntegerOverflow()
    {
        var metadata = BBDownTUtil.GetFFmpegMetaString(
            [new ViewPoint { title = "long recording", start = 3_000_000, end = 3_000_001 }]);

        Assert.Contains("START=3000000000", metadata);
        Assert.Contains("END=3000001000", metadata);
    }

    [Fact]
    public void FFmpegMetadata_NullTitleKeepsAnEmptyTitle()
    {
        var metadata = BBDownTUtil.GetFFmpegMetaString(
            [new ViewPoint { title = null!, start = 0, end = 10 }]);

        Assert.Contains("title=" + Environment.NewLine, metadata);
    }

    // Supply an empty directory in BBDOWN_CHAPTER_TEST_DIR. The caller owns
    // cleanup of audio.m4a, output.m4a and chapters after inspecting the result.
    [ChapterMuxFact]
    public void FFmpegMux_PreservesChapterTitlesWithoutInheritingInputMetadata()
    {
        var root = Path.GetFullPath(Environment.GetEnvironmentVariable("BBDOWN_CHAPTER_TEST_DIR")!);
        Assert.True(Directory.Exists(root));
        var audio = Path.Combine(root, "audio.m4a");
        var output = Path.Combine(root, "output.m4a");
        Assert.False(File.Exists(audio));
        Assert.False(File.Exists(output));
        Assert.False(File.Exists(Path.Combine(root, "chapters")));

        var ffmpeg = BBDownTUtil.FindExecutable("ffmpeg")!;
        var ffprobe = BBDownTUtil.FindExecutable("ffprobe")!;
        RunTool(ffmpeg, "-v", "error", "-f", "lavfi", "-i", "anullsrc=r=8000:cl=mono",
            "-t", "2", "-c:a", "aac", "-threads", "1",
            "-metadata", "title=source title",
            "-metadata:s:a:0", "language=fra",
            "-metadata:s:a:0", "handler_name=source handler", audio);
        List<ViewPoint> points =
        [
            new() { title = "A=B;#C\\D\n第二行", start = 0, end = 1 },
            new() { title = "片尾", start = 1, end = 2 }
        ];

        var previousFfmpeg = BBDownTMuxer.FFMPEG;
        try
        {
            BBDownTMuxer.FFMPEG = ffmpeg;
            Assert.Equal(0, BBDownTMuxer.MuxAV(false, "chapter-test", "", audio, [], output,
                title: "selected title", lang: "eng", audioOnly: true, points: points));
        }
        finally
        {
            BBDownTMuxer.FFMPEG = previousFfmpeg;
        }

        using var probe = JsonDocument.Parse(RunTool(ffprobe, "-v", "error", "-show_chapters",
            "-show_streams", "-show_format", "-of", "json", output));
        var chapters = probe.RootElement.GetProperty("chapters").EnumerateArray().ToArray();
        Assert.Equal(points.Select(point => point.title),
            chapters.Select(chapter => chapter.GetProperty("tags").GetProperty("title").GetString()));
        Assert.Equal(new[] { "0.000000", "1.000000" },
            chapters.Select(chapter => chapter.GetProperty("start_time").GetString()));
        Assert.Equal(new[] { "1.000000", "2.000000" },
            chapters.Select(chapter => chapter.GetProperty("end_time").GetString()));
        Assert.Equal("selected title", probe.RootElement.GetProperty("format")
            .GetProperty("tags").GetProperty("title").GetString());
        var stream = Assert.Single(probe.RootElement.GetProperty("streams").EnumerateArray(),
            stream => stream.GetProperty("codec_type").GetString() == "audio");
        Assert.Equal("eng", stream.GetProperty("tags").GetProperty("language").GetString());
        Assert.NotEqual("source handler", stream.GetProperty("tags").GetProperty("handler_name").GetString());
    }

    private static string RunTool(string executable, params string[] arguments)
    {
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(10_000))
        {
            process.Kill(entireProcessTree: true);
            process.WaitForExit();
            Task.WhenAll(output, error).GetAwaiter().GetResult();
            Assert.Fail(executable + " timed out");
        }
        Task.WhenAll(output, error).GetAwaiter().GetResult();
        Assert.True(process.ExitCode == 0, error.Result);
        return output.Result;
    }
}

public sealed class ChapterMuxFactAttribute : FactAttribute
{
    public ChapterMuxFactAttribute()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("BBDOWN_CHAPTER_TEST_DIR")))
            Skip = "设置 BBDOWN_CHAPTER_TEST_DIR 为独立空目录后执行 FFmpeg 混流回归";
        else if (BBDownTUtil.FindExecutable("ffmpeg") is null || BBDownTUtil.FindExecutable("ffprobe") is null)
            Skip = "FFmpeg 混流回归需要 ffmpeg 和 ffprobe";
    }
}
