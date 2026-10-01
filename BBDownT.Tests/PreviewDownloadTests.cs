using BBDownT.Core.Entity;

namespace BBDownT.Tests;

public class PreviewDownloadTests
{
    [Fact]
    public void PreviewMediaCannotProceedRegardlessOfMuxAndMediaSelectionOptions()
    {
        var preview = new ParsedResult { IsPreviewOnly = true };
        foreach (var option in new[] { new MyOption(), new MyOption { SkipMux = true },
            new MyOption { AudioOnly = true }, new MyOption { VideoOnly = true } })
            Assert.True(Program.StopPreviewDownload(option, preview));
    }

    [Fact]
    public void NonMediaRequestsAndCompleteMediaAreAllowed()
    {
        var preview = new ParsedResult { IsPreviewOnly = true };
        Assert.False(Program.StopPreviewDownload(new() { OnlyShowInfo = true }, preview));
        Assert.False(Program.StopPreviewDownload(new() { SubOnly = true }, preview));
        Assert.False(Program.StopPreviewDownload(new(), new ParsedResult()));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AttachmentModesAllowDashArtifactsButDoNotEnterTheDurlMediaPath(bool cover)
    {
        var option = new MyOption { CoverOnly = cover, DanmakuOnly = !cover };
        var preview = new ParsedResult { IsPreviewOnly = true };
        Assert.False(Program.StopPreviewDownload(option, preview));

        preview.Clips.Add("https://cdn.test/preview.mp4");
        Assert.True(Program.StopPreviewDownload(option, preview));
    }
}
