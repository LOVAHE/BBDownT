using static BBDownT.Core.Entity.Entity;
using BBDownT.Core.Util;

namespace BBDownT.Tests;

public class PageDownloadRunnerTests
{
    [Fact]
    public async Task SameAidDifferentCidsAreDownloadedAndArchivedIndividually()
    {
        var pages = Pages(3);
        var archive = new HashSet<string>();
        var downloaded = new List<string>();
        var runner = new PageDownloadRunner(archive.Contains, key => archive.Add(key), (_, _) => Task.CompletedTask, _ => { });

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
        var runner = new PageDownloadRunner(archive.Contains, key => archive.Add(key), (_, _) => Task.CompletedTask, _ => { });

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
        var runner = new PageDownloadRunner(archive.Contains, key => archive.Add(key), (_, _) => Task.CompletedTask, _ => { });

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
        var runner = new PageDownloadRunner(archive.Contains, key => archive.Add(key), (_, _) => Task.CompletedTask, _ => { });

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
            (ms, _) => { events.Add("delay:" + ms); return Task.CompletedTask; },
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
            (_, _) => throw new Exception("One selected page must not wait"), _ => { });

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
            (_, _) => throw new Exception("Delay must be disabled"), _ => { });

        await runner.RunAsync(Pages(2), false, delay, page =>
        {
            downloaded.Add(page.cid);
            return Task.FromResult(DownloadPageOutcome.Completed);
        });

        Assert.Equal(new[] { "1", "2" }, downloaded);
    }

    [Fact]
    public async Task FailedOutcomeContinuesLaterPagesAndReportsBatchFailure()
    {
        var events = new List<string>();
        var archived = new List<string>();
        var runner = new PageDownloadRunner(_ => false,
            archived.Add, (_, _) => Task.CompletedTask, events.Add);

        var error = await Assert.ThrowsAsync<PageDownloadBatchException>(() => runner.RunAsync(Pages(2), true, 0, page =>
        {
            events.Add("download:" + page.cid);
            return Task.FromResult(page.index == 1 ? DownloadPageOutcome.Failed : DownloadPageOutcome.Completed);
        }));

        Assert.Equal(new[] { 1 }, error.FailedPages);
        Assert.Equal(new[] { "10:2" }, archived);
        Assert.Contains("download:2", events);
        Assert.Contains(events, message => message.StartsWith("P1 下载失败："));
        Assert.DoesNotContain("任务完成", events);
    }

    [Fact]
    public async Task PropagatesPageExceptionWithoutAddingRetries()
    {
        var failure = new IOException("page failure");
        var secondFailure = new InvalidDataException("bad page");
        var attempted = new List<int>();
        var archived = new List<string>();
        var runner = new PageDownloadRunner(_ => false, archived.Add, (_, _) => Task.CompletedTask, _ => { });

        var actual = await Assert.ThrowsAsync<PageDownloadBatchException>(() => runner.RunAsync(Pages(3), true, 0, page =>
        {
            attempted.Add(page.index);
            return page.index switch
            {
                1 => Task.FromException<DownloadPageOutcome>(failure),
                3 => Task.FromException<DownloadPageOutcome>(secondFailure),
                _ => Task.FromResult(DownloadPageOutcome.Completed)
            };
        }));

        Assert.Equal(new[] { 1, 2, 3 }, attempted);
        Assert.Equal(new[] { 1, 3 }, actual.FailedPages);
        Assert.Same(failure, actual.InnerExceptions[0].InnerException);
        Assert.Same(secondFailure, actual.InnerExceptions[1].InnerException);
        Assert.Equal(new[] { "10:2" }, archived);
        Assert.Contains("P1", actual.Message);
        Assert.Contains("P3", actual.Message);
    }

    [Fact]
    public async Task CancellationStopsBatchImmediately()
    {
        var cancellation = new OperationCanceledException("user cancelled");
        var attempts = 0;
        var runner = new PageDownloadRunner(_ => false, _ => { }, (_, _) => Task.CompletedTask, _ => { });

        var actual = await Assert.ThrowsAsync<OperationCanceledException>(() => runner.RunAsync(Pages(3), false, 0, _ =>
        {
            attempts++;
            return Task.FromException<DownloadPageOutcome>(cancellation);
        }));

        Assert.Same(cancellation, actual);
        Assert.Equal(1, attempts);
    }

    [Fact]
    public async Task WrappedCancellationAlsoStopsBatchImmediately()
    {
        var failure = new IOException("cancelled download", new OperationCanceledException());
        var attempts = 0;
        var runner = new PageDownloadRunner(_ => false, _ => { }, (_, _) => Task.CompletedTask, _ => { });

        var actual = await Assert.ThrowsAsync<IOException>(() => runner.RunAsync(Pages(3), false, 0, _ =>
        {
            attempts++;
            return Task.FromException<DownloadPageOutcome>(failure);
        }));

        Assert.Same(failure, actual);
        Assert.Equal(1, attempts);
    }

    [Fact]
    public async Task FatalRuntimeFailureAlsoStopsBatchWhenWrappedByDownloader()
    {
        var failure = new IOException("clip failed", new OutOfMemoryException());
        var attempts = 0;
        var runner = new PageDownloadRunner(_ => false, _ => { }, (_, _) => Task.CompletedTask, _ => { });

        var actual = await Assert.ThrowsAsync<IOException>(() => runner.RunAsync(Pages(3), false, 0, _ =>
        {
            attempts++;
            return Task.FromException<DownloadPageOutcome>(failure);
        }));

        Assert.Same(failure, actual);
        Assert.Equal(1, attempts);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExhaustedHttpTimeoutDoesNotPreventNextPage(bool wrappedByDownloader)
    {
        var timeout = new TaskCanceledException("HTTP timeout", new TimeoutException());
        Exception failure = wrappedByDownloader
            ? new IOException("clip failed", new DownloadInterruptedException("media request timed out", timeout)) : timeout;
        var attempted = new List<int>();
        var runner = new PageDownloadRunner(_ => false, _ => { }, (_, _) => Task.CompletedTask, _ => { });

        var error = await Assert.ThrowsAsync<PageDownloadBatchException>(() => runner.RunAsync(Pages(2), false, 0, page =>
        {
            attempted.Add(page.index);
            return page.index == 1 ? Task.FromException<DownloadPageOutcome>(failure)
                : Task.FromResult(DownloadPageOutcome.Completed);
        }));

        Assert.Equal(new[] { 1, 2 }, attempted);
        Assert.Same(failure, Assert.Single(error.InnerExceptions).InnerException);
    }

    [Fact]
    public async Task CancellationTokenStopsBeforeStartingAnotherPage()
    {
        using var source = new CancellationTokenSource();
        var attempted = new List<int>();
        var archived = new List<string>();
        var runner = new PageDownloadRunner(_ => false, archived.Add, (_, _) => Task.CompletedTask, _ => { });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runner.RunAsync(Pages(2), true, 0, page =>
        {
            attempted.Add(page.index);
            source.Cancel();
            return Task.FromResult(DownloadPageOutcome.Completed);
        }, cancellationToken: source.Token));

        Assert.Equal(new[] { 1 }, attempted);
        Assert.Equal(new[] { "10:1" }, archived);
    }

    [Fact]
    public async Task CancellationInterruptsTheWaitBeforeTheFirstPage()
    {
        using var source = new CancellationTokenSource();
        var attempts = 0;
        var delays = 0;
        var runner = new PageDownloadRunner(_ => false, _ => { }, (milliseconds, token) =>
        {
            Assert.Equal(source.Token, token);
            Assert.Equal(60_000, milliseconds);
            delays++;
            source.Cancel();
            return Task.Delay(milliseconds, token);
        }, _ => { });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runner.RunAsync(Pages(2), false, 60, _ =>
        {
            attempts++;
            return Task.FromResult(DownloadPageOutcome.Completed);
        }, cancellationToken: source.Token));

        Assert.Equal(1, delays);
        Assert.Equal(0, attempts);
    }

    [Fact]
    public async Task ArchivePersistenceFailureIsNotHiddenAsMediaFailure()
    {
        var failure = new IOException("archive is not writable");
        var attempts = 0;
        var runner = new PageDownloadRunner(_ => false, _ => throw failure, (_, _) => Task.CompletedTask, _ => { });

        var actual = await Assert.ThrowsAsync<IOException>(() => runner.RunAsync(Pages(2), true, 0, _ =>
        {
            attempts++;
            return Task.FromResult(DownloadPageOutcome.Completed);
        }));

        Assert.Same(failure, actual);
        Assert.Equal(1, attempts);
    }

    [Fact]
    public async Task AwaitsEachPageBeforeStartingTheNext()
    {
        var release = new TaskCompletionSource<DownloadPageOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new List<string>();
        var runner = new PageDownloadRunner(_ => false, _ => { }, (_, _) => Task.CompletedTask, _ => { });
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
            _ => throw new Exception("No archive writes"), (_, _) => throw new Exception("No delay"), logs.Add);

        await runner.RunAsync([], true, 10, _ => throw new Exception("No page downloads"));

        Assert.Equal(new[] { "任务完成" }, logs);
    }

    private static List<Page> Pages(int count) => Enumerable.Range(1, count)
        .Select(index => new Page(index, "10", index.ToString(), "", "Page", 1, "", 0)).ToList();
}
