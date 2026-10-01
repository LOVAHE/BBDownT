namespace BBDownT.Tests;

public class FlvMergeTests
{
    [Fact]
    public void MergesOnlyConvertedManifestSegmentsAndCleansOnlyItsOwnFiles()
    {
        using var files = new MediaTestDirectory();
        var first = files.Write("10.P2.200.0.mp4", "first");
        var second = files.Write("10.P2.200.1.mp4", "second");
        var otherPage = files.Write("10.P1.100.0.mp4", "other page");
        var oldTs = files.Write("old.ts", "old conversion");
        var destination = files.FilePath("10.P2.200.mp4");
        var converted = new List<string>();

        BBDownTMuxer.MergeFLV([first, second], destination, (input, output) =>
        {
            converted.Add(output);
            File.WriteAllText(output, "ts:" + File.ReadAllText(input));
            return 0;
        });

        Assert.Equal("ts:firstts:second", File.ReadAllText(destination));
        Assert.False(File.Exists(first));
        Assert.False(File.Exists(second));
        Assert.All(converted, path => Assert.False(File.Exists(path)));
        Assert.Equal("other page", File.ReadAllText(otherPage));
        Assert.Equal("old conversion", File.ReadAllText(oldTs));
    }

    [Fact]
    public void FailedConversionPreservesEverySourceAndExistingMergedOutput()
    {
        using var files = new MediaTestDirectory();
        var first = files.Write("first.mp4", "first");
        var second = files.Write("second.mp4", "second");
        var destination = files.Write("merged.mp4", "previous");
        var calls = 0;

        Assert.Throws<IOException>(() => BBDownTMuxer.MergeFLV([first, second], destination, (input, output) =>
        {
            File.WriteAllText(output, "partial TS");
            return ++calls == 1 ? 0 : 1;
        }));

        Assert.Equal("first", File.ReadAllText(first));
        Assert.Equal("second", File.ReadAllText(second));
        Assert.Equal("previous", File.ReadAllText(destination));
        Assert.Equal(3, Directory.GetFiles(files.Root).Length);
    }

    [Fact]
    public void SuccessfulExitWithoutConvertedMediaIsStillAFailure()
    {
        using var files = new MediaTestDirectory();
        var first = files.Write("first.mp4", "first");
        var second = files.Write("second.mp4", "second");
        var destination = files.FilePath("merged.mp4");

        Assert.Throws<IOException>(() => BBDownTMuxer.MergeFLV([first, second], destination, (_, _) => 0));

        Assert.Equal("first", File.ReadAllText(first));
        Assert.Equal("second", File.ReadAllText(second));
        Assert.False(File.Exists(destination));
    }

    [Fact]
    public void SingleSegmentReplacesOldMergedFileWithoutInvokingAConverter()
    {
        using var files = new MediaTestDirectory();
        var input = files.Write("segment.mp4", "new media");
        var destination = files.Write("merged.mp4", "old media");

        BBDownTMuxer.MergeFLV([input], destination, (_, _) => throw new Exception("A single segment must not convert"));

        Assert.Equal("new media", File.ReadAllText(destination));
        Assert.False(File.Exists(input));
    }
}
