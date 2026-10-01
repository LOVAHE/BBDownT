namespace BBDownT.Tests;

public class MediaOutputTests
{
    [Theory]
    [InlineData("result.mp4")]
    [InlineData("音频 sample.m4a")]
    public void SuccessPublishesOnlyAfterWriterCompletes(string name)
    {
        using var files = new MediaTestDirectory();
        var destination = files.FilePath(name);
        string? staged = null;

        Assert.True(MediaOutput.Write(destination, path =>
        {
            staged = path;
            Assert.Equal(files.Root, Path.GetDirectoryName(path));
            Assert.Equal(Path.GetExtension(destination), Path.GetExtension(path));
            File.WriteAllText(path, "completed media");
            Assert.False(File.Exists(destination));
            return 0;
        }));

        Assert.Equal("completed media", File.ReadAllText(destination));
        Assert.False(File.Exists(staged));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NonzeroExitCannotPublishPartialOutputOrOverwriteAnExistingFile(bool existing)
    {
        using var files = new MediaTestDirectory();
        var destination = files.FilePath("result.mp4");
        if (existing) File.WriteAllText(destination, "previous media");
        var unrelated = files.Write("other.partial.mp4", "unrelated");
        string? staged = null;

        Assert.False(MediaOutput.Write(destination, path =>
        {
            staged = path;
            File.WriteAllText(path, "partial media");
            return 1;
        }));

        Assert.False(File.Exists(staged));
        Assert.Equal(existing, File.Exists(destination));
        if (existing) Assert.Equal("previous media", File.ReadAllText(destination));
        Assert.Equal("unrelated", File.ReadAllText(unrelated));
        Assert.Equal(existing, Program.ShouldUseMuxedOutputCache(new(), destination));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ZeroExitRequiresANonemptyOutput(bool createEmpty)
    {
        using var files = new MediaTestDirectory();
        var destination = files.FilePath("result.mp4");

        Assert.False(MediaOutput.Write(destination, path =>
        {
            if (createEmpty) File.WriteAllText(path, "");
            return 0;
        }));

        Assert.Empty(Directory.EnumerateFileSystemEntries(files.Root));
    }

    [Fact]
    public void WriterExceptionPreservesInputAndCleansStagedOutput()
    {
        using var files = new MediaTestDirectory();
        var input = files.Write("downloaded.mp4", "input media");
        var destination = files.FilePath("result.mp4");
        var failure = new IOException("mux interrupted");

        var actual = Assert.Throws<IOException>(() => MediaOutput.Write(destination, path =>
        {
            File.WriteAllText(path, "partial media");
            throw failure;
        }));

        Assert.Same(failure, actual);
        Assert.Equal("input media", File.ReadAllText(input));
        Assert.Equal(new[] { input }, Directory.GetFiles(files.Root));
    }

    [Fact]
    public void CommitFailureKeepsTheExistingDestinationAndRemovesTheStagedFile()
    {
        using var files = new MediaTestDirectory();
        var destination = files.CreateDirectory("result.mp4");

        var error = Record.Exception(() => MediaOutput.Write(destination, path =>
        {
            File.WriteAllText(path, "completed media");
            return 0;
        }));

        Assert.True(error is IOException or UnauthorizedAccessException);
        Assert.True(Directory.Exists(destination));
        Assert.Empty(Directory.GetFiles(files.Root));
    }

    [Fact]
    public void CleanupDoesNotDeleteTheFinalFileWhenInputAndDestinationCoincide()
    {
        using var files = new MediaTestDirectory();
        var destination = files.Write("result.mp4", "completed media");
        var input = files.Write("track.m4a", "raw audio");

        MediaOutput.DeleteInput(destination, Path.Combine(files.Root, ".", "result.mp4"));
        MediaOutput.DeleteInput(input, destination);

        Assert.Equal("completed media", File.ReadAllText(destination));
        Assert.False(File.Exists(input));
    }
}
