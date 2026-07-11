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
}
