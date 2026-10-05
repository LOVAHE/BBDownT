using static BBDownT.Core.Entity.Entity;

namespace BBDownT.Tests;

public class PageDownloadRunnerTests
{
    [Fact]
    public async Task SameAidDifferentCidsAreDownloadedAndArchivedIndividually()
    {
        var pages = Pages(3);
        var archive = new HashSet<string>();
        var downloaded = new List<string>();
        var runner = new PageDownloadRunner(archive.Contains, key => archive.Add(key), _ => Task.CompletedTask, _ => { });

        await runner.RunAsync(pages, true, 0, page =>
        {
            downloaded.Add(page.cid);
            return Task.FromResult(DownloadPageOutcome.Completed);
        }, pages);
        await runner.RunAsync(pages, true, 0, _ => throw new Exception("Every CID should be archived"), pages);

        Assert.Equal(new[] { "1", "2", "3" }, downloaded);
        Assert.Equal(new[] { "10:1", "10:2", "10:3" }, archive.Order());
    }

    [Fact]
    public async Task LegacyAidDoesNotSkipMultiPageVideoEvenWhenOnlyOnePageIsSelected()
    {
        var allPages = Pages(3);
        var archive = new HashSet<string> { "10", "10:1" };
        var downloaded = new List<string>();
        var runner = new PageDownloadRunner(archive.Contains, key => archive.Add(key), _ => Task.CompletedTask, _ => { });

        await runner.RunAsync([allPages[1]], true, 0, page =>
        {
            downloaded.Add(page.cid);
            return Task.FromResult(DownloadPageOutcome.Completed);
        }, allPages);

        Assert.Equal(new[] { "2" }, downloaded);
        Assert.Contains("10:2", archive);
        Assert.DoesNotContain("10:3", archive);
        Assert.Contains("10", archive);
    }

    [Fact]
    public async Task UnknownOriginalPageCountCannotTreatLegacyAidAsComplete()
    {
        var archive = new HashSet<string> { "10" };
        var downloaded = false;
        var runner = new PageDownloadRunner(archive.Contains, key => archive.Add(key), _ => Task.CompletedTask, _ => { });

        await runner.RunAsync(Pages(1), true, 0, _ =>
        {
            downloaded = true;
            return Task.FromResult(DownloadPageOutcome.Completed);
        });

        Assert.True(downloaded);
        Assert.Contains("10:1", archive);
    }

    [Fact]
    public async Task LegacyAidStillSkipsConfirmedSinglePageEntriesInASeason()
    {
        var pages = Pages(2);
        pages[1].aid = "20";
        var archive = new HashSet<string> { "10" };
        var downloaded = new List<string>();
        var runner = new PageDownloadRunner(archive.Contains, key => archive.Add(key), _ => Task.CompletedTask, _ => { });

        await runner.RunAsync(pages, true, 0, page =>
        {
            downloaded.Add(page.aid);
            return Task.FromResult(DownloadPageOutcome.Completed);
        }, pages);

        Assert.Equal(new[] { "20" }, downloaded);
        Assert.Contains("20:2", archive);
    }

    [Fact]
    public async Task WaitsBeforeArchiveCheckAndPreservesLogOrder()
    {
        var events = new List<string>();
        var runner = new PageDownloadRunner(
            key => { events.Add("check:" + key); return key == "10:1"; },
            key => events.Add("archive:" + key),
            ms => { events.Add("delay:" + ms); return Task.CompletedTask; },
            message => events.Add("log:" + message));

        await runner.RunAsync(Pages(2), true, 2, page =>
        {
            events.Add("download:" + page.cid);
            return Task.FromResult(DownloadPageOutcome.Completed);
        });

        Assert.Equal(new[]
        {
            "log:停顿2秒...", "delay:2000", "log:开始解析P1: 10... (1 of 2)",
            "check:10:1", "log:P1已下载过, 跳过下载...",
            "log:停顿2秒...", "delay:2000", "log:开始解析P2: 10... (2 of 2)",
            "check:10:2", "download:2", "archive:10:2", "log:任务完成"
        }, events);
    }

    [Theory]
    [InlineData(nameof(DownloadPageOutcome.Completed), true)]
    [InlineData(nameof(DownloadPageOutcome.InfoOnly), false)]
    public async Task ConnectsMediaOutcomesToArchiveWrites(string outcome, bool shouldArchive)
    {
        var archived = new List<string>();
        var runner = new PageDownloadRunner(_ => false, archived.Add,
            _ => throw new Exception("One selected page must not wait"), _ => { });

        await runner.RunAsync(Pages(1), true, 10, _ => Task.FromResult(Enum.Parse<DownloadPageOutcome>(outcome)));

        Assert.Equal(shouldArchive ? new[] { "10:1" } : [], archived);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task DisabledArchivesAndNonpositiveDelayHaveNoSideEffects(int delay)
    {
        var downloaded = new List<string>();
        var runner = new PageDownloadRunner(
            _ => throw new Exception("Archive read must be disabled"),
            _ => throw new Exception("Archive write must be disabled"),
            _ => throw new Exception("Delay must be disabled"), _ => { });

        await runner.RunAsync(Pages(2), false, delay, page =>
        {
            downloaded.Add(page.cid);
            return Task.FromResult(DownloadPageOutcome.Completed);
        });

        Assert.Equal(new[] { "1", "2" }, downloaded);
    }

    [Fact]
    public async Task FailedOutcomeStillRunsLaterPagesAndReportsFailuresAtEnd()
    {
        var events = new List<string>();
        var runner = new PageDownloadRunner(_ => false, _ => { },
            _ => Task.CompletedTask, events.Add);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => runner.RunAsync(Pages(2), true, 0, page =>
        {
            events.Add("download:" + page.cid);
            return Task.FromResult(page.index == 1 ? DownloadPageOutcome.Failed : DownloadPageOutcome.Completed);
        }));

        // 一个分P失败不该牵连后面的分P: 失败只记录, 最后统一报失败以保留非 0 退出码
        Assert.Equal("共1个分P下载失败: P1", error.Message);
        Assert.Equal(new[]
        {
            "开始解析P1: 10... (1 of 2)", "download:1", "P1 下载失败",
            "开始解析P2: 10... (2 of 2)", "download:2"
        }, events);
        Assert.DoesNotContain("任务完成", events);
    }

    [Fact]
    public async Task PageExceptionIsRecordedAndRemainingPagesStillRun()
    {
        var downloaded = new List<string>();
        var runner = new PageDownloadRunner(_ => false, _ => { }, _ => Task.CompletedTask, _ => { });

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => runner.RunAsync(Pages(2), true, 0, page =>
        {
            if (page.index == 1) return Task.FromException<DownloadPageOutcome>(new IOException("页面下载异常"));
            downloaded.Add(page.cid);
            return Task.FromResult(DownloadPageOutcome.Completed);
        }));

        Assert.Equal("共1个分P下载失败: P1", error.Message);
        Assert.Equal(new[] { "2" }, downloaded);
    }

    [Fact]
    public async Task FailedPageIsNotArchivedSoRerunWillTryItAgain()
    {
        var archive = new HashSet<string>();
        var runner = new PageDownloadRunner(archive.Contains, key => archive.Add(key),
            _ => Task.CompletedTask, _ => { });

        await Assert.ThrowsAsync<InvalidOperationException>(() => runner.RunAsync(Pages(2), true, 0, page =>
            Task.FromResult(page.index == 1 ? DownloadPageOutcome.Failed : DownloadPageOutcome.Completed)));

        Assert.Equal(new[] { "10:2" }, archive);
    }

    [Fact]
    public async Task AwaitsEachPageBeforeStartingTheNext()
    {
        var release = new TaskCompletionSource<DownloadPageOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new List<string>();
        var runner = new PageDownloadRunner(_ => false, _ => { }, _ => Task.CompletedTask, _ => { });
        var running = runner.RunAsync(Pages(2), false, 0, page =>
        {
            started.Add(page.cid);
            return page.index == 1 ? release.Task : Task.FromResult(DownloadPageOutcome.Completed);
        });
        try
        {
            Assert.Equal(new[] { "1" }, started);
            Assert.False(running.IsCompleted);
        }
        finally { release.TrySetResult(DownloadPageOutcome.Completed); await running; }
        Assert.Equal(new[] { "1", "2" }, started);
    }

    [Fact]
    public async Task EmptySelectionOnlyLogsCompletion()
    {
        var logs = new List<string>();
        var runner = new PageDownloadRunner(_ => throw new Exception("No archive reads"),
            _ => throw new Exception("No archive writes"), _ => throw new Exception("No delay"), logs.Add);

        await runner.RunAsync([], true, 10, _ => throw new Exception("No page downloads"));

        Assert.Equal(new[] { "任务完成" }, logs);
    }

    private static List<Page> Pages(int count) => Enumerable.Range(1, count)
        .Select(index => new Page(index, "10", index.ToString(), "", "Page", 1, "", 0)).ToList();
}
