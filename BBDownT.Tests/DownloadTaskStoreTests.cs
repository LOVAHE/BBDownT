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

    private static DownloadTask Start(DownloadTaskStore store, DownloadTask task)
    {
        Assert.True(store.TryAddPending(task, int.MaxValue));
        store.Start(task);
        return task;
    }
}
