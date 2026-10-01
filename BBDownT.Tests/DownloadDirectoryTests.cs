namespace BBDownT.Tests;

public class DownloadDirectoryTests
{
    [Fact]
    public void CleanupRetainsNestedResumeDataWhenTheParentHasNoDirectFiles()
    {
        using var files = new MediaTestDirectory();
        var nested = files.CreateDirectory("another-page");
        var resume = files.Write("another-page/track.vclip", "resume data");

        Program.DeleteEmptyDownloadDirectory(files.Root);

        Assert.Equal("resume data", File.ReadAllText(resume));
        Assert.True(Directory.Exists(nested));
    }

    [Fact]
    public void CleanupRemovesOnlyAnEmptyDirectoryAndAllowsAnAbsentDirectory()
    {
        using var files = new MediaTestDirectory();
        var empty = files.CreateDirectory("empty-page");

        Program.DeleteEmptyDownloadDirectory(empty);
        Program.DeleteEmptyDownloadDirectory(empty);

        Assert.False(Directory.Exists(empty));
        Assert.True(Directory.Exists(files.Root));
    }
}
