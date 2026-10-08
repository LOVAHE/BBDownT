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

        Assert.Contains("「分P」写法无效", exception.Message);
    }

    [Fact]
    public void InvalidSelection_MessageHasNoTrimmedResourceKeySuffix()
    {
        var exception = Assert.Throws<ArgumentException>(() => PageSelectionParser.Parse("1,2-3", 1));

        Assert.Null(exception.ParamName);
        Assert.DoesNotContain("Arg_ParamName_Name", exception.Message);
        Assert.DoesNotContain("selection", exception.Message);
    }

    [Theory]
    [InlineData("1", true)]
    [InlineData("1,3-5,LATEST,3", true)]
    [InlineData(" 2 - 4 ", true)]
    [InlineData("ALL", true)]
    [InlineData("new", true)]
    [InlineData("1-2147483647", true)]
    [InlineData("2147483647", true)]
    [InlineData("2147483648", false)]
    [InlineData("", false)]
    [InlineData(",", false)]
    [InlineData("1,", false)]
    [InlineData("abc", false)]
    [InlineData("0", false)]
    [InlineData("0-3", false)]
    [InlineData("5-3", false)]
    [InlineData("1--2", false)]
    [InlineData("ALL,1", false)]
    [InlineData("+1", false)]
    public void ValidateSyntax_MatchesParseRulesWithoutKnowingThePageCount(string selection, bool valid)
    {
        Assert.Equal(valid, PageSelectionParser.ValidateSyntax(selection) is null);
        if (!valid)
        {
            // 写法错误时，无论分P数多少 Parse 都会拒绝
            Assert.Throws<ArgumentException>(() => PageSelectionParser.Parse(selection, 3));
        }
    }

    [Fact]
    public void ValidateSyntax_DoesNotExpandHugeRanges()
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();

        Assert.Null(PageSelectionParser.ValidateSyntax("1-2147483647,1-2147483647,1-2147483647"));
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(1));
    }

    [Fact]
    public void DescribeInvalidSelection_GivesThePageCountAndExamples()
    {
        var message = PageSelectionParser.DescribeInvalidSelection("1,2-3", 1);

        Assert.Contains("共 1 个分P", message);
        Assert.Contains("LAST", message);
        Assert.Contains("ALL", message);
        Assert.Contains("1,3-12、2-12", PageSelectionParser.DescribeInvalidSelection("13", 12));
        // 示例本身都是该视频可用的写法
        foreach (var count in new[] { 1, 2, 3, 12 })
        {
            var text = PageSelectionParser.DescribeInvalidSelection("x", count);
            var examples = text[(text.IndexOf("可填写如 ", StringComparison.Ordinal) + 5)..text.IndexOf("、LAST", StringComparison.Ordinal)];
            Assert.All(examples.Split('、'), example => PageSelectionParser.Parse(example, count));
        }
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

    [Fact]
    public void Parse_HugeRangeFailsAtTheFirstPageBeyondTheCountWithoutExpandingIt()
    {
        // /add-task 只预检写法，"1-2147483647" 会入队；任务拿到分P数后在这里很快失败
        var watch = System.Diagnostics.Stopwatch.StartNew();

        var error = Assert.Throws<ArgumentException>(() => PageSelectionParser.Parse("1-2147483647", 3));

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(1));
        Assert.StartsWith("「分P」写法无效：1-2147483647", error.Message);
    }
}
