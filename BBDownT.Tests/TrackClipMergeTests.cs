namespace BBDownT.Tests;

public class TrackClipMergeTests
{
    [Fact]
    public void UsesManifestOrderAndPreservesOtherPagesTracksAndResumeSidecars()
    {
        using var files = new MediaTestDirectory();
        var first = files.Write("00000_10.P2.200.vclip", "first");
        var second = files.Write("00001_10.P2.200.vclip", "second");
        var firstResume = files.Write("00000_10.P2.200.vclip.resume", "validator");
        var oldPage = files.Write("00000_10.P1.100.vclip", "P1 resume");
        var audio = files.Write("00000_10.P2.200.aclip", "audio resume");
        var audioResume = files.Write("00000_10.P2.200.aclip.resume", "audio validator");
        var oldExtra = files.Write("00009_10.P2.200.vclip", "old quality fragment");
        var destination = files.FilePath("10.P2.200.mp4");

        BBDownTDownloadUtil.MergeTrackClips([second, first], destination);

        Assert.Equal("secondfirst", File.ReadAllText(destination));
        Assert.False(File.Exists(first));
        Assert.False(File.Exists(second));
        Assert.False(File.Exists(firstResume));
        Assert.Equal("P1 resume", File.ReadAllText(oldPage));
        Assert.Equal("audio resume", File.ReadAllText(audio));
        Assert.Equal("audio validator", File.ReadAllText(audioResume));
        Assert.Equal("old quality fragment", File.ReadAllText(oldExtra));
    }

    [Fact]
    public void MissingFragmentKeepsAllAvailableInputsAndTheExistingDestination()
    {
        using var files = new MediaTestDirectory();
        var first = files.Write("00000_track.vclip", "available");
        var missing = files.FilePath("00001_track.vclip");
        var sidecar = files.Write("00000_track.vclip.resume", "validator");
        var destination = files.Write("track.mp4", "previous media");

        Assert.Throws<FileNotFoundException>(() => BBDownTDownloadUtil.MergeTrackClips([first, missing], destination));

        Assert.Equal("available", File.ReadAllText(first));
        Assert.Equal("validator", File.ReadAllText(sidecar));
        Assert.Equal("previous media", File.ReadAllText(destination));
        Assert.Equal(3, Directory.GetFiles(files.Root).Length);
    }

    [Fact]
    public void SingleFragmentCompletesWithoutTouchingACompletedSiblingTrack()
    {
        using var files = new MediaTestDirectory();
        var clip = files.Write("00000_track.aclip", "audio");
        var sibling = files.Write("video.mp4", "video");
        var destination = files.FilePath("audio.m4a");

        BBDownTDownloadUtil.MergeTrackClips([clip], destination);

        Assert.False(File.Exists(clip));
        Assert.Equal("audio", File.ReadAllText(destination));
        Assert.Equal("video", File.ReadAllText(sibling));
    }
}
