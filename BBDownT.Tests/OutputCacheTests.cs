namespace BBDownT.Tests;

public class OutputCacheTests
{
    [Theory]
    [InlineData(false, false, true, true)]
    [InlineData(false, false, false, false)]
    [InlineData(true, false, true, false)]
    [InlineData(false, true, true, false)]
    public void MuxedCache_RequiresCompleteMediaModeAndAnArtifact(
        bool skipMux, bool infoOnly, bool usable, bool expected)
    {
        var option = new MyOption { SkipMux = skipMux, OnlyShowInfo = infoOnly };

        Assert.Equal(expected, Program.ShouldUseMuxedOutputCache(option, "output.mp4", _ => usable));
    }

    [Fact]
    public void RawMode_DoesNotInspectTheMuxedArtifact()
    {
        Assert.False(Program.ShouldUseMuxedOutputCache(new MyOption { SkipMux = true }, "output.mp4",
            _ => throw new InvalidOperationException("Raw mode cannot use a muxed-file cache.")));
    }

    [Fact]
    public void RequiresAnExistingNonemptyArtifact()
    {
        using var files = new MediaTestDirectory();
        var path = files.FilePath("title.mp4");
        Assert.False(Program.IsUsableArtifact(path));
        File.WriteAllText(path, "");
        Assert.False(Program.IsUsableArtifact(path));
        File.WriteAllText(path, "completed media");
        Assert.True(Program.IsUsableArtifact(path));
    }

    [Fact]
    public void RawStreamModeDoesNotSkipBecauseAMuxedFileExists()
    {
        using var files = new MediaTestDirectory();
        var path = files.Write("title.mp4", "completed media");
        var option = new MyOption { AudioOnly = true, VideoOnly = true };
        Program.HandleConflictingOptions(option);

        Assert.False(Program.ShouldUseMuxedOutputCache(option, path));
        Assert.True(Program.ShouldUseMuxedOutputCache(new(), path));
    }
}
