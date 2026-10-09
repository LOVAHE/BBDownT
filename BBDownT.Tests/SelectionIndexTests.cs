using BBDownT.Core.Entity;
using static BBDownT.Core.Entity.Entity;

namespace BBDownT.Tests;

public class SelectionIndexTests
{
    [Theory]
    [InlineData("0", 3, 0)]
    [InlineData("2", 3, 2)]
    [InlineData("3", 3, 0)]
    [InlineData("-1", 3, 0)]
    [InlineData("not-a-number", 3, 0)]
    [InlineData(null, 3, 0)]
    public void ParseSelectionIndex_ClampsInvalidInputToFirstItem(
        string? input,
        int itemCount,
        int expected)
    {
        Assert.Equal(expected, Program.ParseSelectionIndex(input, itemCount));
    }

    [Fact]
    public void TryRestoreTrackChoice_FindsThePickedTracksInAReparsedList()
    {
        var reparsed = new ParsedResult
        {
            VideoTracks = [CreateVideo("80", "HEVC"), CreateVideo("64", "AVC"), CreateVideo("64", "HEVC")],
            AudioTracks = [CreateAudio("30280"), CreateAudio("30216")]
        };
        int vIndex = 0, aIndex = 0;

        Assert.True(Program.TryRestoreTrackChoice(reparsed, CreateVideo("64", "HEVC"), CreateAudio("30216"), ref vIndex, ref aIndex));
        Assert.Equal(2, vIndex);
        Assert.Equal(1, aIndex);
    }

    [Fact]
    public void TryRestoreTrackChoice_AsksAgainWhenAPickedTrackIsGone()
    {
        var reparsed = new ParsedResult { VideoTracks = [CreateVideo("80", "HEVC")], AudioTracks = [CreateAudio("30280")] };
        int vIndex = 0, aIndex = 0;

        Assert.False(Program.TryRestoreTrackChoice(reparsed, CreateVideo("64", "AVC"), CreateAudio("30280"), ref vIndex, ref aIndex));
        Assert.Equal(0, vIndex);
    }

    private static Video CreateVideo(string id, string codecs) => new() { id = id, dfn = id, baseUrl = "https://example.test/" + id, codecs = codecs };

    private static Audio CreateAudio(string id) => new() { id = id, dfn = id, baseUrl = "https://example.test/" + id, codecs = "M4A", bandwith = 0, dur = 0 };
}
