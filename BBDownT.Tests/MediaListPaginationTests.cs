using BBDownT.Core.Fetcher;

namespace BBDownT.Tests;

public class MediaListPaginationTests
{
    [Theory]
    [InlineData("", "", true)]
    [InlineData("123", "123", true)]
    public void EnsureAdvanced_RejectsStalledCursorWhenMorePagesAreClaimed(
        string previous,
        string current,
        bool hasMore)
    {
        Assert.Throws<InvalidDataException>(
            () => MediaListPagination.EnsureAdvanced(previous, current, hasMore));
    }

    [Theory]
    [InlineData("", "123", true)]
    [InlineData("123", "123", false)]
    public void EnsureAdvanced_AcceptsProgressOrFinalPage(
        string previous,
        string current,
        bool hasMore)
    {
        MediaListPagination.EnsureAdvanced(previous, current, hasMore);
    }
}
