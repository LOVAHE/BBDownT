using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using BBDownT.Core.Util;

namespace BBDownT;

/// <summary>
/// GET /files/groups 的返回：下载根目录下的文件按视频分组。Total 为组数，TotalFiles 为文件数(含分片)；
/// 文件太多只统计了最新的一部分时 Truncated 为 true
/// </summary>
public sealed record FileGroupList(int Total, int TotalFiles, bool Truncated, List<FileGroup> Groups);

/// <summary>
/// 一个视频的文件：已完成的下载(视频及其字幕、弹幕、封面等附属文件，多P视频的整个文件夹)，
/// 或一次未完成的下载(临时工作文件夹里的分片)
/// </summary>
public sealed record FileGroup
{
    /// <summary>
    /// 组的标识，用于 DELETE /files/groups?id=…：h:&lt;视频&gt;(下载历史)、d:&lt;文件夹&gt;(多P文件夹)、
    /// s:&lt;文件夹/文件名&gt;(同名文件)、w:&lt;文件夹&gt;(未完成的下载)
    /// </summary>
    public string Id { get; init; } = "";
    /// <summary>
    /// complete：已完成；incomplete：未完成的下载
    /// </summary>
    public string Status { get; init; } = FileGroupStatus.Complete;
    /// <summary>
    /// 分组依据：history(下载历史)、folder(多P文件夹)、file(同名文件)、work(临时工作文件夹)
    /// </summary>
    public string Source { get; init; } = "";
    public string Title { get; init; } = "";
    /// <summary>
    /// 未完成的下载没有记下标题、正在按 av 号查询；稍后再请求一次即可拿到标题
    /// </summary>
    public bool TitlePending { get; init; }
    /// <summary>
    /// 未完成的下载：按 av 号查询时B站返回视频不存在或不可见，不能继续下载(Url、Bvid 为null)
    /// </summary>
    public bool Unavailable { get; init; }
    /// <summary>
    /// 封面图片链接(B站图床)
    /// </summary>
    public string? Pic { get; init; }
    /// <summary>
    /// 组里的本地图片文件(如下载时保存的封面)，没有 Pic 时可用作封面
    /// </summary>
    public string? CoverFile { get; init; }
    public string? Owner { get; init; }
    public string? Aid { get; init; }
    public string? Bvid { get; init; }
    /// <summary>
    /// 在B站打开的页面
    /// </summary>
    public string? PageUrl { get; init; }
    /// <summary>
    /// 重新解析用的链接或编号
    /// </summary>
    public string? Url { get; init; }
    /// <summary>
    /// 下载完成时间(下载历史记录的)，Unix时间戳(秒)
    /// </summary>
    public long? FinishedAt { get; init; }
    /// <summary>
    /// 组内文件最新的修改时间，Unix时间戳(秒)
    /// </summary>
    public long ModifiedTime { get; init; }
    /// <summary>
    /// 删除整组时会删除的文件的大小之和
    /// </summary>
    public long TotalBytes { get; init; }
    /// <summary>
    /// 删除整组时会删除的文件数。已完成的组就是 Files 的个数；未完成的下载还包括分片、.resume 续传状态、
    /// 说明文件和合并中断留下的隐藏暂存文件
    /// </summary>
    public int FileCount { get; init; }
    /// <summary>
    /// 主视频(没有视频时为音频)的相对路径，用于播放和打开位置；未完成的下载为null
    /// </summary>
    public string? MainFile { get; init; }
    /// <summary>
    /// 多P文件夹或临时工作文件夹的相对路径
    /// </summary>
    public string? Folder { get; init; }
    /// <summary>
    /// 组内的文件(未完成的下载不含分片和 .resume 续传状态)；未完成的下载还列出合并中断留下的隐藏暂存文件 .&lt;轨道&gt;.&lt;guid&gt;.partial.mp4
    /// </summary>
    public List<DownloadedFile> Files { get; init; } = [];
    public int ClipCount { get; init; }
    public int CompleteClipCount { get; init; }
    /// <summary>
    /// 未完成的下载的分片
    /// </summary>
    public List<FileClip> Clips { get; init; } = [];
    /// <summary>
    /// 正在下载：不能删除，也不需要继续下载
    /// </summary>
    public bool Active { get; init; }
    /// <summary>
    /// 未完成的下载：继续下载用的请求(下载开始时记下的)，可直接 POST /add-task；没有时为null。
    /// 已完成的下载：下载历史里的重新下载请求
    /// </summary>
    public DownloadHistoryRequest? Request { get; init; }

    /// <summary>
    /// 未完成的下载既没有说明文件里的标题、也还没查到标题：列出时按av号查询；不输出
    /// </summary>
    [JsonIgnore]
    internal bool NeedsTitleLookup { get; init; }

    /// <summary>
    /// 组内全部文件(含分片、.resume 续传状态和暂存文件，不含说明文件)的相对路径，删除整组时使用；不输出
    /// </summary>
    [JsonIgnore]
    internal List<string> MemberPaths { get; init; } = [];
}

/// <param name="Track">video 或 audio</param>
/// <param name="Page">能从文件名认出的分P序号</param>
/// <param name="Complete">这一段已下载完整</param>
public sealed record FileClip(string Path, string Track, int Index, int? Page, long Size, bool Complete);

/// <summary>
/// DELETE /files/groups 的返回：删除的文件数和删除失败的文件数
/// </summary>
public sealed record FileGroupDeleteResult(int Deleted, int Failed);

internal static class FileGroupStatus
{
    internal const string Complete = "complete";
    internal const string Incomplete = "incomplete";
}

/// <summary>
/// 列出的一个文件：相对下载根目录的路径(/ 分隔)、大小、修改时间(Unix秒)
/// </summary>
internal sealed record ListedFile(string Path, long Size, long ModifiedTime);

/// <summary>
/// 按av号查到的视频信息(未完成的下载没有说明文件时用来显示标题)
/// </summary>
/// <param name="Gone">B站返回视频不存在或不可见(此时没有标题)</param>
internal sealed record VideoTitleInfo(string Title, string? Owner, string? Pic, string? Bvid, bool Gone = false)
{
    internal static readonly VideoTitleInfo Missing = new("", null, null, null, Gone: true);
}

/// <summary>
/// 把下载根目录下的文件按视频分组。规则(先满足的优先)：
/// 1. 临时工作文件夹(有说明文件 .bbdownt-task.json，或者文件夹名是 av 号、里面有以这个 av 号命名的分片、临时文件、续传状态或暂存文件)
///    里不属于下载历史的文件是一次未完成的下载，整个文件夹一组；
/// 2. 下载历史记录过的文件按视频(BV号/av号/ep号)分组，同一视频的多次下载合为一组，最新的记录提供标题、封面和时间；
///    同一文件夹里同名的字幕、弹幕、封面等附属文件，以及多P文件夹里其他以分P序号开头的音视频(和它们同名的附属文件)也归入这一组；
/// 3. 其余文件：多P文件夹(至少两个以 [P01] 之类分P序号开头的音视频)里这些分P音视频和同名附属文件一组，
///    其他文件按文件名(去掉扩展名)分组，同名的附属文件(video.mp4、video.zh-CN.srt、video.jpg)归入同一组。
/// </summary>
internal static partial class DownloadFileGroups
{
    internal static readonly string[] VideoExtensions = ["mp4", "mkv", "flv", "webm", "mov", "m4v", "ts"];
    internal static readonly string[] AudioExtensions = ["m4a", "mp3", "aac", "flac", "eac3", "ec3", "opus", "wav"];
    internal static readonly string[] ImageExtensions = ["jpg", "jpeg", "png", "webp", "gif", "avif"];
    /// <summary>
    /// 可作为附属文件归入同名视频的扩展名(字幕、弹幕、封面、说明，以及只下载不混流时轨道旁保留的 .resume 续传状态)；
    /// 音视频文件不会被当作别的视频的附属文件
    /// </summary>
    internal static readonly string[] SidecarExtensions = ["srt", "ass", "ssa", "vtt", "lrc", "xml", "json", "nfo", "txt", "resume", .. ImageExtensions];

    /// <param name="files">下载根目录下列出的文件(已排除受保护的文件和隐藏文件)</param>
    /// <param name="history">下载历史，最新的在前</param>
    /// <param name="metadata">临时工作文件夹(相对路径) -&gt; 说明文件内容</param>
    /// <param name="stateFor">分片的 .resume 续传状态(相对路径)；用来判断分片是否完整</param>
    /// <param name="isActive">(文件夹, av号, 最新修改时间) -&gt; 是否正在下载</param>
    /// <param name="titleFor">av号 -&gt; 已查到的视频信息，以及是否正在查询</param>
    /// <param name="hiddenFiles">下载流程产生的隐藏文件：说明文件和合并中断留下的暂存文件；只归入未完成的下载</param>
    internal static List<FileGroup> Build(
        IReadOnlyList<ListedFile> files,
        IReadOnlyList<DownloadHistoryEntry> history,
        IReadOnlyDictionary<string, DownloadWorkMetadata> metadata,
        Func<string, DownloadResumeState?>? stateFor = null,
        Func<string, string?, long, bool>? isActive = null,
        Func<string, (VideoTitleInfo? Info, bool Pending)>? titleFor = null,
        IReadOnlyList<ListedFile>? hiddenFiles = null)
    {
        stateFor ??= _ => null;
        isActive ??= (_, _, _) => false;
        titleFor ??= _ => (null, false);
        var byPath = new Dictionary<string, ListedFile>(StringComparer.Ordinal);
        foreach (var file in files) byPath.TryAdd(file.Path, file);
        var byDir = byPath.Values.GroupBy(file => DirOf(file.Path), StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.OrderBy(file => file.Path, StringComparer.Ordinal).ToList(), StringComparer.Ordinal);
        var hiddenByDir = (hiddenFiles ?? []).Where(file => DirOf(file.Path).Length > 0)
            .GroupBy(file => DirOf(file.Path), StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.OrderBy(file => file.Path, StringComparer.Ordinal).ToList(), StringComparer.Ordinal);

        // 1. 临时工作文件夹：必须有说明文件，或者文件夹名是 av 号、里面有以它命名的工作文件
        //    (别的下载工具留下的 foo.resume 之类不算，免得把普通文件夹整个当成未完成的下载)
        var workDirs = new HashSet<string>(StringComparer.Ordinal);
        foreach (var dir in byDir.Keys.Concat(hiddenByDir.Keys).Distinct(StringComparer.Ordinal))
        {
            if (dir.Length == 0) continue;
            var folderName = NameOf(dir);
            var hidden = hiddenByDir.GetValueOrDefault(dir) ?? [];
            if (metadata.ContainsKey(dir)
                || hidden.Any(file => DownloadWorkFolder.IsMetadataFileName(NameOf(file.Path))
                    || DownloadWorkFolder.IsStagedTrackOf(NameOf(file.Path), folderName))
                || (byDir.GetValueOrDefault(dir) ?? []).Any(file => DownloadWorkFolder.IsWorkFileOf(NameOf(file.Path), folderName)))
            {
                workDirs.Add(dir);
            }
        }

        // 2. 下载历史：文件 -> 视频组(最新的记录优先)
        var owner = new Dictionary<string, string>(StringComparer.Ordinal);
        var historyGroups = new Dictionary<string, List<DownloadHistoryEntry>>(StringComparer.Ordinal);
        foreach (var entry in history)
        {
            var key = "h:" + VideoKey(entry);
            foreach (var file in entry.Files)
            {
                if (!byPath.ContainsKey(file.Path) || owner.ContainsKey(file.Path)) continue;
                owner[file.Path] = key;
                if (!historyGroups.TryGetValue(key, out var entries)) historyGroups[key] = entries = [];
                if (!entries.Contains(entry)) entries.Add(entry);
            }
        }
        // 同名附属文件(字幕、弹幕、封面)：按文件名从长到短找同一文件夹里属于下载历史的同名文件
        // (video.part2.zh-CN.srt 先找 video.part2，找不到再找 video)
        var ownedStems = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (path, key) in owner)
        {
            if (!workDirs.Contains(DirOf(path))) ownedStems.TryAdd(DirOf(path) + "/" + StemOf(NameOf(path)), key);
        }
        foreach (var file in byPath.Values)
        {
            var name = NameOf(file.Path);
            if (owner.ContainsKey(file.Path) || !IsSidecar(name) || workDirs.Contains(DirOf(file.Path))) continue;
            foreach (var stem in CandidateStems(name))
            {
                if (ownedStems.TryGetValue(DirOf(file.Path) + "/" + stem, out var key))
                {
                    owner[file.Path] = key;
                    break;
                }
            }
        }
        // 多P文件夹：文件夹里属于下载历史的文件都来自同一个视频时，其他以分P序号开头的音视频(之前的版本下载的分P)
        // 和与它们同名的附属文件也归入这个视频；文件夹里别的文件(没有下载记录的其他视频等)不并入
        foreach (var (dir, list) in byDir)
        {
            if (dir.Length == 0 || workDirs.Contains(dir) || !IsMultiPageFolder(list)) continue;
            var keys = list.Where(file => owner.ContainsKey(file.Path)).Select(file => owner[file.Path]).Distinct(StringComparer.Ordinal).ToList();
            if (keys.Count != 1) continue;
            foreach (var file in PageMembers(list)) owner.TryAdd(file.Path, keys[0]);
        }

        var groups = new List<FileGroup>();
        var historyMembers = owner.GroupBy(pair => pair.Value, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Select(pair => byPath[pair.Key]).ToList(), StringComparer.Ordinal);
        foreach (var (key, entries) in historyGroups)
        {
            groups.Add(HistoryGroup(key, entries, historyMembers[key]));
        }

        // 3. 未完成的下载
        foreach (var dir in workDirs)
        {
            var members = (byDir.GetValueOrDefault(dir) ?? []).Where(file => !owner.ContainsKey(file.Path)).ToList();
            var hidden = hiddenByDir.GetValueOrDefault(dir) ?? [];
            if (members.Count == 0 && hidden.Count == 0) continue;
            foreach (var file in members) owner[file.Path] = "w:" + dir;
            groups.Add(WorkGroup(dir, members, hidden, metadata.GetValueOrDefault(dir), stateFor, isActive, titleFor));
        }

        // 4. 其余文件：多P文件夹里分P序号开头的音视频和同名附属文件一组，其他按文件名分组
        foreach (var (dir, list) in byDir)
        {
            var rest = list.Where(file => !owner.ContainsKey(file.Path)).ToList();
            if (rest.Count == 0) continue;
            if (dir.Length > 0 && IsMultiPageFolder(rest))
            {
                var pages = PageMembers(rest);
                groups.Add(PlainGroup("d:" + dir, "folder", NameOf(dir), dir, pages));
                var inFolderGroup = pages.Select(file => file.Path).ToHashSet(StringComparer.Ordinal);
                rest = rest.Where(file => !inFolderGroup.Contains(file.Path)).ToList();
                if (rest.Count == 0) continue;
            }
            var stems = new Dictionary<string, List<ListedFile>>(StringComparer.Ordinal);
            var mediaStems = rest.Where(file => IsMedia(NameOf(file.Path))).Select(file => StemOf(NameOf(file.Path)))
                .ToHashSet(StringComparer.Ordinal);
            foreach (var file in rest)
            {
                var name = NameOf(file.Path);
                var stem = IsMedia(name) ? StemOf(name)
                    : IsSidecar(name) ? CandidateStems(name).FirstOrDefault(mediaStems.Contains) ?? StemOf(name)
                    : StemOf(name);
                if (!stems.TryGetValue(stem, out var bucket)) stems[stem] = bucket = [];
                bucket.Add(file);
            }
            foreach (var (stem, bucket) in stems)
            {
                groups.Add(PlainGroup("s:" + (dir.Length == 0 ? stem : dir + "/" + stem), "file", stem, null, bucket));
            }
        }

        return groups
            .OrderByDescending(group => Math.Max(group.ModifiedTime, group.FinishedAt ?? 0))
            .ThenBy(group => group.Id, StringComparer.Ordinal)
            .ToList();
    }

    private static FileGroup HistoryGroup(string key, List<DownloadHistoryEntry> entries, List<ListedFile> members)
    {
        var latest = entries[0];
        var basic = Summarize(members);
        var folder = members.Select(file => DirOf(file.Path)).Distinct(StringComparer.Ordinal).ToList() is [var only] && only.Length > 0 && members.Count > 1
            ? only : null;
        return basic with
        {
            Id = key,
            Source = "history",
            Title = entries.Select(entry => entry.Title).FirstOrDefault(title => !string.IsNullOrWhiteSpace(title))
                ?? (basic.MainFile is { } main ? StemOf(NameOf(main)) : NameOf(members[0].Path)),
            Pic = entries.Select(entry => entry.Pic).FirstOrDefault(pic => !string.IsNullOrWhiteSpace(pic)),
            Owner = latest.Owner,
            Aid = latest.Aid,
            Bvid = latest.Bvid,
            PageUrl = latest.PageUrl,
            Url = latest.Request.Url is { Length: > 0 } url ? url : latest.Url,
            FinishedAt = latest.FinishedAt,
            Folder = folder,
            Request = latest.Request,
        };
    }

    private static FileGroup PlainGroup(string id, string source, string title, string? folder, List<ListedFile> members) =>
        Summarize(members) with { Id = id, Source = source, Title = title, Folder = folder };

    /// <summary>
    /// 已完成的组共有的部分：文件列表(音视频在前)、主文件、封面图片、大小和时间
    /// </summary>
    private static FileGroup Summarize(List<ListedFile> members)
    {
        var ordered = members.OrderBy(file => IsMedia(NameOf(file.Path)) ? 0 : 1).ThenBy(file => file.Path, StringComparer.Ordinal).ToList();
        var main = ordered.FirstOrDefault(file => HasExtension(file.Path, VideoExtensions))
            ?? ordered.FirstOrDefault(file => HasExtension(file.Path, AudioExtensions));
        return new FileGroup
        {
            Status = FileGroupStatus.Complete,
            Files = ordered.Select(file => new DownloadedFile(file.Path, file.Size, file.ModifiedTime)).ToList(),
            MainFile = main?.Path,
            CoverFile = ordered.FirstOrDefault(file => HasExtension(file.Path, ImageExtensions))?.Path,
            ModifiedTime = members.Max(file => file.ModifiedTime),
            TotalBytes = members.Sum(file => file.Size),
            FileCount = members.Count,
            MemberPaths = ordered.Select(file => file.Path).ToList(),
        };
    }

    private static FileGroup WorkGroup(string dir, List<ListedFile> members, List<ListedFile> hidden, DownloadWorkMetadata? metadata,
        Func<string, DownloadResumeState?> stateFor, Func<string, string?, long, bool> isActive,
        Func<string, (VideoTitleInfo? Info, bool Pending)> titleFor)
    {
        var paths = members.Select(file => file.Path).ToHashSet(StringComparer.Ordinal);
        var clips = new List<(ListedFile File, ClipFileName Name)>();
        var others = new List<ListedFile>();
        foreach (var file in members)
        {
            var name = NameOf(file.Path);
            if (name.EndsWith(".resume", StringComparison.Ordinal)) continue;
            if (DownloadWorkFolder.TryParseClip(name, out var clip)) clips.Add((file, clip));
            else others.Add(file);
        }
        // 合并分片或混流中断留下的隐藏暂存文件也列出来(可能有整条轨道那么大)；说明文件只计数，不列出
        var staged = hidden.Where(file => DownloadWorkFolder.TryParseStagedName(NameOf(file.Path), out _)).ToList();
        others.AddRange(staged);

        // 分片完整：续传状态记下这一段已下载完整，且本地文件就是记下的长度
        var clipList = clips
            .OrderBy(clip => clip.Name.IsVideo ? 0 : 1).ThenBy(clip => clip.Name.Page ?? 0)
            .ThenBy(clip => clip.Name.TrackBase, StringComparer.Ordinal).ThenBy(clip => clip.Name.Index)
            .Select(clip =>
            {
                var complete = clip.File.Size > 0 && paths.Contains(clip.File.Path + ".resume")
                    && stateFor(clip.File.Path + ".resume") is { Complete: true } state && state.LocalLength == clip.File.Size;
                return new FileClip(clip.File.Path, clip.Name.IsVideo ? "video" : "audio", clip.Name.Index, clip.Name.Page, clip.File.Size, complete);
            })
            .ToList();

        // av 号来自说明文件或文件夹名(工作文件夹以 av 号命名，判断工作文件夹时已核对过文件名里的 av 号)
        var folderName = NameOf(dir);
        var aid = metadata?.Aid is { Length: > 0 } metaAid ? metaAid
            : DownloadWorkFolder.IsAidFolderName(folderName) ? folderName
            : null;
        var lookup = aid is not null && string.IsNullOrWhiteSpace(metadata?.Title) ? titleFor(aid) : (null, false);
        var unavailable = lookup.Info is { Gone: true };
        var info = unavailable ? null : lookup.Info;
        var bvid = metadata?.Bvid ?? info?.Bvid ?? (unavailable ? null : BvidFor(aid));
        var title = metadata?.Title ?? info?.Title
            ?? (aid is not null ? $"未完成的下载（av{aid}）" : $"未完成的下载（{folderName}）");
        var orderedOthers = others.OrderBy(file => file.Path, StringComparer.Ordinal).ToList();
        var all = members.Concat(hidden).ToList();
        var modified = all.Max(file => file.ModifiedTime);
        return new FileGroup
        {
            Id = "w:" + dir,
            Status = FileGroupStatus.Incomplete,
            Source = "work",
            Title = title,
            TitlePending = string.IsNullOrWhiteSpace(metadata?.Title) && lookup.Info is null && lookup.Pending,
            Unavailable = unavailable,
            NeedsTitleLookup = aid is not null && string.IsNullOrWhiteSpace(metadata?.Title) && lookup.Info is null && !lookup.Pending,
            Pic = metadata?.Pic ?? info?.Pic,
            CoverFile = orderedOthers.FirstOrDefault(file => HasExtension(file.Path, ImageExtensions))?.Path,
            Owner = metadata?.Owner ?? info?.Owner,
            Aid = aid,
            Bvid = bvid,
            PageUrl = bvid is null ? null : $"https://www.bilibili.com/video/{bvid}/" + (metadata?.Page is > 1 and var page ? $"?p={page}" : ""),
            Url = metadata?.Request?.Url is { Length: > 0 } requestUrl ? requestUrl
                : metadata?.Url is { Length: > 0 } url ? url
                : unavailable ? null
                : bvid is not null ? $"https://www.bilibili.com/video/{bvid}/"
                : aid is not null ? "av" + aid : null,
            ModifiedTime = modified,
            TotalBytes = all.Sum(file => file.Size),
            FileCount = all.Count,
            Folder = dir,
            Files = orderedOthers.Select(file => new DownloadedFile(file.Path, file.Size, file.ModifiedTime)).ToList(),
            ClipCount = clipList.Count,
            CompleteClipCount = clipList.Count(clip => clip.Complete),
            Clips = clipList,
            Active = isActive(dir, aid, modified),
            Request = metadata?.Request is { Url.Length: > 0 } request ? request : null,
            MemberPaths = members.Concat(staged).Select(file => file.Path).OrderBy(path => path, StringComparer.Ordinal).ToList(),
        };
    }

    private static string? BvidFor(string? aid)
    {
        if (aid is null || !long.TryParse(aid, out var number) || number <= 0) return null;
        try { return BilibiliBvConverter.Encode(number); }
        catch (Exception) { return null; }
    }

    /// <summary>
    /// 同一视频的多次下载合为一组：按 BV号、av号、ep号识别，都没有时每条记录各一组
    /// </summary>
    internal static string VideoKey(DownloadHistoryEntry entry) =>
        entry.Bvid is { Length: > 0 } bvid ? bvid
        : entry.Aid is { Length: > 0 } aid ? "av" + aid
        : entry.Ep is { Length: > 0 } ep ? "ep" + ep
        : "id:" + entry.Id;

    /// <summary>
    /// 多P文件夹：至少两个音视频文件以分P序号开头(默认的多P文件名 [P01]标题)
    /// </summary>
    internal static bool IsMultiPageFolder(IEnumerable<ListedFile> files) =>
        files.Count(file => IsPageMedia(NameOf(file.Path))) >= 2;

    private static bool IsPageMedia(string name) => IsMedia(name) && PagePrefixRegex().IsMatch(name);

    /// <summary>
    /// 多P文件夹里属于这个视频的文件：以分P序号开头的音视频，以及与它们同名的字幕、弹幕、封面等附属文件
    /// </summary>
    internal static List<ListedFile> PageMembers(IReadOnlyCollection<ListedFile> files)
    {
        var pageStems = files.Where(file => IsPageMedia(NameOf(file.Path))).Select(file => StemOf(NameOf(file.Path)))
            .ToHashSet(StringComparer.Ordinal);
        return files.Where(file =>
        {
            var name = NameOf(file.Path);
            return IsPageMedia(name) || (IsSidecar(name) && CandidateStems(name).Any(pageStems.Contains));
        }).ToList();
    }

    internal static bool IsMedia(string name) => HasExtension(name, VideoExtensions) || HasExtension(name, AudioExtensions);

    internal static bool IsSidecar(string name) => HasExtension(name, SidecarExtensions);

    private static bool HasExtension(string path, string[] extensions)
    {
        var dot = path.LastIndexOf('.');
        return dot >= 0 && extensions.Contains(path[(dot + 1)..].ToLowerInvariant());
    }

    internal static string DirOf(string path)
    {
        var slash = path.LastIndexOf('/');
        return slash < 0 ? "" : path[..slash];
    }

    internal static string NameOf(string path) => path[(path.LastIndexOf('/') + 1)..];

    /// <summary>
    /// 附属文件可能对应的主文件名，从长到短：video.part2.zh-CN.srt -&gt; video.part2.zh-CN、video.part2、video
    /// </summary>
    internal static IEnumerable<string> CandidateStems(string name)
    {
        var stem = StemOf(name);
        while (true)
        {
            yield return stem;
            var shorter = StemOf(stem);
            if (shorter == stem) yield break;
            stem = shorter;
        }
    }

    /// <summary>
    /// 去掉最后一个扩展名；没有扩展名(或以点开头)时原样返回
    /// </summary>
    internal static string StemOf(string name)
    {
        var dot = name.LastIndexOf('.');
        return dot <= 0 ? name : name[..dot];
    }

    [GeneratedRegex(@"^(\[P\d+\]|P\d+[\s._\-\]])", RegexOptions.IgnoreCase)]
    private static partial Regex PagePrefixRegex();
}
