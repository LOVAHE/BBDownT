using System.Net;
using System.Net.Http.Headers;

namespace BBDownT.Tests;

public class RangeDownloadTests
{
    [Theory]
    [InlineData(20L * 1024 * 1024 - 1, 1)]
    [InlineData(20L * 1024 * 1024, 1)]
    [InlineData(20L * 1024 * 1024 + 1, 2)]
    [InlineData(2L * 20 * 1024 * 1024, 2)]
    public void GetAllClips_CoversFileExactlyOnce(long fileSize, int expectedCount)
    {
        var clips = BBDownTDownloadUtil.GetAllClips("https://example.test/media", fileSize);

        Assert.Equal(expectedCount, clips.Count);
        Assert.Equal(0, clips[0].from);
        Assert.Equal(fileSize - 1, clips[^1].to);
        Assert.Equal(fileSize, clips.Sum(clip => clip.to - clip.from + 1));
        for (var index = 1; index < clips.Count; index++)
        {
            Assert.Equal(clips[index - 1].to + 1, clips[index].from);
        }
    }

    [Fact]
    public async Task DeleteStaleClipFiles_RemovesOnlyGeneratedClipsForDestination()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"bbdownt-clips-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var destination = Path.Combine(directory, "video.mp4");
        var matching = Path.Combine(directory, "00000_video.vclip");
        var matchingSidecar = matching + ".resume";
        var orphanSidecar = Path.Combine(directory, "00001_video.vclip.resume");
        var otherDestination = Path.Combine(directory, "00000_other.vclip");
        var otherSidecar = otherDestination + ".resume";
        var userFile = Path.Combine(directory, "notes.txt");
        var shortUserFile = Path.Combine(directory, "a");
        try
        {
            await File.WriteAllTextAsync(matching, "generated");
            await File.WriteAllTextAsync(matchingSidecar, "validator");
            await File.WriteAllTextAsync(orphanSidecar, "orphan-validator");
            await File.WriteAllTextAsync(otherDestination, "other");
            await File.WriteAllTextAsync(otherSidecar, "other-validator");
            await File.WriteAllTextAsync(userFile, "user");
            await File.WriteAllTextAsync(shortUserFile, "short-user");

            Assert.Equal(3, BBDownTDownloadUtil.DeleteStaleClipFiles(destination));
            Assert.False(File.Exists(matching));
            Assert.False(File.Exists(matchingSidecar));
            Assert.False(File.Exists(orphanSidecar));
            Assert.True(File.Exists(otherDestination));
            Assert.True(File.Exists(otherSidecar));
            Assert.True(File.Exists(userFile));
            Assert.True(File.Exists(shortUserFile));
        }
        finally
        {
            if (File.Exists(matching)) File.Delete(matching);
            if (File.Exists(matchingSidecar)) File.Delete(matchingSidecar);
            if (File.Exists(orphanSidecar)) File.Delete(orphanSidecar);
            if (File.Exists(otherDestination)) File.Delete(otherDestination);
            if (File.Exists(otherSidecar)) File.Delete(otherSidecar);
            if (File.Exists(userFile)) File.Delete(userFile);
            if (File.Exists(shortUserFile)) File.Delete(shortUserFile);
            Directory.Delete(directory);
        }
    }


    [Theory]
    [InlineData(0, 256, 256)]
    [InlineData(128, 256, 384)]
    [InlineData(128, 0, 128)]
    public void GetExpectedTempLength_IncludesExistingPartialBytes(
        long existingLength,
        long responseLength,
        long expected)
    {
        Assert.Equal(
            expected,
            BBDownTDownloadUtil.GetExpectedTempLength(existingLength, responseLength));
    }

    [Fact]
    public void GetExpectedTempLength_AllowsUnknownResponseLength()
    {
        Assert.Null(BBDownTDownloadUtil.GetExpectedTempLength(128, null));
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0L)]
    [InlineData(-1L)]
    public void EnsureKnownPositiveFileSize_RejectsUnavailableLength(long? contentLength)
    {
        Assert.Throws<InvalidDataException>(
            () => BBDownTDownloadUtil.EnsureKnownPositiveFileSize(contentLength));
    }

    [Fact]
    public void EnsureKnownPositiveFileSize_ReturnsPositiveLength()
    {
        Assert.Equal(1024, BBDownTDownloadUtil.EnsureKnownPositiveFileSize(1024));
    }

    [Fact]
    public void GetTotalFileSize_UsesFullLengthFromPartialResponse()
    {
        var contentRange = new ContentRangeHeaderValue(0, 99, 1024);

        Assert.Equal(
            1024,
            BBDownTDownloadUtil.GetTotalFileSize(
                HttpStatusCode.PartialContent,
                100,
                contentRange));
    }

    [Fact]
    public void GetTotalFileSize_RejectsUnexpectedSuccessfulStatus()
    {
        Assert.Throws<InvalidDataException>(() =>
            BBDownTDownloadUtil.GetTotalFileSize(HttpStatusCode.NoContent, null, null));
    }
}
