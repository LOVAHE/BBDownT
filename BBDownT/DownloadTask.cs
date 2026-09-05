using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace BBDownT;

public sealed class DownloadTask
{
    private readonly object stateLock = new();

    public DownloadTask(string aid, string url, long taskCreateTime)
        : this(Guid.NewGuid().ToString("N"), aid, url, taskCreateTime)
    {
    }

    internal DownloadTask(string taskId, string aid, string url, long taskCreateTime)
    {
        TaskId = taskId;
        Aid = aid;
        Url = url;
        TaskCreateTime = taskCreateTime;
    }

    public string TaskId { get; }
    public string Aid { get; private set; }
    public string Url { get; }
    public long TaskCreateTime { get; }

    internal static double CalculateDownloadSpeed(double totalDownloadedBytes, long startedAt, long finishedAt)
    {
        var elapsedSeconds = finishedAt - startedAt;
        return elapsedSeconds <= 0 ? 0 : totalDownloadedBytes / elapsedSeconds;
    }

    internal void SetMetadata(string? title, string? pic, long? videoPubTime)
    {
        lock (stateLock)
        {
            Title = title;
            Pic = pic;
            VideoPubTime = videoPubTime;
        }
    }

    internal void SetAid(string aid)
    {
        lock (stateLock)
        {
            Aid = aid;
        }
    }

    internal bool MatchesId(string id)
    {
        lock (stateLock)
        {
            return string.Equals(TaskId, id, StringComparison.Ordinal)
                || (!string.IsNullOrEmpty(Aid) && string.Equals(Aid, id, StringComparison.Ordinal));
        }
    }

    internal void ReportProgress(double progress)
    {
        lock (stateLock)
        {
            Progress = ProgressBar.NormalizeProgress(progress);
        }
    }

    internal void ReportDownloadedBytes(double bytesPerSecond)
    {
        lock (stateLock)
        {
            DownloadSpeed = bytesPerSecond;
            TotalDownloadedBytes += bytesPerSecond;
        }
    }

    internal void AddSavePath(string path)
    {
        lock (stateLock)
        {
            SavePaths.Add(path);
        }
    }

    internal void SetError(string error)
    {
        lock (stateLock)
        {
            Error = error;
        }
    }

    internal void Finish(long finishedAt, bool succeeded)
    {
        lock (stateLock)
        {
            TaskFinishTime = finishedAt;
            IsSuccessful = succeeded;
            if (succeeded)
            {
                Progress = 1f;
                DownloadSpeed = CalculateDownloadSpeed(TotalDownloadedBytes, TaskCreateTime, finishedAt);
            }
        }
    }

    internal DownloadTask CreateSnapshot()
    {
        lock (stateLock)
        {
            return new DownloadTask(TaskId, Aid, Url, TaskCreateTime)
            {
                Title = Title,
                Pic = Pic,
                VideoPubTime = VideoPubTime,
                TaskFinishTime = TaskFinishTime,
                Progress = Progress,
                DownloadSpeed = DownloadSpeed,
                TotalDownloadedBytes = TotalDownloadedBytes,
                IsSuccessful = IsSuccessful,
                Error = Error,
                SavePaths = [.. SavePaths]
            };
        }
    }

    [JsonInclude]
    public string? Title = null;
    [JsonInclude]
    public string? Pic = null;
    [JsonInclude]
    public long? VideoPubTime = null;
    [JsonInclude]
    public long? TaskFinishTime = null;
    [JsonInclude]
    public double Progress = 0f;
    [JsonInclude]
    public double DownloadSpeed = 0f;
    [JsonInclude]
    public double TotalDownloadedBytes = 0f;
    [JsonInclude]
    public bool IsSuccessful = false;
    [JsonInclude]
    public string? Error = null;

    [JsonInclude]
    public List<string> SavePaths = new();
};
public record DownloadTaskCollection(
    List<DownloadTask> Pending,
    List<DownloadTask> Running,
    List<DownloadTask> Finished);
