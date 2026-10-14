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
    private readonly int maxFinishedTasks;
    private readonly long finishedRetentionSeconds;

    internal DownloadTaskStore(Func<long>? getCurrentTimeSeconds = null, BBDownTServerOptions? options = null)
    {
        this.getCurrentTimeSeconds = getCurrentTimeSeconds
            ?? (() => DateTimeOffset.Now.ToUnixTimeSeconds());
        options ??= new BBDownTServerOptions();
        maxFinishedTasks = options.MaxFinishedTasks;
        finishedRetentionSeconds = options.FinishedTaskRetentionSeconds;
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
                task.MarkStarted(getCurrentTimeSeconds());
                runningTasks.Add(task);
            }
        }
    }

    public void Complete(DownloadTask task, long finishedAt, bool succeeded)
    {
        lock (stateLock)
        {
            task.Finish(finishedAt, succeeded);
            if (runningTasks.Remove(task))
            {
                finishedTasks.Add(task);
            }

            var oldestAllowed = finishedAt - finishedRetentionSeconds;
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

    public DownloadTask? FindSnapshot(string id)
    {
        lock (stateLock)
        {
            PruneExpiredFinishedTasks();
            return (pendingTasks.FirstOrDefault(task => task.MatchesId(id))
                ?? runningTasks.FirstOrDefault(task => task.MatchesId(id))
                ?? finishedTasks.FirstOrDefault(task => task.MatchesId(id)))?.CreateSnapshot();
        }
    }

    public void RemoveFinished(Predicate<DownloadTask> predicate)
    {
        lock (stateLock)
        {
            finishedTasks.RemoveAll(predicate);
        }
    }

    private void PruneExpiredFinishedTasks()
    {
        var oldestAllowed = getCurrentTimeSeconds() - finishedRetentionSeconds;
        finishedTasks.RemoveAll(finishedTask => finishedTask.TaskFinishTime < oldestAllowed);
    }
}
