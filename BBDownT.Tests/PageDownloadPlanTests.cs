using BBDownT.Core.Entity;
using static BBDownT.Core.Entity.Entity;

namespace BBDownT.Tests;

public class PageDownloadPlanTests
{
    [Fact]
    public void FiltersInSourceOrderAndKeepsOriginalPageInstances()
    {
        var info = Info(3);
        var plan = PageDownloadPlan.Create(info, new(), ["3", "1", "1", "99"], "single", "multi");

        Assert.Equal(new[] { 1, 3 }, plan.Pages.Select(page => page.index));
        Assert.Same(info.PagesInfo[0], plan.Pages[0]);
        Assert.Same(info.PagesInfo[2], plan.Pages[1]);
        Assert.Equal(3, info.PagesInfo.Count);
        Assert.Equal("multi", plan.SavePathFormat);
    }

    [Theory]
    [InlineData(1, false, false, "single")]
    [InlineData(1, true, false, "multi")]
    [InlineData(1, true, true, "single")]
    [InlineData(3, false, false, "multi")]
    public void NamingUsesOriginalCountAndBangumiCompletion(int count, bool bangumi, bool completed, string expected)
    {
        var info = Info(count);
        info.IsBangumi = bangumi;
        info.IsBangumiEnd = completed;
        var plan = PageDownloadPlan.Create(info, new(), ["1"], "single", "multi");

        Assert.Single(plan.Pages);
        Assert.Equal(expected, plan.SavePathFormat);
    }

    [Theory]
    [InlineData(1, "custom-single")]
    [InlineData(2, "custom-multi")]
    public void RespectsSeparateCustomNamingPatterns(int count, string expected)
    {
        var option = new MyOption { FilePattern = "custom-single", MultiFilePattern = "custom-multi" };
        var plan = PageDownloadPlan.Create(Info(count), option, ["1"], "single", "multi");
        Assert.Equal(expected, plan.SavePathFormat);
    }

    [Fact]
    public void NullSelectionKeepsAllPagesAndEmptySelectionKeepsNone()
    {
        var info = Info(2);
        Assert.Same(info.PagesInfo, PageDownloadPlan.Create(info, new(), null, "s", "m").Pages);
        Assert.Empty(PageDownloadPlan.Create(info, new(), [], "s", "m").Pages);
    }

    private static VInfo Info(int count) => new()
    {
        Title = "Video", Desc = "", Pic = "", PubTime = 0,
        PagesInfo = Enumerable.Range(1, count)
            .Select(index => new Page(index, "10", index.ToString(), "", "Page", 1, "", 0)).ToList()
    };
}
