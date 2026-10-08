using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json.Serialization;
using BBDownT.Core;
using BBDownT.Core.Entity;

namespace BBDownT;

public sealed class DownloadTask
{
    private readonly object stateLock = new();
    private readonly Dictionary<string, int> streamIndexByPage = new(StringComparer.Ordinal);
    private DownloadVideoRecord? currentVideo;
    private int videoCount;

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

    /// <summary>
    /// 记录输出文件。相对路径按当前工作目录(任务执行时即任务的下载目录)转成绝对路径，
    /// 与空间投稿TXT清单等其他输出的写法一致
    /// </summary>
    internal void AddSavePath(string path)
    {
        var fullPath = Path.GetFullPath(path);
        lock (stateLock)
        {
            SavePaths.Add(fullPath);
            currentVideo?.AddFile(fullPath);
        }
    }

    /// <summary>
    /// 记录某个分P实际下载的音视频流，便于在任务列表中确认画质。
    /// 以分P为键：下载失败重试同一分P时覆盖旧记录，不会重复添加。
    /// </summary>
    internal void SetStream(string pageKey, string description) => SetStream(pageKey, description, []);

    /// <param name="tags">画质、编码、音质等标签，写入下载历史用，如 ["4K 超清", "HEVC", "192K"]</param>
    internal void SetStream(string pageKey, string description, IReadOnlyList<string> tags)
    {
        lock (stateLock)
        {
            currentVideo?.SetStream(pageKey, description, tags);
            if (streamIndexByPage.TryGetValue(pageKey, out var index))
            {
                Streams[index] = description;
                return;
            }
            streamIndexByPage[pageKey] = Streams.Count;
            Streams.Add(description);
        }
    }

    /// <summary>
    /// 每个视频下载结束(成功或失败)时调用，服务器用它写入下载历史；在任务的状态锁之外调用。
    /// 未设置时(命令行、只看信息的任务)不记录。
    /// </summary>
    internal Action<FinishedVideo>? VideoFinished { get; set; }

    /// <summary>
    /// 是否已开始过逐个视频的记录；没有时任务结束后按整个任务记一条历史
    /// </summary>
    internal bool HasVideoRecords
    {
        get
        {
            lock (stateLock)
            {
                return videoCount > 0;
            }
        }
    }

    /// <summary>
    /// 开始记录一个视频：之后记录的输出文件、音视频流和分P都归入这个视频，直到 <see cref="FinishVideo"/>
    /// </summary>
    internal DownloadVideoRecord BeginVideo(string url, DownloadHistoryRequest request)
    {
        var video = new DownloadVideoRecord(url, request);
        lock (stateLock)
        {
            currentVideo = video;
            videoCount++;
        }
        return video;
    }

    /// <summary>
    /// 正在下载的视频的请求(在解析改写选项之前记下的)；没有逐个视频的记录时为null。
    /// 写入临时工作文件夹的说明文件，供「继续下载」使用
    /// </summary>
    internal DownloadHistoryRequest? CurrentVideoRequest
    {
        get
        {
            lock (stateLock)
            {
                return currentVideo?.Request;
            }
        }
    }

    internal void DescribeVideo(DownloadVideoRecord video, string aid, VInfo info, string? apiType)
    {
        lock (stateLock)
        {
            video.Describe(aid, info, apiType);
        }
    }

    /// <summary>
    /// 记录实际下载完成(含已存在而跳过)的分P
    /// </summary>
    /// <param name="alreadyExisted">输出文件已存在，这次跳过了下载</param>
    internal void AddPage(int index, string title, bool alreadyExisted = false)
    {
        lock (stateLock)
        {
            currentVideo?.AddPage(index, title, alreadyExisted);
        }
    }

    internal void FinishVideo(DownloadVideoRecord video, bool succeeded, string? error)
    {
        FinishedVideo finished;
        lock (stateLock)
        {
            if (video.Finished) return;
            if (ReferenceEquals(currentVideo, video)) currentVideo = null;
            finished = video.Finish(succeeded, error);
        }
        try
        {
            VideoFinished?.Invoke(finished);
        }
        catch (Exception e)
        {
            Logger.LogWarn($"记录下载历史失败：{e.Message}");
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
                SavePaths = [.. SavePaths],
                Streams = [.. Streams]
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

    [JsonInclude]
    public List<string> Streams = new();
};
public record DownloadTaskCollection(
    List<DownloadTask> Pending,
    List<DownloadTask> Running,
    List<DownloadTask> Finished);
