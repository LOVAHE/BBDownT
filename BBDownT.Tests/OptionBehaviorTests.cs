using BBDownT.Core.Entity;
using System.CommandLine;
using static BBDownT.Core.Entity.Entity;

namespace BBDownT.Tests;

public class OptionBehaviorTests
{
    [Fact]
    public void AudioAndVideoOnly_KeepBothStreamsAndEnableSkipMux()
    {
        var option = new MyOption { AudioOnly = true, VideoOnly = true };
        var result = CreateParsedResult();

        Program.HandleConflictingOptions(option);
        Program.ApplyDashStreamSelection(option, result);

        Assert.True(option.AudioOnly);
        Assert.True(option.VideoOnly);
        Assert.True(option.SkipMux);
        Assert.Single(result.VideoTracks);
        Assert.Single(result.AudioTracks);
        Assert.Empty(result.BackgroundAudioTracks);
        Assert.Empty(result.RoleAudioList);
        Assert.False(Program.NeedsMuxer(option));
    }

    [Theory]
    [InlineData(true, false, 0, 1)]
    [InlineData(false, true, 1, 0)]
    public void IndividualStreamOnlyOption_RemainsExclusive(
        bool audioOnly,
        bool videoOnly,
        int expectedVideoCount,
        int expectedAudioCount)
    {
        var option = new MyOption { AudioOnly = audioOnly, VideoOnly = videoOnly };
        var result = CreateParsedResult();

        Program.ApplyDashStreamSelection(option, result);

        Assert.Equal(expectedVideoCount, result.VideoTracks.Count);
        Assert.Equal(expectedAudioCount, result.AudioTracks.Count);
    }

    [Fact]
    public void PageNumberPadding_UsesOriginalTotalPageCount()
    {
        var page = new Page(7, "2", "3", "", "part", 1, "", 0);

        var path = Program.FormatSavePath(
            "[P<pageNumberWithZero>]",
            "title",
            null,
            null,
            page,
            pagesCount: 120,
            "WEB",
            0);

        Assert.Equal("[P007].mp4", path);
    }

    [Theory]
    [InlineData(new[] { "BV1xx411c7mD", "-e", "HEVC", "-q", "1080P" }, true)]
    [InlineData(new[] { "BV1xx411c7mD", "--dfn-priority=1080P", "--encoding-priority=HEVC" }, false)]
    [InlineData(new[] { "BV1xx411c7mD", "-e", "HEVC" }, true)]
    [InlineData(new[] { "BV1xx411c7mD", "-q", "1080P" }, false)]
    public async Task CommandLinePriority_UsesWrittenAliasOrder(string[] arguments, bool encodingFirst)
    {
        MyOption? captured = null;
        var command = CommandLineInvoker.GetRootCommand(option =>
        {
            captured = option;
            return Task.CompletedTask;
        });

        Assert.Equal(0, await command.InvokeAsync(arguments));
        Assert.NotNull(captured);
        Assert.Equal(encodingFirst, captured.EncodingPriorityFirst);
    }

    [Theory]
    [InlineData(true, "-e HEVC\n-q 1080P\n")]
    [InlineData(true, "--encoding-priority=HEVC\n--dfn-priority=1080P\n")]
    [InlineData(false, "-q 1080P\n-e HEVC\n")]
    public async Task ConfigPriority_UsesWrittenOrder(bool encodingFirst, string config)
    {
        var path = Path.GetTempFileName();
        await File.WriteAllTextAsync(path, config);
        MyOption? captured = null;
        var command = CommandLineInvoker.GetRootCommand(option =>
        {
            captured = option;
            return Task.CompletedTask;
        });
        var arguments = new List<string> { "BV1xx411c7mD", "--config-file", path };
        try
        {
            Assert.True(BBDownTConfigParser.HandleConfig(arguments, command));
            Assert.Equal(0, await command.InvokeAsync(arguments.ToArray()));
            Assert.NotNull(captured);
            Assert.Equal(encodingFirst, captured.EncodingPriorityFirst);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData(true, "-e", "HEVC", "-q 1080P\n")]
    [InlineData(false, "-q", "1080P", "-e HEVC\n")]
    public async Task CommandLinePriority_PrecedesLaterConfigPriority(
        bool encodingFirst,
        string commandOption,
        string commandValue,
        string config)
    {
        var path = Path.GetTempFileName();
        await File.WriteAllTextAsync(path, config);
        MyOption? captured = null;
        var command = CommandLineInvoker.GetRootCommand(option =>
        {
            captured = option;
            return Task.CompletedTask;
        });
        var arguments = new List<string>
        {
            "BV1xx411c7mD", commandOption, commandValue, "--config-file", path
        };
        try
        {
            Assert.True(BBDownTConfigParser.HandleConfig(arguments, command));
            Assert.Equal(0, await command.InvokeAsync(arguments.ToArray()));
            Assert.NotNull(captured);
            Assert.Equal(encodingFirst, captured.EncodingPriorityFirst);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void VideoSorting_UsesSelectedPrimaryPriority()
    {
        var videos = new List<Video>
        {
            CreateVideo("80", "1080P", "AVC"),
            CreateVideo("64", "720P", "HEVC")
        };
        var qualityPriority = new Dictionary<string, int> { ["1080P"] = 0, ["720P"] = 1 };
        var encodingPriority = new Dictionary<string, byte> { ["HEVC"] = 0, ["AVC"] = 1 };

        var encodingFirst = Program.SortTracks(videos, qualityPriority, encodingPriority, false, true);
        var qualityFirst = Program.SortTracks(videos, qualityPriority, encodingPriority, false, false);

        Assert.Equal("HEVC", encodingFirst[0].codecs);
        Assert.Equal("1080P", qualityFirst[0].dfn);
    }

    [Fact]
    public async Task SkipMuxTask_RecordsOnlyExistingNonEmptyStreamFiles()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"bbdownt-streams-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var videoPath = Path.Combine(directory, "video.mp4");
        var audioPath = Path.Combine(directory, "audio.m4a");
        var emptyPath = Path.Combine(directory, "empty.m4a");
        var missingPath = Path.Combine(directory, "missing.m4a");
        var task = new DownloadTask("2", "BV1xx411c7mD", 1);
        try
        {
            await File.WriteAllTextAsync(videoPath, "video");
            await File.WriteAllTextAsync(audioPath, "audio");
            await File.WriteAllTextAsync(emptyPath, "");

            Program.RecordDownloadedStreams(task, videoPath, audioPath, emptyPath, missingPath, "");

            Assert.Equal(new[] { videoPath, audioPath }, task.CreateSnapshot().SavePaths);
        }
        finally
        {
            if (File.Exists(videoPath)) File.Delete(videoPath);
            if (File.Exists(audioPath)) File.Delete(audioPath);
            if (File.Exists(emptyPath)) File.Delete(emptyPath);
            Directory.Delete(directory);
        }
    }

    [Fact]
    public async Task SkipMux_IgnoresMuxedOutputCache()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"bbdownt-cache-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var muxedPath = Path.Combine(directory, "title.mp4");
        var option = new MyOption { AudioOnly = true, VideoOnly = true };
        Program.HandleConflictingOptions(option);
        try
        {
            await File.WriteAllTextAsync(muxedPath, "old-muxed-output");

            Assert.False(Program.ShouldUseMuxedOutputCache(option, muxedPath));
            Assert.True(Program.ShouldUseMuxedOutputCache(new MyOption(), muxedPath));
        }
        finally
        {
            if (File.Exists(muxedPath)) File.Delete(muxedPath);
            Directory.Delete(directory);
        }
    }

    private static ParsedResult CreateParsedResult()
    {
        var audio = new Audio
        {
            id = "30280",
            dfn = "192K",
            baseUrl = "audio",
            codecs = "M4A",
            bandwith = 192000,
            dur = 1
        };
        return new ParsedResult
        {
            VideoTracks = [CreateVideo("80", "1080P", "AVC")],
            AudioTracks = [audio],
            BackgroundAudioTracks = [audio],
            RoleAudioList =
            [
                new AudioMaterialInfo
                {
                    title = "role",
                    personName = "person",
                    path = "role.m4a",
                    audio = [audio]
                }
            ]
        };
    }

    private static Video CreateVideo(string id, string dfn, string codecs)
    {
        return new Video
        {
            id = id,
            dfn = dfn,
            baseUrl = "video",
            codecs = codecs,
            bandwith = 1,
            dur = 1
        };
    }
}
