using System;
using System.Collections.Generic;
using System.Linq;

namespace BBDownT;

internal sealed class DownloadTaskStore
{
    private readonly List<DownloadTask> pendingTasks = [];
    private readonly List<DownloadTask> runningTasks = [];
    private readonly List<DownloadTask> finishedTasks = [];
    private readonly object stateLock = new();
    private readonly Func<long> getCurrentTimeSeconds;
    private long finishedRetentionSeconds = 86400;

    internal DownloadTaskStore(Func<long>? getCurrentTimeSeconds = null)
    {
        this.getCurrentTimeSeconds = getCurrentTimeSeconds
            ?? (() => DateTimeOffset.Now.ToUnixTimeSeconds());
    }

    public bool TryAddPending(DownloadTask task, int maxPendingTasks)
    {
        lock (stateLock)
        {
            if (pendingTasks.Count >= maxPendingTasks)
            {
                return false;
            }

            pendingTasks.Add(task);
            return true;
        }
    }

    public void Start(DownloadTask task)
    {
        lock (stateLock)
        {
            if (pendingTasks.Remove(task))
            {
                runningTasks.Add(task);
            }
        }
    }

    public void Complete(
        DownloadTask task,
        long finishedAt,
        bool succeeded,
        int maxFinishedTasks = 1000,
        long retentionSeconds = 86400)
    {
        lock (stateLock)
        {
            finishedRetentionSeconds = retentionSeconds;
            task.Finish(finishedAt, succeeded);
            if (runningTasks.Remove(task))
            {
                finishedTasks.Add(task);
            }

            var oldestAllowed = finishedAt - retentionSeconds;
            finishedTasks.RemoveAll(finishedTask => finishedTask.TaskFinishTime < oldestAllowed);
            if (finishedTasks.Count > maxFinishedTasks)
            {
                finishedTasks.RemoveRange(0, finishedTasks.Count - maxFinishedTasks);
            }
        }
    }

    public DownloadTaskCollection GetSnapshot()
    {
        lock (stateLock)
        {
            PruneExpiredFinishedTasks();
            return new(
                pendingTasks.Select(task => task.CreateSnapshot()).ToList(),
                runningTasks.Select(task => task.CreateSnapshot()).ToList(),
                finishedTasks.Select(task => task.CreateSnapshot()).ToList());
        }
    }

    public List<DownloadTask> GetPendingSnapshot()
    {
        lock (stateLock)
        {
            return pendingTasks.Select(task => task.CreateSnapshot()).ToList();
        }
    }

    public List<DownloadTask> GetRunningSnapshot()
    {
        lock (stateLock)
        {
            return runningTasks.Select(task => task.CreateSnapshot()).ToList();
        }
    }

    public List<DownloadTask> GetFinishedSnapshot()
    {
        lock (stateLock)
        {
            PruneExpiredFinishedTasks();
            return finishedTasks.Select(task => task.CreateSnapshot()).ToList();
        }
    }

    public DownloadTask? FindSnapshot(string aid)
    {
        lock (stateLock)
        {
            PruneExpiredFinishedTasks();
            return (pendingTasks.FirstOrDefault(task => Matches(task, aid))
                ?? runningTasks.FirstOrDefault(task => Matches(task, aid))
                ?? finishedTasks.FirstOrDefault(task => Matches(task, aid)))?.CreateSnapshot();
        }
    }

    public void RemoveFinished(Predicate<DownloadTask> predicate)
    {
        lock (stateLock)
        {
            finishedTasks.RemoveAll(predicate);
        }
    }

    private static bool Matches(DownloadTask task, string id)
    {
        return task.MatchesId(id);
    }

    private void PruneExpiredFinishedTasks()
    {
        var oldestAllowed = getCurrentTimeSeconds() - finishedRetentionSeconds;
        finishedTasks.RemoveAll(finishedTask => finishedTask.TaskFinishTime < oldestAllowed);
    }
}
