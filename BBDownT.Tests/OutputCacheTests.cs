namespace BBDownT.Tests;

public class OutputCacheTests
{
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
