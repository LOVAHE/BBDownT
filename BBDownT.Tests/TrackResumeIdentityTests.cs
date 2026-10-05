using static BBDownT.Core.Entity.Entity;

namespace BBDownT.Tests;

public class TrackResumeIdentityTests
{
    [Fact]
    public void RefreshedSignedUrl_KeepsTheSelectedTrackIdentity()
    {
        var page = IntlPage("13287667");
        var video = VideoTrack();
        var first = Program.GetTrackResumeIdentity(page, "INTL", "video", video: video);
        video.baseUrl = "https://other-cdn.test/video?signature=refreshed";

        Assert.Equal(first, Program.GetTrackResumeIdentity(page, "INTL", "video", video: video));
        Assert.Matches("^[a-f0-9]{64}$", first);
    }

    [Theory]
    [InlineData("quality")]
    [InlineData("codec")]
    [InlineData("resolution")]
    [InlineData("fps")]
    [InlineData("bandwidth")]
    public void ChangedRepresentation_DoesNotReuseTheOldTrackIdentity(string change)
    {
        var page = IntlPage("13287667");
        var video = VideoTrack();
        var first = Program.GetTrackResumeIdentity(page, "INTL", "video", video: video);
        switch (change)
        {
            case "quality": video.id = "64"; break;
            case "codec": video.codecs = "AVC"; break;
            case "resolution": video.res = "1280x720"; break;
            case "fps": video.fps = "60"; break;
            case "bandwidth": video.bandwith++; break;
        }

        Assert.NotEqual(first, Program.GetTrackResumeIdentity(page, "INTL", "video", video: video));
    }

    [Fact]
    public void EpisodeAndSite_ArePartOfTheIdentity()
    {
        var first = Program.GetTrackResumeIdentity(IntlPage("13287667"), "INTL", "video", video: VideoTrack());

        Assert.NotEqual(first, Program.GetTrackResumeIdentity(IntlPage("13287745"), "INTL", "video", video: VideoTrack()));
        Assert.NotEqual(first, Program.GetTrackResumeIdentity(IntlPage("13287667"), "WEB", "video", video: VideoTrack()));
    }

    [Fact]
    public void AudioRoleAndLanguage_ArePartOfTheIdentity()
    {
        var page = IntlPage("13287667");
        var audio = new Audio
        {
            id = "1", dfn = "audio", baseUrl = "https://cdn.test/audio", codecs = "M4A", bandwith = 160000, dur = 600
        };
        var first = Program.GetTrackResumeIdentity(page, "INTL", "audio", audio: audio, variant: "en");

        Assert.NotEqual(first, Program.GetTrackResumeIdentity(page, "INTL", "background-audio", audio: audio, variant: "en"));
        Assert.NotEqual(first, Program.GetTrackResumeIdentity(page, "INTL", "audio", audio: audio, variant: "th"));
    }

    [Fact]
    public void MuxInputCleanup_RemovesTheCorrespondingResumeRecord()
    {
        var deleted = new List<string>();
        var input = Path.GetFullPath("cache/track.mp4");
        var final = Path.GetFullPath("output/final.mp4");

        MediaOutput.DeleteInput(input, final, deleted.Add);

        Assert.Equal(new[] { input, input + ".resume" }, deleted);
    }

    [Fact]
    public void MuxInputCleanup_PreservesBothDataAndRecordWhenPathsCoincide()
    {
        var final = Path.GetFullPath("cache/track.mp4");

        MediaOutput.DeleteInput(final, final, _ => throw new Exception("Final media must be preserved"));
    }

    private static Page IntlPage(string episode) => new(1, "", "", episode, "E1", 0, "", 0);

    private static Video VideoTrack() => new()
    {
        id = "80", dfn = "1080P", baseUrl = "https://cdn.test/video?signature=original",
        codecs = "HEVC", res = "1920x1080", fps = "30", bandwith = 2400000, dur = 600
    };
}
