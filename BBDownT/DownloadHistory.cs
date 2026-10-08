using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using BBDownT.Core;
using BBDownT.Core.Util;

namespace BBDownT;

/// <summary>
/// 一条下载历史：一个视频的一次下载(成功或失败)。UP主空间批量下载时清单里的每个视频各一条。
/// 文件路径相对下载根目录；Exists 在每次查询时重新检查。
/// </summary>
public sealed record DownloadHistoryEntry
{
    public string Id { get; init; } = "";
    public string TaskId { get; init; } = "";
    /// <summary>
    /// 结束时间，Unix时间戳(秒)
    /// </summary>
    public long FinishedAt { get; init; }
    /// <summary>
    /// 提交的链接或编号，已去掉分享跟踪参数
    /// </summary>
    public string Url { get; init; } = "";
    /// <summary>
    /// 在B站打开的页面链接；认不出时为null
    /// </summary>
    public string? PageUrl { get; init; }
    /// <summary>
    /// video/bangumi/cheese/list/space；未知时为null
    /// </summary>
    public string? Kind { get; init; }
    public string? Aid { get; init; }
    public string? Bvid { get; init; }
    public string? Ep { get; init; }
    public string? Title { get; init; }
    public string? Owner { get; init; }
    public string? Pic { get; init; }
    /// <summary>
    /// 实际使用的解析接口：WEB/TV/APP/INTL
    /// </summary>
    public string? Api { get; init; }
    public List<DownloadHistoryPage> Pages { get; init; } = [];
    /// <summary>
    /// 每个分P实际下载的音视频流，与 DownloadTask.Streams 相同
    /// </summary>
    public List<string> Streams { get; init; } = [];
    /// <summary>
    /// 画质、编码、音质标签(去重)，如 ["4K 超清", "HEVC", "192K"]
    /// </summary>
    public List<string> StreamTags { get; init; } = [];
    public List<DownloadHistoryFile> Files { get; init; } = [];
    public long TotalBytes { get; init; }
    public bool Success { get; init; }
    /// <summary>
    /// 每个分P的输出文件都已存在，这次没有重新下载(仍算成功；Files、TotalBytes 是已有文件的)
    /// </summary>
    public bool Skipped { get; init; }
    public string? Error { get; init; }
    /// <summary>
    /// 重新下载用的请求，可直接提交给 /add-task；不含Cookie、Token等登录信息
    /// </summary>
    public DownloadHistoryRequest Request { get; init; } = new();
}

public sealed record DownloadHistoryPage(int Index, string Title);

public sealed record DownloadHistoryFile(string Path, long Size, bool Exists);

/// <summary>
/// 重新下载所需的选项，字段名与 /add-task 的请求相同；只保存与默认值不同的字段
/// </summary>
public sealed record DownloadHistoryRequest
{
    public string Url { get; init; } = "";
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public bool? UseTvApi { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public bool? UseAppApi { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public bool? UseIntlApi { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public bool? AudioOnly { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public bool? VideoOnly { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public bool? SubOnly { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public bool? DanmakuOnly { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public bool? CoverOnly { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? DfnPriority { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? EncodingPriority { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? VideoStream { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? AudioStream { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? AudioLanguage { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? SelectPage { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public bool? SkipSubtitle { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? SubtitleLanguage { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? AiSubtitlePolicy { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public bool? SkipAi { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public bool? DownloadDanmaku { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? DownloadDanmakuFormats { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public bool? SkipCover { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? FilePattern { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? MultiFilePattern { get; init; }
    /// <summary>
    /// UP主空间链接：按清单下载全部投稿；此时同时保存投稿之间的间隔
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public bool? DownloadAll { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public int? DelayPerVideo { get; init; }

    internal static DownloadHistoryRequest From(MyOption option)
    {
        static bool? IfTrue(bool value) => value ? true : null;
        static string? Text(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        return new DownloadHistoryRequest
        {
            Url = DownloadHistory.CleanUrl(option.Url),
            UseTvApi = IfTrue(option.UseTvApi),
            UseAppApi = IfTrue(option.UseAppApi),
            UseIntlApi = IfTrue(option.UseIntlApi),
            AudioOnly = IfTrue(option.AudioOnly),
            VideoOnly = IfTrue(option.VideoOnly),
            SubOnly = IfTrue(option.SubOnly),
            DanmakuOnly = IfTrue(option.DanmakuOnly),
            CoverOnly = IfTrue(option.CoverOnly),
            DfnPriority = Text(option.DfnPriority),
            EncodingPriority = Text(option.EncodingPriority),
            VideoStream = Text(option.VideoStream),
            AudioStream = Text(option.AudioStream),
            AudioLanguage = Text(option.AudioLanguage),
            SelectPage = Text(option.SelectPage),
            SkipSubtitle = IfTrue(option.SkipSubtitle),
            SubtitleLanguage = Text(option.SubtitleLanguage),
            AiSubtitlePolicy = Text(option.AiSubtitlePolicy),
            SkipAi = option.SkipAi ? null : false,
            DownloadDanmaku = IfTrue(option.DownloadDanmaku),
            DownloadDanmakuFormats = Text(option.DownloadDanmakuFormats),
            SkipCover = IfTrue(option.SkipCover),
            FilePattern = Text(option.FilePattern),
            MultiFilePattern = Text(option.MultiFilePattern),
            DownloadAll = IfTrue(option.DownloadAll),
            DelayPerVideo = option.DownloadAll ? option.DelayPerVideo : null,
        };
    }
}

/// <summary>
/// 数据目录里 history.json 的内容；Entries 按记录先后排列(最旧的在前)
/// </summary>
public sealed record DownloadHistoryDocument(int Version, List<DownloadHistoryEntry> Entries);

/// <summary>
/// GET /history 的返回：Total 为全部记录数，Matched 为符合搜索词的记录数，Items 为本页(最新的在前)
/// </summary>
public sealed record DownloadHistoryList(int Total, int Matched, int Offset, int Limit, List<DownloadHistoryEntry> Items);

/// <summary>
/// 一个视频结束时(成功或失败)交给服务器写入历史的内容；文件为绝对路径
/// </summary>
internal sealed record FinishedVideo(
    string Url,
    DownloadHistoryRequest Request,
    string? Aid,
    string? Kind,
    string? Title,
    string? Owner,
    string? Pic,
    string? Api,
    List<DownloadHistoryPage> Pages,
    List<string> Streams,
    List<string> StreamTags,
    List<string> Files,
    bool Success,
    string? Error,
    bool Skipped = false)
{
    /// <summary>
    /// 任务没有逐个视频的记录时(如解析前就失败、只导出UP主投稿清单)，按整个任务记一条
    /// </summary>
    internal static FinishedVideo FromTask(DownloadTask snapshot, DownloadHistoryRequest request, bool succeeded) => new(
        snapshot.Url,
        request,
        snapshot.Aid,
        SpaceBatchDownload.IsSpaceUrl(snapshot.Url) ? "space" : null,
        snapshot.Title,
        null,
        snapshot.Pic,
        null,
        [],
        [.. snapshot.Streams],
        [],
        [.. snapshot.SavePaths],
        succeeded,
        succeeded ? null : snapshot.Error);
}

internal static partial class DownloadHistory
{
    internal const string FileName = "history.json";
    internal const int MaxErrorLength = 300;

    /// <summary>
    /// 与网页前端 cleanLink 相同：去掉分享跟踪参数、登录凭证参数和 #片段；不是 http(s) 链接时原样返回(去掉首尾空白)
    /// </summary>
    internal static readonly string[] TrackingParams =
    [
        "spm_id_from", "vd_source", "from_spmid", "share_source", "share_medium", "share_plat",
        "share_session_id", "share_tag", "share_from", "unique_k", "bbid", "ts", "timestamp", "buvid", "is_story_h5", "plat_id"
    ];

    /// <summary>
    /// 登录凭证参数(与 Logger 遮盖的一致，名称不区分大小写)：直接删掉，不保存也不随「重新下载」再提交。
    /// 不能换成 &lt;redacted&gt;，否则重新下载时链接无效
    /// </summary>
    internal static readonly string[] CredentialParams =
    [
        "access_key", "access_token", "refresh_token", "SESSDATA", "bili_jct", "DedeUserID", "ac_time_value"
    ];

    internal static string CleanUrl(string? url)
    {
        var value = url?.Trim() ?? "";
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return value;
        }
        var query = uri.Query.TrimStart('?')
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Where(pair => KeepQueryParam(Uri.UnescapeDataString(pair.Split('=', 2)[0].Replace('+', ' '))))
            .ToList();
        return uri.GetLeftPart(UriPartial.Path) + (query.Count > 0 ? "?" + string.Join('&', query) : "");
    }

    private static bool KeepQueryParam(string name) =>
        !TrackingParams.Contains(name, StringComparer.Ordinal) && !CredentialParams.Contains(name, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 把一个视频的下载结果整理成历史记录。toRelativePath 把绝对路径换成相对下载根目录的路径，
    /// 不在下载根目录内的文件返回null(这类文件无法通过文件接口访问，不记录)；
    /// relativizePaths 把失败原因里下载根目录下的绝对路径换成相对路径
    /// </summary>
    internal static DownloadHistoryEntry CreateEntry(string taskId, long finishedAt, FinishedVideo video, Func<string, string?> toRelativePath,
        Func<string, string>? relativizePaths = null)
    {
        var url = CleanUrl(video.Url);
        var (aid, bvid, ep) = Identify(video.Aid, url);
        var kind = video.Kind
            ?? (video.Aid?.StartsWith("cheese:", StringComparison.Ordinal) == true || url.Contains("cheese/", StringComparison.OrdinalIgnoreCase)
                ? "cheese" : null);
        var files = new List<DownloadHistoryFile>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var path in video.Files)
        {
            var relative = toRelativePath(path);
            if (relative is null || !seen.Add(relative)) continue;
            var info = new FileInfo(path);
            files.Add(new DownloadHistoryFile(relative, info.Exists ? info.Length : 0, info.Exists));
        }
        return new DownloadHistoryEntry
        {
            Id = Guid.NewGuid().ToString("N"),
            TaskId = taskId,
            FinishedAt = finishedAt,
            Url = url,
            PageUrl = PageUrlFor(url, kind, bvid, ep),
            Kind = kind,
            Aid = aid,
            Bvid = bvid,
            Ep = ep,
            Title = string.IsNullOrWhiteSpace(video.Title) ? null : video.Title,
            Owner = string.IsNullOrWhiteSpace(video.Owner) ? null : video.Owner,
            Pic = string.IsNullOrWhiteSpace(video.Pic) ? null : video.Pic,
            Api = video.Api ?? RequestedApi(video.Request),
            Pages = [.. video.Pages],
            Streams = [.. video.Streams],
            StreamTags = video.StreamTags.Where(tag => !string.IsNullOrWhiteSpace(tag)).Distinct(StringComparer.Ordinal).ToList(),
            Files = files,
            TotalBytes = files.Sum(file => file.Size),
            Success = video.Success,
            Skipped = video.Success && video.Skipped,
            Error = video.Success ? null : ShortError(video.Error, relativizePaths),
            Request = video.Request with { Url = CleanUrl(video.Request.Url) }
        };
    }

    /// <summary>
    /// 历史里显示的失败原因：常见系统异常换成中文说明，下载根目录换成相对路径，遮盖敏感值，过长时截断
    /// </summary>
    internal static string? ShortError(string? error, Func<string, string>? relativizePaths = null)
    {
        if (string.IsNullOrWhiteSpace(error)) return null;
        var text = error.Trim();
        if (relativizePaths is not null) text = relativizePaths(text);
        text = Logger.RedactSensitiveText(FriendlyError(text));
        return text.Length <= MaxErrorLength ? text : text[..(MaxErrorLength - 1)] + "…";
    }

    /// <summary>
    /// 常见系统异常的中文说明。NativeAOT 发布开启了 UseSystemResourceKeys，系统异常的 Message 只有资源键
    /// (如「Arg_KeyNotFound」「UnauthorizedAccess_IODenied_Path, 路径」)；普通运行时是英文原文，两种都认。
    /// 认不出的原样返回
    /// </summary>
    internal static string FriendlyError(string text)
    {
        if (KeyNotFoundRegex().IsMatch(text)) return "视频不存在或无法获取信息（可能已删除、需要登录或有地区限制）";
        if (AccessDeniedRegex().Match(text) is { Success: true } denied) return WithPath("没有写入权限", denied);
        if (FileNotFoundRegex().Match(text) is { Success: true } file) return WithPath("找不到文件", file);
        if (PathNotFoundRegex().Match(text) is { Success: true } directory) return WithPath("找不到目录", directory);
        if (DiskFullRegex().IsMatch(text)) return "磁盘空间不足";
        if (HttpTimeoutRegex().IsMatch(text)) return "网络请求超时";
        if (HttpStatusRegex().Match(text) is { Success: true } status) return $"网络请求失败（HTTP {status.Groups["code"].Value}）";
        return text;
    }

    private static string WithPath(string message, Match match) =>
        match.Groups["path"] is { Success: true, Value.Length: > 0 } path ? $"{message}：{path.Value}" : message;

    private static string RequestedApi(DownloadHistoryRequest request) =>
        // 与 Program.GetApiType 相同的优先级
        request.UseIntlApi == true ? "INTL" : request.UseAppApi == true ? "APP" : request.UseTvApi == true ? "TV" : "WEB";

    /// <summary>
    /// 从解析出的输入标识(数字AID、ep:123、cheese:123)或链接里取出 AID、BV号、EP号
    /// </summary>
    internal static (string? Aid, string? Bvid, string? Ep) Identify(string? inputId, string url)
    {
        string? aid = null, bvid = null, ep = null;
        var id = inputId?.Trim() ?? "";
        if (id.Length > 0 && id.All(char.IsAsciiDigit)) aid = id;
        else if (id.StartsWith("ep:", StringComparison.Ordinal) || id.StartsWith("cheese:", StringComparison.Ordinal))
        {
            var value = id[(id.IndexOf(':') + 1)..];
            if (value.Length > 0 && value.All(char.IsAsciiDigit)) ep = value;
        }

        if (aid is null && ep is null)
        {
            if (BvRegex().Match(url) is { Success: true } bv) bvid = "BV" + bv.Groups[1].Value;
            else if (AvRegex().Match(url) is { Success: true } av) aid = av.Groups[1].Value;
            else if (EpRegex().Match(url) is { Success: true } epMatch) ep = epMatch.Groups[1].Value;
        }
        if (bvid is null && aid is not null && long.TryParse(aid, out var number) && number > 0)
        {
            try { bvid = BilibiliBvConverter.Encode(number); }
            catch (Exception) { bvid = null; }
        }
        return (aid, bvid, ep);
    }

    private static string? PageUrlFor(string url, string? kind, string? bvid, string? ep)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
            && (BBDownTUtil.IsBilibiliHost(uri.Host) || BBDownTUtil.IsShortLinkUri(url)))
        {
            return url;
        }
        if (bvid is not null) return $"https://www.bilibili.com/video/{bvid}/";
        if (ep is not null) return kind == "cheese" ? $"https://www.bilibili.com/cheese/play/ep{ep}" : $"https://www.bilibili.com/bangumi/play/ep{ep}";
        if (SsRegex().Match(url) is { Success: true } ss) return $"https://www.bilibili.com/bangumi/play/ss{ss.Groups[1].Value}";
        return null;
    }

    /// <summary>
    /// 搜索：按空白拆成多个词，每个词都要出现在标题、UP主、BV号、av号、ep号、链接或分P标题之一中(不区分大小写)
    /// </summary>
    internal static bool Matches(DownloadHistoryEntry entry, string[] terms)
    {
        foreach (var term in terms)
        {
            if (!Contains(entry.Title, term) && !Contains(entry.Owner, term) && !Contains(entry.Bvid, term)
                && !Contains(entry.Aid is null ? null : "av" + entry.Aid, term)
                && !Contains(entry.Ep is null ? null : "ep" + entry.Ep, term)
                && !Contains(entry.Url, term)
                && !entry.Pages.Any(page => Contains(page.Title, term)))
            {
                return false;
            }
        }
        return true;
    }

    private static bool Contains(string? value, string term) =>
        value is not null && value.Contains(term, StringComparison.OrdinalIgnoreCase);

    [GeneratedRegex(@"\b[Bb][Vv](1[0-9A-Za-z]{9})\b")]
    private static partial Regex BvRegex();

    [GeneratedRegex(@"(?:^|/video/)[Aa][Vv](\d+)\b")]
    private static partial Regex AvRegex();

    [GeneratedRegex(@"(?:^|/)ep(\d+)\b")]
    private static partial Regex EpRegex();

    [GeneratedRegex(@"(?:^|/)ss(\d+)\b")]
    private static partial Regex SsRegex();

    [GeneratedRegex(@"^(?:Arg_KeyNotFound(?:WithKey)?\b|The given key\b.*\bwas not present in the dictionary)")]
    private static partial Regex KeyNotFoundRegex();

    [GeneratedRegex(@"^(?:UnauthorizedAccess_IODenied_(?:Path, (?<path>.+)|NoPathName)|Access to the path '(?<path>.+)' is denied\.?|Access to the path is denied\.?)$", RegexOptions.Singleline)]
    private static partial Regex AccessDeniedRegex();

    [GeneratedRegex(@"^(?:IO_FileNotFound(?:_FileName, (?<path>.+))?|Could not find file '(?<path>.+)'\.?)$", RegexOptions.Singleline)]
    private static partial Regex FileNotFoundRegex();

    [GeneratedRegex(@"^(?:IO_PathNotFound_(?:Path, (?<path>.+)|NoPathName)|Could not find a part of the path '(?<path>.+)'\.?)$", RegexOptions.Singleline)]
    private static partial Regex PathNotFoundRegex();

    [GeneratedRegex(@"No space left on device|IO_DiskFull|not enough space on the disk", RegexOptions.IgnoreCase)]
    private static partial Regex DiskFullRegex();

    [GeneratedRegex(@"^(?:net_http_request_timedout\b|The request was canceled due to the configured HttpClient\.Timeout)")]
    private static partial Regex HttpTimeoutRegex();

    [GeneratedRegex(@"^(?:net_http_message_not_success_statuscode(?:_reason)?, (?<code>\d{3})|Response status code does not indicate success: (?<code>\d{3}))")]
    private static partial Regex HttpStatusRegex();
}

/// <summary>
/// 下载历史的持久化存储：数据目录下的 history.json(非 Windows 系统上权限为 0600，只有本用户可读写)。
/// 线程安全；每次修改都先写临时文件再改名替换，写到一半退出也不会留下半个文件(残留的临时文件下次启动时清理)；
/// 最多保留 capacity 条(最旧的先删)；文件缺失时从空开始，文件损坏时备份后从空开始。
/// 文件被别的进程改过时(如命令行 serve 和 App 共用数据目录)，下次读写前重新读取；
/// 但两个进程同时写入时仍可能丢失其中一方的修改，不建议多个服务共用一个数据目录。
/// </summary>
internal sealed class DownloadHistoryStore
{
    internal const int DefaultCapacity = 2000;
    internal const int CurrentVersion = 1;
    /// <summary>
    /// 超过这个时间的临时文件是写到一半退出留下的(正常写入不会这么久)，读取时删除
    /// </summary>
    internal static readonly TimeSpan StaleTempFileAge = TimeSpan.FromMinutes(10);
    private readonly object stateLock = new();
    private readonly int capacity;
    private List<DownloadHistoryEntry>? entries;
    /// <summary>
    /// 内存中的记录对应的文件状态；文件不存在时为null
    /// </summary>
    private FileStamp? loadedStamp;
    /// <summary>
    /// 上次写入失败，内存中有尚未写入文件的修改
    /// </summary>
    private bool unsaved;

    private readonly record struct FileStamp(DateTime LastWriteTimeUtc, long Length);

    internal DownloadHistoryStore(string filePath, int capacity = DefaultCapacity)
    {
        FilePath = Path.GetFullPath(filePath);
        this.capacity = Math.Max(1, capacity);
    }

    internal string FilePath { get; }

    internal int Count
    {
        get { lock (stateLock) return Load().Count; }
    }

    /// <summary>
    /// 立即读取文件(启动时调用，文件损坏的提示能早些出现)
    /// </summary>
    internal void EnsureLoaded()
    {
        lock (stateLock) Load();
    }

    internal void Add(DownloadHistoryEntry entry) => AddRange([entry]);

    internal void AddRange(IReadOnlyCollection<DownloadHistoryEntry> newEntries)
    {
        if (newEntries.Count == 0) return;
        lock (stateLock)
        {
            var list = Load();
            list.AddRange(newEntries);
            if (list.Count > capacity) list.RemoveRange(0, list.Count - capacity);
            Save(list);
        }
    }

    internal bool Remove(string id)
    {
        lock (stateLock)
        {
            var list = Load();
            if (list.RemoveAll(entry => string.Equals(entry.Id, id, StringComparison.Ordinal)) == 0) return false;
            Save(list);
            return true;
        }
    }

    internal int Clear()
    {
        lock (stateLock)
        {
            var list = Load();
            var removed = list.Count;
            list.Clear();
            Save(list);
            return removed;
        }
    }

    /// <summary>
    /// 最新的在前；q 按 <see cref="DownloadHistory.Matches"/> 过滤
    /// </summary>
    internal DownloadHistoryList Query(string? q, int offset, int limit)
    {
        var terms = (q ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        offset = Math.Max(0, offset);
        limit = Math.Max(1, limit);
        lock (stateLock)
        {
            var list = Load();
            var matched = new List<DownloadHistoryEntry>();
            for (var i = list.Count - 1; i >= 0; i--)
            {
                if (terms.Length == 0 || DownloadHistory.Matches(list[i], terms)) matched.Add(list[i]);
            }
            return new DownloadHistoryList(list.Count, matched.Count, offset, limit, matched.Skip(offset).Take(limit).ToList());
        }
    }

    /// <summary>
    /// 全部记录，最新的在前
    /// </summary>
    internal List<DownloadHistoryEntry> Snapshot()
    {
        lock (stateLock)
        {
            var list = Load();
            return Enumerable.Reverse(list).ToList();
        }
    }

    /// <summary>
    /// 内存中的记录。首次调用时读取文件(并清理残留的临时文件)；之后文件被别的进程改过时重新读取，
    /// 本进程上次没能写入的记录按 Id 合并进去，下次修改时一并写入
    /// </summary>
    private List<DownloadHistoryEntry> Load()
    {
        if (entries is null) RemoveStaleTempFiles();
        // 先取文件状态再读取：读取期间文件又被替换时，下次还会再读一次
        var stamp = CurrentStamp();
        if (entries is not null && stamp == loadedStamp) return entries;
        var pending = unsaved ? entries : null;
        entries = ReadFile();
        // 文件损坏时已改名备份，此后按文件不存在处理
        loadedStamp = File.Exists(FilePath) ? stamp : null;
        if (pending is not null)
        {
            var ids = entries.Select(entry => entry.Id).ToHashSet(StringComparer.Ordinal);
            entries.AddRange(pending.Where(entry => !ids.Contains(entry.Id)));
            if (entries.Count > capacity) entries.RemoveRange(0, entries.Count - capacity);
        }
        return entries;
    }

    private List<DownloadHistoryEntry> ReadFile()
    {
        try
        {
            var file = new FileInfo(FilePath);
            if (!file.Exists || file.Length == 0) return [];
            DownloadHistoryDocument? document;
            using (var stream = file.OpenRead())
            {
                document = JsonSerializer.Deserialize(stream, AppJsonSerializerContext.Default.DownloadHistoryDocument);
            }
            if (document is null) throw new JsonException("内容为 null");
            var list = (document.Entries ?? []).Where(entry => entry is not null && !string.IsNullOrEmpty(entry.Id))
                .Select(Normalize).ToList();
            if (list.Count > capacity) list.RemoveRange(0, list.Count - capacity);
            return list;
        }
        catch (Exception e)
        {
            var backup = BackUpUnreadableFile();
            Logger.LogWarn($"下载历史文件无法读取（{e.GetType().Name}: {Logger.RedactSensitiveText(e.Message)}），"
                + (backup is null ? "将从空记录开始。" : $"已备份为 {Path.GetFileName(backup)}，将从空记录开始。"));
            return [];
        }
    }

    private FileStamp? CurrentStamp()
    {
        try
        {
            var file = new FileInfo(FilePath);
            return file.Exists ? new FileStamp(file.LastWriteTimeUtc, file.Length) : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// 删除写到一半退出留下的 history.json.&lt;32位十六进制&gt;.tmp(修改时间早于 <see cref="StaleTempFileAge"/>)；
    /// 别的进程正在写的临时文件是新的，不会删
    /// </summary>
    private void RemoveStaleTempFiles()
    {
        try
        {
            var directory = Path.GetDirectoryName(FilePath)!;
            if (!Directory.Exists(directory)) return;
            var prefix = Path.GetFileName(FilePath) + ".";
            foreach (var temp in Directory.EnumerateFiles(directory, prefix + "*.tmp"))
            {
                var middle = Path.GetFileName(temp)[prefix.Length..^".tmp".Length];
                if (middle.Length != 32 || !middle.All(char.IsAsciiHexDigitLower)) continue;
                try
                {
                    if (DateTime.UtcNow - File.GetLastWriteTimeUtc(temp) > StaleTempFileAge) File.Delete(temp);
                }
                catch (Exception e)
                {
                    Logger.LogDebug("删除下载历史临时文件失败: {0}", e.Message);
                }
            }
        }
        catch (Exception e)
        {
            Logger.LogDebug("清理下载历史临时文件失败: {0}", e.Message);
        }
    }

    /// <summary>
    /// 旧文件或手工编辑过的文件里可能缺少列表字段
    /// </summary>
    private static DownloadHistoryEntry Normalize(DownloadHistoryEntry entry) => entry with
    {
        Url = entry.Url ?? "",
        TaskId = entry.TaskId ?? "",
        Pages = entry.Pages?.Where(page => page is not null).ToList() ?? [],
        Streams = entry.Streams?.Where(stream => stream is not null).ToList() ?? [],
        StreamTags = entry.StreamTags?.Where(tag => tag is not null).ToList() ?? [],
        Files = entry.Files?.Where(file => file is not null && !string.IsNullOrEmpty(file.Path)).ToList() ?? [],
        Request = entry.Request ?? new DownloadHistoryRequest { Url = entry.Url ?? "" }
    };

    private string? BackUpUnreadableFile()
    {
        try
        {
            if (!File.Exists(FilePath)) return null;
            var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            var backup = $"{FilePath}.corrupt-{stamp}";
            for (var n = 2; File.Exists(backup); n++) backup = $"{FilePath}.corrupt-{stamp}-{n}";
            File.Move(FilePath, backup);
            return backup;
        }
        catch (Exception e)
        {
            Logger.LogWarn($"备份损坏的下载历史文件失败：{e.Message}");
            return null;
        }
    }

    private void Save(List<DownloadHistoryEntry> list)
    {
        var directory = Path.GetDirectoryName(FilePath)!;
        var temp = Path.Combine(directory, $"{Path.GetFileName(FilePath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            Directory.CreateDirectory(directory);
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
            // 历史里是观看和下载记录：与 BBDownT.web.json 一样只有本用户可读写
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            using (var stream = new FileStream(temp, options))
            {
                JsonSerializer.Serialize(stream, new DownloadHistoryDocument(CurrentVersion, list),
                    AppJsonSerializerContext.Default.DownloadHistoryDocument);
                stream.Flush(flushToDisk: true);
            }
            // 改名不改变修改时间和大小，记下的就是替换后 history.json 的状态
            var written = new FileInfo(temp);
            var stamp = new FileStamp(written.LastWriteTimeUtc, written.Length);
            File.Move(temp, FilePath, overwrite: true);
            loadedStamp = stamp;
            unsaved = false;
        }
        catch (Exception e)
        {
            // 写入失败(磁盘满、权限等)不影响下载；内存中的记录仍然可用，下次修改时再尝试写入
            unsaved = true;
            Logger.LogWarn($"保存下载历史失败：{e.Message}");
            try { File.Delete(temp); } catch (Exception) { }
        }
    }
}
