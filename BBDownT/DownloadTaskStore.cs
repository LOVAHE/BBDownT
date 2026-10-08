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

    /// <summary>
    /// 把排队中的任务标为开始执行。任务已被取消(不在排队列表里)时返回false，调用方不能再执行它
    /// </summary>
    public bool Start(DownloadTask task)
    {
        lock (stateLock)
        {
            if (!pendingTasks.Remove(task))
            {
                return false;
            }

            runningTasks.Add(task);
            return true;
        }
    }

    /// <summary>
    /// 按加入顺序取出下一个排队中的任务并标为开始执行；没有排队中的任务(可能都已取消)时返回null。
    /// 与 <see cref="TryRemovePending"/> 在同一把锁下进行：同一个任务要么开始执行，要么被取消，不会两者都发生
    /// </summary>
    public DownloadTask? StartNext()
    {
        lock (stateLock)
        {
            if (pendingTasks.Count == 0)
            {
                return null;
            }

            var task = pendingTasks[0];
            pendingTasks.RemoveAt(0);
            runningTasks.Add(task);
            return task;
        }
    }

    /// <summary>
    /// 取消一个还没开始的任务。正在执行的任务不能取消，返回 <see cref="PendingRemoval.AlreadyStarted"/>；
    /// 已结束(还在已完成列表里)的返回 <see cref="PendingRemoval.AlreadyFinished"/>
    /// </summary>
    public PendingRemoval TryRemovePending(string id, out DownloadTask? removed)
    {
        lock (stateLock)
        {
            var index = pendingTasks.FindIndex(task => task.MatchesId(id));
            if (index >= 0)
            {
                removed = pendingTasks[index];
                pendingTasks.RemoveAt(index);
                return PendingRemoval.Removed;
            }

            removed = null;
            if (runningTasks.Any(task => task.MatchesId(id))) return PendingRemoval.AlreadyStarted;
            PruneExpiredFinishedTasks();
            return finishedTasks.Any(task => task.MatchesId(id))
                ? PendingRemoval.AlreadyFinished
                : PendingRemoval.NotFound;
        }
    }

    /// <summary>
    /// 取消全部还没开始的任务，返回被取消的任务
    /// </summary>
    public List<DownloadTask> RemoveAllPending()
    {
        lock (stateLock)
        {
            var removed = pendingTasks.ToList();
            pendingTasks.Clear();
            return removed;
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

internal enum PendingRemoval
{
    Removed,
    NotFound,
    AlreadyStarted,
    AlreadyFinished
}
