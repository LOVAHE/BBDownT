using System.Security.Cryptography;
using System.Text;

namespace BBDownT.Tests;

public class TrackClipMergeTests
{
    private const string Url = "https://cdn.test/track";
    private static readonly BBDownTDownloadUtil.DownloadConfig Config = new() { ResourceIdentity = "track" };

    [Fact]
    public async Task UsesManifestOrderAndPreservesOtherPagesTracksAndResumeSidecars()
    {
        using var files = new MediaTestDirectory();
        var first = await WriteClip(files, "00000_10.P2.200.vclip", "first", 0, 11);
        var second = await WriteClip(files, "00001_10.P2.200.vclip", "second", 5, 11);
        var oldPage = files.Write("00000_10.P1.100.vclip", "P1 resume");
        var audio = files.Write("00000_10.P2.200.aclip", "audio resume");
        var audioResume = files.Write("00000_10.P2.200.aclip.resume", "audio validator");
        var oldExtra = files.Write("00009_10.P2.200.vclip", "old quality fragment");
        var destination = files.FilePath("10.P2.200.mp4");
        files.FilePath("10.P2.200.mp4.resume");

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            BBDownTDownloadUtil.MergeTrackClipsAsync([second, first], destination, Config));
        Assert.False(File.Exists(destination));

        await BBDownTDownloadUtil.MergeTrackClipsAsync([first, second], destination, Config);

        Assert.Equal("firstsecond", File.ReadAllText(destination));
        Assert.True((await DownloadResumeState.LoadAsync(destination + ".resume"))!.Complete);
        Assert.False(File.Exists(first));
        Assert.False(File.Exists(second));
        Assert.False(File.Exists(first + ".resume"));
        Assert.False(File.Exists(second + ".resume"));
        Assert.Equal("P1 resume", File.ReadAllText(oldPage));
        Assert.Equal("audio resume", File.ReadAllText(audio));
        Assert.Equal("audio validator", File.ReadAllText(audioResume));
        Assert.Equal("old quality fragment", File.ReadAllText(oldExtra));
    }

    [Fact]
    public async Task MissingFragmentKeepsAllAvailableInputsAndTheExistingDestination()
    {
        using var files = new MediaTestDirectory();
        var first = await WriteClip(files, "00000_track.vclip", "available", 0, 18);
        var missing = files.FilePath("00001_track.vclip");
        var destination = files.Write("track.mp4", "previous media");

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            BBDownTDownloadUtil.MergeTrackClipsAsync([first, missing], destination, Config));

        Assert.Equal("available", File.ReadAllText(first));
        Assert.True(File.Exists(first + ".resume"));
        Assert.Equal("previous media", File.ReadAllText(destination));
        Assert.Equal(3, Directory.GetFiles(files.Root).Length);
    }

    [Fact]
    public async Task SingleFragmentCompletesWithoutTouchingACompletedSiblingTrack()
    {
        using var files = new MediaTestDirectory();
        var clip = await WriteClip(files, "00000_track.aclip", "audio", 0, 5);
        var sibling = files.Write("video.mp4", "video");
        var destination = files.FilePath("audio.m4a");
        files.FilePath("audio.m4a.resume");

        await BBDownTDownloadUtil.MergeTrackClipsAsync([clip], destination, Config);

        Assert.False(File.Exists(clip));
        Assert.False(File.Exists(clip + ".resume"));
        Assert.Equal("audio", File.ReadAllText(destination));
        Assert.Equal("video", File.ReadAllText(sibling));
    }

    private static async Task<string> WriteClip(MediaTestDirectory files, string name, string contents, long from, long total)
    {
        var path = files.Write(name, contents);
        files.FilePath(name + ".resume");
        var bytes = Encoding.UTF8.GetBytes(contents);
        await new DownloadResumeState(Config.ResourceIdentity!, DownloadResumeState.SourceHash(Url), from,
            from + bytes.Length - 1, total, true, bytes.Length, Convert.ToHexString(SHA256.HashData(bytes)),
            new("\"version\"", null)).SaveAsync(path + ".resume");
        return path;
    }
}
