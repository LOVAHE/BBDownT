using static BBDownT.Core.Entity.Entity;

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

    [Theory]
    [InlineData(5, "Title/[P1]Part.mp4")]
    [InlineData(120, "Title/[P001]Part.mp4")]
    [InlineData(12, "Title/[P1]Part.mp4")]
    [InlineData(12, "Title/[P001]Part.mp4")]
    [InlineData(1200, "Title/[P0001]Part.mp4")]
    public void PageNumbersPaddedByVersion217AreFound(int pagesCount, string legacyName)
    {
        using var files = new MediaTestDirectory();
        files.CreateDirectory("Title");
        files.Write(legacyName, "completed media");
        var page = new Page(1, "10", "1", "", "Part", 1, 0);

        var found = InDirectory(files.Root, () => Program.FindLegacyOutput(new MyOption(), "Title/[P01]Part.mp4",
            Program.MultiPageDefaultSavePath, "Title", null, null, page, pagesCount, "WEB", 0, null));

        Assert.Equal(legacyName, found);
    }

    [Fact]
    public void UncappedNamesFromVersion217AreFound()
    {
        using var files = new MediaTestDirectory();
        var title = new string('测', 80);
        files.Write(title + ".mp4", "completed media");
        var page = new Page(1, "10", "1", "", "P1", 1, 0);
        var savePath = Program.FormatSavePath(Program.SinglePageDefaultSavePath, title, null, null, page, 1, "WEB", 0);

        var found = InDirectory(files.Root, () => Program.FindLegacyOutput(new MyOption(), savePath,
            Program.SinglePageDefaultSavePath, title, null, null, page, 1, "WEB", 0, null));

        Assert.NotEqual(title + ".mp4", savePath);
        Assert.Equal(title + ".mp4", found);
    }

    [Fact]
    public void DeprecatedDfnSuffixKeepsItsPlaceInTheLegacyName()
    {
        using var files = new MediaTestDirectory();
        files.CreateDirectory("Title");
        files.Write("Title/[P1]Part.mp4", "another rendition");
        files.Write("Title/[P1]Part[1080P 高清].mp4", "completed media");
        var page = new Page(1, "10", "1", "", "Part", 1, 0);
        var video = new Video { id = "80", dfn = "1080P 高清", baseUrl = "", codecs = "HEVC" };
        var previous = Program.MultiPageDefaultSavePath;
        try
        {
            Program.MultiPageDefaultSavePath += "[<dfn>]";
            var found = InDirectory(files.Root, () => Program.FindLegacyOutput(new MyOption(), "Title/[P01]Part[1080P 高清].mp4",
                Program.MultiPageDefaultSavePath, "Title", video, null, page, 5, "WEB", 0, null));

            Assert.Equal("Title/[P1]Part[1080P 高清].mp4", found);
        }
        finally
        {
            Program.MultiPageDefaultSavePath = previous;
        }
    }

    [Fact]
    public void AnOverlongLegacyNameUnderAServerRootIsIgnored()
    {
        using var files = new MediaTestDirectory();
        var title = new string('测', 100);
        var page = new Page(1, "10", "1", "", "P1", 1, 0);
        var option = new MyOption { RestrictedOutputRoot = files.Root };
        var savePath = Program.FormatSavePath(Program.SinglePageDefaultSavePath, title, null, null, page, 1, "WEB", 0, files.Root);

        Assert.Null(InDirectory(files.Root, () => Program.FindLegacyOutput(option, savePath,
            Program.SinglePageDefaultSavePath, title, null, null, page, 1, "WEB", 0, null)));
    }

    [Fact]
    public void AnotherPagesFileIsNotMistakenForThisPage()
    {
        using var files = new MediaTestDirectory();
        files.CreateDirectory("Title");
        files.Write("Title/[P10]Part.mp4", "another page");
        files.Write("Title/[P010]Part.mp4", "another page");
        var page = new Page(1, "10", "1", "", "Part", 1, 0);

        Assert.Null(InDirectory(files.Root, () => Program.FindLegacyOutput(new MyOption(), "Title/[P01]Part.mp4",
            Program.MultiPageDefaultSavePath, "Title", null, null, page, 120, "WEB", 0, null)));
    }

    [Fact]
    public void CappedNamesFromEarlierBetasAreFound()
    {
        using var files = new MediaTestDirectory();
        var page = new Page(1, "10", "1", "", new string('测', 80), 1, 0);
        var betaName = Program.FormatSavePath(
            Program.MultiPageDefaultSavePath.Replace("<pageNumberWithZero:2>", "<pageNumberWithZero>"), "Title", null, null, page, 5, "WEB", 0);
        files.CreateDirectory("Title");
        files.Write(betaName, "completed media");
        var savePath = Program.FormatSavePath(Program.MultiPageDefaultSavePath, "Title", null, null, page, 5, "WEB", 0);

        Assert.DoesNotContain(page.title, betaName);
        Assert.Equal(betaName, InDirectory(files.Root, () => Program.FindLegacyOutput(new MyOption(), savePath,
            Program.MultiPageDefaultSavePath, "Title", null, null, page, 5, "WEB", 0, null)));
    }

    [Fact]
    public void VideoOnlyHasNoLegacyFallback()
    {
        using var files = new MediaTestDirectory();
        files.CreateDirectory("Title");
        files.Write("Title/[P1]Part.mp4", "completed media");
        var page = new Page(1, "10", "1", "", "Part", 1, 0);

        Assert.Null(InDirectory(files.Root, () => Program.FindLegacyOutput(new MyOption { VideoOnly = true },
            "Title/[P01]Part.video.mp4", Program.MultiPageDefaultSavePath, "Title", null, null, page, 5, "WEB", 0, null)));
    }

    [Fact]
    public void TheCurrentNameIsNotReportedAsALegacyName()
    {
        using var files = new MediaTestDirectory();
        files.CreateDirectory("Title");
        files.Write("Title/[P01]Part.mp4", "completed media");
        var page = new Page(1, "10", "1", "", "Part", 1, 0);

        Assert.Null(InDirectory(files.Root, () => Program.FindLegacyOutput(new MyOption(), "Title/[P01]Part.mp4",
            Program.MultiPageDefaultSavePath, "Title", null, null, page, 12, "WEB", 0, null)));
    }

    private static T InDirectory<T>(string directory, Func<T> action)
    {
        var previous = Environment.CurrentDirectory;
        try
        {
            Environment.CurrentDirectory = directory;
            return action();
        }
        finally
        {
            Environment.CurrentDirectory = previous;
        }
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
