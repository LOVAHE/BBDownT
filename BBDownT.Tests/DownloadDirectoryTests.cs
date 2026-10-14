using static BBDownT.Core.Entity.Entity;

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

    [Fact]
    public void SharedCoverIsKeptUntilTheLastSelectedPageOfItsDownloadDirectory()
    {
        Page a1 = new(1, "10", "1", "", "P1", 1, 0), b1 = new(1, "20", "2", "", "P1", 1, 0),
            a2 = new(2, "10", "3", "", "P2", 1, 0), unselected = new(3, "10", "4", "", "P3", 1, 0);
        var selected = new List<Page> { a1, b1, a2 };

        Assert.False(Program.IsLastPageUsingDownloadDirectory(selected, a1));
        Assert.True(Program.IsLastPageUsingDownloadDirectory(selected, b1));
        Assert.True(Program.IsLastPageUsingDownloadDirectory(selected, a2));
        Assert.True(Program.IsLastPageUsingDownloadDirectory(selected, unselected));
    }

    [Fact]
    public void LeftoverCoversAreRemovedWithoutTouchingResumeData()
    {
        using var files = new MediaTestDirectory();
        files.CreateDirectory("10");
        files.CreateDirectory("20");
        File.WriteAllText(Path.Combine(files.Root, "10", "10.jpg"), "cover");
        var cover = Path.Combine(files.Root, "20", "20.jpg");
        File.WriteAllText(cover, "cover");
        var track = files.Write("20/20.P1.2.mp4", "partial track");
        var pages = new List<Page>
        {
            new(1, "10", "1", "", "P1", 1, 0), new(2, "10", "2", "", "P2", 1, 0),
            new(1, "20", "2", "", "P1", 1, 0), new(1, "30", "3", "", "P1", 1, 0)
        };
        var previous = Environment.CurrentDirectory;
        try
        {
            Environment.CurrentDirectory = files.Root;
            Program.DeleteLeftoverCovers(pages, null);
        }
        finally
        {
            Environment.CurrentDirectory = previous;
        }

        Assert.False(Directory.Exists(Path.Combine(files.Root, "10")));
        Assert.False(File.Exists(cover));
        Assert.Equal("partial track", File.ReadAllText(track));
    }
}
