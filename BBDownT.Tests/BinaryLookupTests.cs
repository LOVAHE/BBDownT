namespace BBDownT.Tests;

public class BinaryLookupTests
{
    [Fact]
    public void ExplicitToolPaths_StillWorkAfterTheWorkingDirectoryChanges()
    {
        using var files = new MediaTestDirectory();
        files.CreateDirectory("tools");
        files.Write("tools/ffmpeg", "ffmpeg");
        files.Write("tools/aria2c", "aria2c");
        var previous = (Environment.CurrentDirectory, BBDownTMuxer.FFMPEG, BBDownTAria2c.ARIA2C);
        try
        {
            Environment.CurrentDirectory = files.Root;
            Program.FindBinaries(new MyOption { FFmpegPath = "tools/ffmpeg", Aria2cPath = "tools/aria2c", SkipMux = true });
            Environment.CurrentDirectory = previous.CurrentDirectory;

            Assert.Equal("ffmpeg", File.ReadAllText(BBDownTMuxer.FFMPEG));
            Assert.Equal("aria2c", File.ReadAllText(BBDownTAria2c.ARIA2C));
        }
        finally
        {
            (Environment.CurrentDirectory, BBDownTMuxer.FFMPEG, BBDownTAria2c.ARIA2C) = previous;
        }
    }

    [Fact]
    public void Mp4box_IsFoundUnderTheNameGpacInstalls()
    {
        using var files = new MediaTestDirectory();
        files.Write("MP4Box" + (OperatingSystem.IsWindows() ? ".exe" : ""), "gpac");
        var previous = (Environment.CurrentDirectory, BBDownTMuxer.MP4BOX);
        try
        {
            Environment.CurrentDirectory = files.Root;
            BBDownTMuxer.MP4BOX = "missing-mp4box";

            var resolved = Program.ResolveMp4box();

            Assert.NotNull(resolved);
            Assert.Equal(resolved, BBDownTMuxer.MP4BOX);
            Assert.Equal("gpac", File.ReadAllText(resolved));
        }
        finally
        {
            (Environment.CurrentDirectory, BBDownTMuxer.MP4BOX) = previous;
        }
    }
}
