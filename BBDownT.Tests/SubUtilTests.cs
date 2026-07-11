using BBDownT.Core.Util;
using static BBDownT.Core.Entity.Entity;

namespace BBDownT.Tests;

public class SubUtilTests
{
    [Fact]
    public void HaveValidUrls_AcceptsEmptyOrFullyPopulatedLists()
    {
        Assert.True(SubUtil.HaveValidUrls([]));
        Assert.True(SubUtil.HaveValidUrls(
        [
            CreateSubtitle("https://example.test/first.json"),
            CreateSubtitle("https://example.test/last.json")
        ]));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void HaveValidUrls_RejectsEmptyUrlAtAnyPosition(bool emptyUrlIsLast)
    {
        var empty = CreateSubtitle("");
        var valid = CreateSubtitle("https://example.test/subtitle.json");
        var subtitles = emptyUrlIsLast ? new[] { valid, empty } : new[] { empty, valid };

        Assert.False(SubUtil.HaveValidUrls(subtitles));
    }

    private static Subtitle CreateSubtitle(string url)
    {
        return new Subtitle { url = url, lan = "en", path = "subtitle.srt" };
    }
}
