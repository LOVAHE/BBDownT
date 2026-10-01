using BBDownT.Core.Entity;
using static BBDownT.Core.Entity.Entity;

namespace BBDownT.Tests;

public class ProgressiveStreamSelectionTests
{
    [Fact]
    public async Task QualitySwitchUsesTheNewSegmentsAndAcceptedQualityForNaming()
    {
        var initial = Result("16", "old.mp4");
        initial.Dfns = ["16", "80"];
        // The API may accept a lower quality than the one requested.
        var accepted = Result("64", "new-1.mp4", "new-2.mp4");
        var selection = new ProgressiveStreamSelection();
        var requested = new List<string>();
        using var input = new StringReader("1\n");
        using var output = new StringWriter();

        var result = await selection.ChooseAsync(initial, quality =>
        {
            requested.Add(quality);
            return Task.FromResult(accepted);
        }, input, output);

        Assert.Equal(new[] { "80" }, requested);
        Assert.Equal(new[] { "new-1.mp4", "new-2.mp4" }, result.Clips);
        Assert.Equal(new[] { "64" }, result.Dfns);
        Assert.Equal("64", Assert.Single(result.VideoTracks).id);
        var page = new Page(1, "2", "3", "", "part", 1, "", 0);
        Assert.Equal("64.mp4", Program.FormatSavePath("<dfn>", "title",
            result.VideoTracks[0], null, page, 1, "WEB", 0));
        Assert.Equal("old.mp4", Assert.Single(initial.Clips));
        Assert.Single(initial.VideoTracks);
    }

    [Fact]
    public async Task AcceptedFallbackKeepsTheRequestedQualityForTheNextPageAttempt()
    {
        var initial = Result("16", "old.mp4");
        initial.Dfns = ["16", "80"];
        var selection = new ProgressiveStreamSelection();
        var requested = new List<string>();
        Task<ParsedResult> Fetch(string quality)
        {
            requested.Add(quality);
            return Task.FromResult(Result("64", $"attempt-{requested.Count}.mp4"));
        }
        using var input = new StringReader("1\n");
        using var output = new StringWriter();

        var selected = await selection.ChooseAsync(initial, Fetch, input, output);
        var retry = await Fetch(selection.RequestedQuality);

        Assert.Equal(new[] { "80", "80" }, requested);
        Assert.Equal("64", Assert.Single(selected.VideoTracks).id);
        Assert.Equal("attempt-2.mp4", Assert.Single(retry.Clips));
    }

    [Fact]
    public async Task FailedRefetchLeavesTheInitialResponseIntact()
    {
        var initial = Result("16", "old.mp4");
        var selection = new ProgressiveStreamSelection();
        using var input = new StringReader("0\n");
        using var output = new StringWriter();

        await Assert.ThrowsAsync<HttpRequestException>(() => selection.ChooseAsync(initial,
            _ => throw new HttpRequestException("network failure"), input, output));

        Assert.Equal("old.mp4", Assert.Single(initial.Clips));
        Assert.Single(initial.VideoTracks);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task MissingSegmentsOrTrackMetadataCannotUseTheOldSegments(bool keepSegments)
    {
        var initial = Result("16", "old.mp4");
        var selection = new ProgressiveStreamSelection();
        var accepted = Result("80", "new.mp4");
        if (keepSegments) accepted.VideoTracks.Clear();
        else accepted.Clips.Clear();
        using var input = new StringReader("0\n");
        using var output = new StringWriter();

        await Assert.ThrowsAsync<InvalidDataException>(() => selection.ChooseAsync(initial,
            _ => Task.FromResult(accepted), input, output));

        Assert.Equal("old.mp4", Assert.Single(initial.Clips));
    }

    private static ParsedResult Result(string quality, params string[] clips) => new()
    {
        Dfns = [quality],
        Clips = [.. clips],
        VideoTracks = [new Video { id = quality, dfn = quality, baseUrl = clips[0], codecs = "AVC" }]
    };
}
