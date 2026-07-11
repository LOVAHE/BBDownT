using System.CommandLine;

namespace BBDownT.Tests;

public class PageSelectionParserTests
{
    [Fact]
    public async Task CommandLineBinder_PreservesExplicitEmptySelection()
    {
        MyOption? captured = null;
        var command = CommandLineInvoker.GetRootCommand(option =>
        {
            captured = option;
            return Task.CompletedTask;
        });

        Assert.Equal(0, await command.InvokeAsync(["BV1xx411c7mD", "-p", ""]));
        Assert.NotNull(captured);
        Assert.True(captured.SelectPageSpecified);
        Assert.Throws<ArgumentException>(
            () => PageSelectionParser.Parse(captured.SelectPage, 10));
    }

    [Fact]
    public void All_ReturnsNullSelection()
    {
        Assert.Null(PageSelectionParser.Parse("ALL", 10));
    }

    [Fact]
    public void ListRangesAndLastAliases_AreExpandedAndDeduplicated()
    {
        var selected = PageSelectionParser.Parse("1,3-5,LATEST,3", 10);

        Assert.Equal(new[] { "1", "3", "4", "5", "10" }, selected);
    }

    [Theory]
    [InlineData("LAST")]
    [InlineData("LATEST")]
    [InlineData("NEW")]
    [InlineData("new")]
    public void LastAliases_SelectFinalPage(string selection)
    {
        Assert.Equal(new[] { "10" }, PageSelectionParser.Parse(selection, 10));
    }

    [Theory]
    [InlineData("")]
    [InlineData(",")]
    [InlineData(",1")]
    [InlineData("1,")]
    [InlineData("abc")]
    [InlineData("0")]
    [InlineData("11")]
    [InlineData("5-3")]
    [InlineData("1--2")]
    [InlineData("1,,2")]
    [InlineData("ALL,1")]
    [InlineData("+1")]
    [InlineData("1-+2")]
    public void InvalidExplicitSelection_ThrowsInsteadOfFallingBackToAll(string selection)
    {
        var exception = Assert.Throws<ArgumentException>(
            () => PageSelectionParser.Parse(selection, 10));

        Assert.Contains("分P参数无效", exception.Message);
    }

    [Fact]
    public void ProgressiveStream_RejectsCombinedAudioAndVideoOnlyMode()
    {
        Assert.False(Program.CanUseProgressiveStream(
            new MyOption { AudioOnly = true, VideoOnly = true }));
        Assert.True(Program.CanUseProgressiveStream(
            new MyOption { AudioOnly = true }));
        Assert.True(Program.CanUseProgressiveStream(
            new MyOption { VideoOnly = true }));
        Assert.True(Program.CanUseProgressiveStream(new MyOption()));
    }
}
