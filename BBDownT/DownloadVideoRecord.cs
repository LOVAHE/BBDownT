using System;
using System.Collections.Generic;
using System.Linq;
using BBDownT.Core.Entity;

namespace BBDownT;

/// <summary>
/// 任务中一个视频的下载记录(普通任务就是任务本身；UP主空间批量下载时是清单里的每一条)，结束时写入下载历史。
/// 只由 <see cref="DownloadTask"/> 在其状态锁内修改。
/// </summary>
internal sealed class DownloadVideoRecord
{
    private readonly List<DownloadHistoryPage> pages = [];
    /// <summary>
    /// 输出文件已存在、这次没有重新下载的分P
    /// </summary>
    private readonly HashSet<int> existingPages = [];
    private readonly List<string> files = [];
    private readonly List<string> streamKeys = [];
    private readonly Dictionary<string, (string Description, IReadOnlyList<string> Tags)> streams = new(StringComparer.Ordinal);

    internal DownloadVideoRecord(string url, DownloadHistoryRequest request)
    {
        Url = url;
        Request = request;
    }

    internal string Url { get; }
    internal DownloadHistoryRequest Request { get; }
    internal string? Aid { get; private set; }
    internal string? Kind { get; private set; }
    internal string? Title { get; private set; }
    internal string? Owner { get; private set; }
    internal string? Pic { get; private set; }
    internal string? Api { get; private set; }
    internal bool Finished { get; private set; }

    internal void Describe(string aid, VInfo info, string? apiType)
    {
        Aid = aid;
        Title = info.Title;
        Pic = info.Pic;
        Owner = info.PagesInfo.FirstOrDefault(page => !string.IsNullOrWhiteSpace(page.ownerName))?.ownerName;
        Kind = info is SpaceVideoInfo ? "space"
            : info.IsCheese ? "cheese"
            : info.IsBangumi ? "bangumi"
            : info.PagesInfo.Select(page => page.aid).Distinct().Count() > 1 ? "list"
            : "video";
        Api = apiType;
    }

    internal void AddPage(int index, string title, bool alreadyExisted = false)
    {
        pages.RemoveAll(page => page.Index == index);
        pages.Add(new DownloadHistoryPage(index, title));
        if (alreadyExisted) existingPages.Add(index);
        else existingPages.Remove(index);
    }

    internal void AddFile(string fullPath) => files.Add(fullPath);

    internal void SetStream(string pageKey, string description, IReadOnlyList<string> tags)
    {
        if (!streams.ContainsKey(pageKey)) streamKeys.Add(pageKey);
        streams[pageKey] = (description, tags);
    }

    internal FinishedVideo Finish(bool succeeded, string? error)
    {
        Finished = true;
        // 每个分P都因文件已存在而跳过：这次什么也没下载，选中的流不是已有文件的画质，不记
        var skipped = pages.Count > 0 && pages.All(page => existingPages.Contains(page.Index));
        return new FinishedVideo(
            Url, Request, Aid, Kind, Title, Owner, Pic, Api,
            [.. pages],
            skipped ? [] : streamKeys.Select(key => streams[key].Description).ToList(),
            skipped ? [] : streamKeys.SelectMany(key => streams[key].Tags).Distinct(StringComparer.Ordinal).ToList(),
            [.. files],
            succeeded,
            succeeded ? null : error,
            skipped);
    }
}
