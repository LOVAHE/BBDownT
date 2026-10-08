namespace BBDownT.Tests;

public class DownloadTaskStoreTests
{
    [Fact]
    public async Task SnapshotsAndTransitions_AreSafeUnderConcurrentAccess()
    {
        var store = new DownloadTaskStore(() => 100);
        var tasks = Enumerable.Range(0, 100)
            .Select(index => Start(
                store,
                new DownloadTask(index.ToString(), $"BV{index}", index)))
            .ToArray();

        var reader = Task.Run(() =>
        {
            for (var index = 0; index < 500; index++)
            {
                var snapshot = store.GetSnapshot();
                Assert.Equal(100, snapshot.Running.Count + snapshot.Finished.Count);
                Assert.All(snapshot.Running, task => Assert.Null(task.TaskFinishTime));
                Assert.All(snapshot.Finished, task => Assert.NotNull(task.TaskFinishTime));
            }
        });
        var completer = Task.Run(() =>
        {
            foreach (var task in tasks)
            {
                store.Complete(task, task.TaskCreateTime + 1, true);
            }
        });
        var remover = Task.Run(() =>
        {
            for (var index = 0; index < 500; index++)
            {
                store.RemoveFinished(task => int.Parse(task.Aid) < 0);
            }
        });

        await Task.WhenAll(reader, completer, remover);

        Assert.Empty(store.GetRunningSnapshot());
        Assert.Equal(100, store.GetFinishedSnapshot().Count);
    }

    [Fact]
    public void RemoveFinished_RemovesMatchingTasks()
    {
        var store = new DownloadTaskStore(() => 100);
        var first = Start(store, new DownloadTask("1", "first", 1));
        var second = Start(store, new DownloadTask("2", "second", 2));
        store.Complete(first, 3, true);
        store.Complete(second, 4, false);

        store.RemoveFinished(task => !task.IsSuccessful);

        var remaining = Assert.Single(store.GetFinishedSnapshot());
        Assert.Equal("1", remaining.Aid);
    }

    [Fact]
    public void PendingCapacity_ExcludesRunningTask()
    {
        var store = new DownloadTaskStore(() => 100);
        var first = new DownloadTask("", "first", 1);
        var second = new DownloadTask("", "second", 2);

        Assert.True(store.TryAddPending(first, 1));
        Assert.False(store.TryAddPending(second, 1));

        store.Start(first);

        Assert.True(store.TryAddPending(second, 1));
        Assert.Single(store.GetRunningSnapshot());
        Assert.Single(store.GetPendingSnapshot());
    }

    [Fact]
    public void Snapshot_PreservesStableTaskIdAcrossAidResolution()
    {
        var store = new DownloadTaskStore(() => 100);
        var task = new DownloadTask("", "BV1xx411c7mD", 1);
        store.TryAddPending(task, 1);
        var taskId = task.TaskId;

        task.SetAid("2");
        var snapshot = store.FindSnapshot(taskId);

        Assert.NotNull(snapshot);
        Assert.Equal(taskId, snapshot.TaskId);
        Assert.Equal("2", snapshot.Aid);
    }

    [Fact]
    public void Complete_EnforcesFinishedCountAndRetention()
    {
        var store = new DownloadTaskStore(() => 100);
        var oldTask = Start(store, new DownloadTask("old", "old", 1));
        store.Complete(oldTask, 10, true, maxFinishedTasks: 2, retentionSeconds: 1000);

        var middleTask = Start(store, new DownloadTask("middle", "middle", 2));
        store.Complete(middleTask, 20, true, maxFinishedTasks: 2, retentionSeconds: 1000);
        var latestTask = Start(store, new DownloadTask("latest", "latest", 3));
        store.Complete(latestTask, 30, true, maxFinishedTasks: 2, retentionSeconds: 1000);

        Assert.Equal(new[] { "middle", "latest" }, store.GetFinishedSnapshot().Select(task => task.Aid));

        var futureTask = Start(store, new DownloadTask("future", "future", 4));
        store.Complete(futureTask, 2000, true, maxFinishedTasks: 2, retentionSeconds: 10);

        var remaining = Assert.Single(store.GetFinishedSnapshot());
        Assert.Equal("future", remaining.Aid);
    }

    [Fact]
    public void FinishedSnapshot_PrunesExpiredTasksWithoutAnotherCompletion()
    {
        long now = 10;
        var store = new DownloadTaskStore(() => now);
        var task = Start(store, new DownloadTask("old", "old", 1));
        store.Complete(task, 10, true, retentionSeconds: 5);

        now = 16;

        Assert.Empty(store.GetFinishedSnapshot());
        Assert.Null(store.FindSnapshot(task.TaskId));
    }

    [Fact]
    public void TryRemovePending_RemovesOnlyTasksThatHaveNotStarted()
    {
        var store = new DownloadTaskStore(() => 100);
        var pending = new DownloadTask("", "BV1pending", 1);
        var other = new DownloadTask("", "BV1other", 2);
        Assert.True(store.TryAddPending(pending, 10));
        Assert.True(store.TryAddPending(other, 10));
        var running = Start(store, new DownloadTask("", "BV1running", 3));
        var finished = Start(store, new DownloadTask("", "BV1finished", 4));
        store.Complete(finished, 50, true);

        Assert.Equal(PendingRemoval.Removed, store.TryRemovePending(pending.TaskId, out var removed));
        Assert.Same(pending, removed);
        Assert.Equal([other.TaskId], store.GetPendingSnapshot().Select(t => t.TaskId));

        Assert.Equal(PendingRemoval.NotFound, store.TryRemovePending(pending.TaskId, out removed));
        Assert.Null(removed);
        Assert.Equal(PendingRemoval.AlreadyStarted, store.TryRemovePending(running.TaskId, out removed));
        Assert.Null(removed);
        Assert.Equal(PendingRemoval.AlreadyFinished, store.TryRemovePending(finished.TaskId, out _));
        Assert.Equal(PendingRemoval.NotFound, store.TryRemovePending("no-such-task", out _));
        Assert.Single(store.GetRunningSnapshot());
        Assert.Single(store.GetFinishedSnapshot());
    }

    [Fact]
    public void RemovedTask_CannotBeStartedAnymore_AndStartNextSkipsIt()
    {
        var store = new DownloadTaskStore(() => 100);
        var first = new DownloadTask("", "first", 1);
        var second = new DownloadTask("", "second", 2);
        var third = new DownloadTask("", "third", 3);
        foreach (var task in new[] { first, second, third }) Assert.True(store.TryAddPending(task, 10));

        Assert.Equal(PendingRemoval.Removed, store.TryRemovePending(first.TaskId, out _));
        Assert.False(store.Start(first));

        // 按加入顺序取下一个，被取消的不会被取出
        Assert.Same(second, store.StartNext());
        Assert.Same(third, store.StartNext());
        Assert.Null(store.StartNext());
        Assert.Equal(PendingRemoval.AlreadyStarted, store.TryRemovePending(second.TaskId, out _));
    }

    [Fact]
    public void RemoveAllPending_LeavesRunningTasksAlone()
    {
        var store = new DownloadTaskStore(() => 100);
        var running = Start(store, new DownloadTask("", "running", 1));
        Assert.True(store.TryAddPending(new DownloadTask("", "a", 2), 10));
        Assert.True(store.TryAddPending(new DownloadTask("", "b", 3), 10));

        Assert.Equal(2, store.RemoveAllPending().Count);

        Assert.Empty(store.GetPendingSnapshot());
        Assert.Equal([running.TaskId], store.GetRunningSnapshot().Select(t => t.TaskId));
        Assert.Null(store.StartNext());
        Assert.Empty(store.RemoveAllPending());
    }

    /// <summary>
    /// 执行队列取任务与取消同时进行：每个任务要么开始执行，要么被取消，不会两者都发生，也不会都没发生
    /// </summary>
    [Fact]
    public async Task StartNextAndTryRemovePending_RacingOnTheSameTasks_EachTaskEndsUpExactlyOnce()
    {
        for (var round = 0; round < 20; round++)
        {
            var store = new DownloadTaskStore(() => 100);
            var tasks = Enumerable.Range(0, 200).Select(i => new DownloadTask("", $"BV{i}", i)).ToList();
            foreach (var task in tasks) Assert.True(store.TryAddPending(task, int.MaxValue));

            using var go = new ManualResetEventSlim();
            var worker = Task.Run(() =>
            {
                go.Wait();
                var started = new List<DownloadTask>();
                while (store.StartNext() is { } task) started.Add(task);
                return started;
            });
            var canceller = Task.Run(() =>
            {
                go.Wait();
                var removed = new List<DownloadTask>();
                var conflicts = 0;
                foreach (var task in Enumerable.Reverse(tasks))
                {
                    switch (store.TryRemovePending(task.TaskId, out var r))
                    {
                        case PendingRemoval.Removed: removed.Add(r!); break;
                        case PendingRemoval.AlreadyStarted: conflicts++; break;
                        default: throw new InvalidOperationException("不应找不到任务");
                    }
                }
                return (removed, conflicts);
            });
            go.Set();
            var started = await worker;
            var (removedTasks, conflictCount) = await canceller;

            Assert.Equal(tasks.Count, started.Count + removedTasks.Count);
            Assert.Empty(started.Intersect(removedTasks));
            Assert.Equal(started.Count, conflictCount);
            Assert.Equal(started.Count, store.GetRunningSnapshot().Count);
            Assert.Empty(store.GetPendingSnapshot());
        }
    }

    private static DownloadTask Start(DownloadTaskStore store, DownloadTask task)
    {
        Assert.True(store.TryAddPending(task, int.MaxValue));
        store.Start(task);
        return task;
    }
}
