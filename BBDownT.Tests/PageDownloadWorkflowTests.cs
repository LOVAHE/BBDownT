using BBDownT.Core.Entity;
using static BBDownT.Core.Entity.Entity;

namespace BBDownT.Tests;

public class PageDownloadWorkflowTests
{
    [Fact]
    public void Plan_FiltersInSourceOrderAndKeepsOriginalPageInstances()
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
    public void Plan_NamingUsesOriginalCountAndBangumiCompletion(
        int count, bool bangumi, bool completed, string expected)
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
    public void Plan_RespectsSeparateCustomNamingPatterns(int count, string expected)
    {
        var option = new MyOption { FilePattern = "custom-single", MultiFilePattern = "custom-multi" };
        var plan = PageDownloadPlan.Create(Info(count), option, ["1"], "single", "multi");

        Assert.Equal(expected, plan.SavePathFormat);
    }

    [Fact]
    public void Plan_NullSelectionKeepsAllPages_EmptySelectionKeepsNone()
    {
        var info = Info(2);
        Assert.Same(info.PagesInfo, PageDownloadPlan.Create(info, new(), null, "s", "m").Pages);
        Assert.Empty(PageDownloadPlan.Create(info, new(), [], "s", "m").Pages);
    }

    [Fact]
    public async Task Runner_WaitsBeforeArchiveCheckAndPreservesLogOrder()
    {
        var events = new List<string>();
        var runner = new PageDownloadRunner(
            aid => { events.Add("check:" + aid); return aid == "1"; },
            aid => events.Add("archive:" + aid),
            ms => { events.Add("delay:" + ms); return Task.CompletedTask; },
            message => events.Add("log:" + message));

        await runner.RunAsync(Info(2).PagesInfo, true, 2, page =>
        {
            events.Add("download:" + page.aid);
            return Task.FromResult(DownloadPageOutcome.Completed);
        });

        Assert.Equal(new[]
        {
            "log:停顿2秒...", "delay:2000", "log:开始解析P1: 1... (1 of 2)",
            "check:1", "log:aid: 1已下载过, 跳过下载...",
            "log:停顿2秒...", "delay:2000", "log:开始解析P2: 2... (2 of 2)",
            "check:2", "download:2", "archive:2", "log:任务完成"
        }, events);
    }

    [Theory]
    [InlineData(nameof(DownloadPageOutcome.Completed), true)]
    [InlineData(nameof(DownloadPageOutcome.AlreadyExists), true)]
    [InlineData(nameof(DownloadPageOutcome.Partial), true)]
    [InlineData(nameof(DownloadPageOutcome.InfoOnly), false)]
    [InlineData(nameof(DownloadPageOutcome.ExclusiveArtifact), false)]
    public async Task Runner_ArchivesOnlyMediaOutcomes(string outcomeName, bool shouldArchive)
    {
        var archived = new List<string>();
        var runner = new PageDownloadRunner(_ => false, archived.Add,
            _ => throw new Exception("A single selected page must not wait"), _ => { });

        await runner.RunAsync(Info(1).PagesInfo, true, 10,
            _ => Task.FromResult(Enum.Parse<DownloadPageOutcome>(outcomeName)));

        Assert.Equal(shouldArchive ? new[] { "1" } : [], archived);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task Runner_DisabledArchivesAndNonpositiveDelayHaveNoSideEffects(int delay)
    {
        var downloaded = new List<string>();
        var runner = new PageDownloadRunner(
            _ => throw new Exception("Archive check must be disabled"),
            _ => throw new Exception("Archive write must be disabled"),
            _ => throw new Exception("Delay must be disabled"), _ => { });

        await runner.RunAsync(Info(2).PagesInfo, false, delay, page =>
        {
            downloaded.Add(page.aid);
            return Task.FromResult(DownloadPageOutcome.Completed);
        });

        Assert.Equal(new[] { "1", "2" }, downloaded);
    }

    [Fact]
    public async Task Runner_FailedOutcomeStopsLaterPagesAndDoesNotArchiveOrLogCompletion()
    {
        var events = new List<string>();
        var runner = new PageDownloadRunner(_ => false,
            _ => throw new Exception("Failed page must not be archived"),
            _ => Task.CompletedTask, events.Add);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            runner.RunAsync(Info(2).PagesInfo, true, 0, page =>
            {
                events.Add("download:" + page.aid);
                return Task.FromResult(DownloadPageOutcome.Failed);
            }));

        Assert.Equal("P1 下载失败", error.Message);
        Assert.Equal(new[] { "开始解析P1: 1... (1 of 2)", "download:1" }, events);
    }

    [Fact]
    public async Task Runner_PropagatesPageExceptionWithoutAddingRetries()
    {
        var failure = new IOException("page failed after its own retries");
        var attempts = 0;
        var runner = new PageDownloadRunner(_ => false,
            _ => throw new Exception("Failed page must not be archived"),
            _ => Task.CompletedTask, _ => { });

        var actual = await Assert.ThrowsAsync<IOException>(() =>
            runner.RunAsync(Info(2).PagesInfo, true, 0, _ =>
            {
                attempts++;
                return Task.FromException<DownloadPageOutcome>(failure);
            }));

        Assert.Same(failure, actual);
        Assert.Equal(1, attempts);
    }

    [Fact]
    public async Task Runner_AwaitsEachPageBeforeStartingTheNext()
    {
        var releaseFirst = new TaskCompletionSource<DownloadPageOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new List<string>();
        var runner = new PageDownloadRunner(_ => false, _ => { }, _ => Task.CompletedTask, _ => { });

        var running = runner.RunAsync(Info(2).PagesInfo, false, 0, page =>
        {
            started.Add(page.aid);
            return page.index == 1 ? releaseFirst.Task : Task.FromResult(DownloadPageOutcome.Completed);
        });
        try
        {
            Assert.Equal(new[] { "1" }, started);
            Assert.False(running.IsCompleted);
        }
        finally
        {
            releaseFirst.TrySetResult(DownloadPageOutcome.Completed);
            await running;
        }

        Assert.Equal(new[] { "1", "2" }, started);
    }

    [Fact]
    public async Task Runner_EmptySelectionOnlyLogsCompletion()
    {
        var messages = new List<string>();
        var runner = new PageDownloadRunner(
            _ => throw new Exception("No archive reads expected"),
            _ => throw new Exception("No archive writes expected"),
            _ => throw new Exception("No delay expected"), messages.Add);

        await runner.RunAsync([], true, 10, _ => throw new Exception("No page downloads expected"));

        Assert.Equal(new[] { "任务完成" }, messages);
    }

    private static VInfo Info(int count) => new()
    {
        Title = "Video", Desc = "", Pic = "", PubTime = 0,
        PagesInfo = Enumerable.Range(1, count)
            .Select(index => new Page(index, index.ToString(), index.ToString(), "", "Page", 1, "", 0))
            .ToList()
    };
}
