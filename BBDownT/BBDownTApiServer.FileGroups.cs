using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Enumeration;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BBDownT.Core;
using BBDownT.Core.Util;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;

namespace BBDownT;

/// <summary>
/// 「已下载文件」按视频分组：GET /files/groups 列出，DELETE /files/groups?id=… 删除整组。
/// 与 /files 一样只供网页前端使用，只能访问下载根目录内的文件。
/// </summary>
public partial class BBDownTApiServer
{
    /// <summary>
    /// 分组时最多统计的文件数(最新的优先)；一个4K视频的分片和续传状态就有一两百个，比 /files 的上限大
    /// </summary>
    internal const int MaxGroupedFiles = 20000;

    /// <summary>
    /// 列出文件时等待新开始的标题查询的最长时间；超时的先显示「未完成的下载（av号）」，查到后下次列出时显示
    /// </summary>
    internal TimeSpan TitleLookupWait { get; set; } = TimeSpan.FromSeconds(1.5);

    internal const int MaxTitleLookupsPerList = 8;

    private VideoTitleResolver? titles;

    /// <summary>
    /// 旧版本留下的临时文件夹没有说明文件：按av号查标题(结果缓存在内存，并补写进文件夹的说明文件)。测试替换点
    /// </summary>
    internal VideoTitleResolver Titles
    {
        get
        {
            if (titles is not null) return titles;
            Interlocked.CompareExchange(ref titles, new VideoTitleResolver(VideoTitleResolver.FetchFromViewApiAsync), null);
            return titles;
        }
        set => titles = value;
    }

    /// <summary>
    /// 测试用：任务队列(判断是否正在下载)
    /// </summary>
    internal DownloadTaskStore Tasks => taskStore;

    private void MapFileGroupsApi(RouteGroupBuilder filesApi)
    {
        filesApi.MapGet("/groups", async () => Results.Json(await ListFileGroupsAsync(), AppJsonSerializerContext.Default.FileGroupList));
        filesApi.MapDelete("/groups", (HttpContext context) =>
        {
            var query = context.Request.Query;
            var id = query["id"].ToString();
            if (string.IsNullOrWhiteSpace(id)) return Results.BadRequest("缺少要删除的组");
            var group = BuildFileGroups(null).Groups.FirstOrDefault(g => string.Equals(g.Id, id, StringComparison.Ordinal));
            if (group is null) return Results.NotFound();
            if (group.Active) return Results.Text("这个视频正在下载，下载结束后才能删除", statusCode: StatusCodes.Status409Conflict);
            // 网页带上确认时看到的文件数、大小和修改时间：列表加载后这一组有了变化(例如同一视频又下载完成了一次)时，
            // 不按新算出的成员删除确认框里没有列出的文件
            if (!MatchesExpected(query["count"], group.FileCount) || !MatchesExpected(query["bytes"], group.TotalBytes)
                || !MatchesExpected(query["mtime"], group.ModifiedTime))
            {
                return Results.Text("这一组的文件有变化，请刷新列表后再删除", statusCode: StatusCodes.Status409Conflict);
            }
            return Results.Json(DeleteFileGroup(group), AppJsonSerializerContext.Default.FileGroupDeleteResult);
        });
    }

    /// <summary>
    /// 没有传这个参数时不检查(其他调用方)；传了就必须与重新计算的值相同
    /// </summary>
    private static bool MatchesExpected(Microsoft.Extensions.Primitives.StringValues value, long actual) =>
        value.Count == 0 || string.IsNullOrEmpty(value.ToString())
        || (long.TryParse(value.ToString(), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var expected)
            && expected == actual);

    /// <summary>
    /// 工作文件夹是否正在下载：下载流程登记了这个文件夹(下载开始到结束)，或者有正在运行的任务就是这个 av 号
    /// (还在解析、马上会用到这个文件夹)
    /// </summary>
    private static bool IsWorkFolderActive(string fullDir, string? aid, IReadOnlyList<DownloadTask> running) =>
        DownloadWorkFolder.IsActive(fullDir)
        || (aid is not null && running.Any(task => string.Equals(task.Aid, aid, StringComparison.Ordinal)));

    /// <summary>
    /// 文件所在的文件夹正在下载(DELETE /files/?path= 用)：文件夹名当作 av 号
    /// </summary>
    internal bool IsInActiveDownload(string fullPath)
    {
        var dir = Path.GetDirectoryName(fullPath);
        if (string.IsNullOrEmpty(dir)) return false;
        var folderName = Path.GetFileName(dir);
        return IsWorkFolderActive(dir, DownloadWorkFolder.IsAidFolderName(folderName) ? folderName : null, taskStore.GetRunningSnapshot());
    }

    /// <summary>
    /// 分组列出下载根目录下的文件。未完成的下载没有记下标题时按av号查询：只等新开始的查询最多 <see cref="TitleLookupWait"/>，
    /// 之前已开始、仍在查询的不等(TitlePending 为 true，网页稍后再请求)
    /// </summary>
    internal async Task<FileGroupList> ListFileGroupsAsync()
    {
        var list = BuildFileGroups(null);
        var lookups = new List<Task>();
        // 一次最多新查几个(旧版本留下的文件夹通常只有一两个)，其余的下次列出时再查
        foreach (var group in list.Groups.Where(g => g.NeedsTitleLookup).Take(MaxTitleLookupsPerList))
        {
            var folder = ResolveDownloadDirectory(group.Folder);
            if (Titles.Start(group.Aid!) is not { } lookup) continue;
            lookups.Add(lookup);
            // 只给以这个 av 号命名的工作文件夹补写说明文件(GET 请求尽量不在用户的文件夹里写东西)
            if (folder is not null && string.Equals(Path.GetFileName(folder), group.Aid, StringComparison.Ordinal))
            {
                _ = SaveLookedUpTitleAsync(lookup, folder, group.Aid!);
            }
        }
        if (lookups.Count == 0) return list;
        var all = Task.WhenAll(lookups);
        await Task.WhenAny(all, Task.Delay(TitleLookupWait));
        return BuildFileGroups(null);
    }

    /// <summary>
    /// 查到标题后补写进旧文件夹的说明文件(不存在时才写)，下次不必再查
    /// </summary>
    private static async Task SaveLookedUpTitleAsync(Task<VideoTitleInfo?> lookup, string folder, string aid)
    {
        var info = await lookup;
        if (info is null || info.Gone) return;
        var now = DateTimeOffset.Now.ToUnixTimeSeconds();
        DownloadWorkFolder.TryCreate(folder, new DownloadWorkMetadata
        {
            Source = DownloadWorkFolder.SourceLookup,
            Title = info.Title,
            Owner = info.Owner,
            Pic = info.Pic,
            Aid = aid,
            Bvid = info.Bvid,
            Url = info.Bvid is null ? "av" + aid : $"https://www.bilibili.com/video/{info.Bvid}/",
            StartedAt = now,
            UpdatedAt = now,
        });
    }

    /// <param name="titleFor">av号 -&gt; 查到的标题；为null时用 <see cref="Titles"/> 的缓存(不发起查询)</param>
    internal FileGroupList BuildFileGroups(Func<string, (VideoTitleInfo? Info, bool Pending)>? titleFor)
    {
        var root = DownloadRootFullPath;
        if (!Directory.Exists(root)) return new FileGroupList(0, 0, false, []);
        var (files, hidden, truncated) = EnumerateFilesForGroups(root);
        var metadata = new Dictionary<string, DownloadWorkMetadata>(StringComparer.Ordinal);
        foreach (var dir in files.Select(file => DownloadFileGroups.DirOf(file.Path)).Where(dir => dir.Length > 0).Distinct(StringComparer.Ordinal))
        {
            var fullDir = Path.Combine(root, dir);
            if (!File.Exists(DownloadWorkFolder.MetadataPath(fullDir))) continue;
            if (DownloadWorkFolder.Read(fullDir) is { } meta) metadata[dir] = meta;
        }
        List<DownloadHistoryEntry> historyEntries;
        try
        {
            historyEntries = History.Snapshot();
        }
        catch (Exception e)
        {
            Logger.LogDebug("读取下载历史失败: {0}", e.Message);
            historyEntries = [];
        }
        var running = taskStore.GetRunningSnapshot();
        var groups = DownloadFileGroups.Build(files, historyEntries, metadata,
            relative => ReadResumeState(Path.Combine(root, relative)),
            (dir, aid, _) => IsWorkFolderActive(Path.Combine(root, dir), aid, running),
            titleFor ?? (aid => (Titles.TryGet(aid), Titles.IsPending(aid))),
            hiddenFiles: hidden);
        return new FileGroupList(groups.Count, files.Count, truncated, groups);
    }

    /// <summary>
    /// 分组用的文件列表：与 /files 相同，跳过隐藏文件(和隐藏文件夹)、受保护的文件；下载根目录包含数据目录时跳过数据目录。
    /// 另外单独返回下载流程产生的隐藏文件(说明文件、合并中断留下的暂存文件)，它们只归入未完成的下载：
    /// 删除整组时一并删除，大小也计入这一组
    /// </summary>
    private (List<ListedFile> Files, List<ListedFile> Hidden, bool Truncated) EnumerateFilesForGroups(string root)
    {
        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.System };
        var entries = new FileSystemEnumerable<(string FullPath, long Size, DateTimeOffset Modified, bool Hidden)>(root,
            (ref FileSystemEntry entry) => (entry.ToFullPath(), entry.Length, entry.LastWriteTimeUtc, entry.IsHidden), options)
        {
            ShouldIncludePredicate = (ref FileSystemEntry entry) => !entry.IsDirectory
                && (!entry.IsHidden || IsEngineHiddenFile(entry.FileName.ToString())),
            ShouldRecursePredicate = (ref FileSystemEntry entry) => !entry.IsHidden,
        };
        var all = entries
            .Where(file => (file.Hidden || !IsProtectedFile(file.FullPath)) && !IsInsideDataDirectory(file.FullPath))
            .OrderByDescending(file => file.Modified)
            .Take(MaxGroupedFiles + 1)
            .ToList();
        var truncated = all.Count > MaxGroupedFiles;
        var files = new List<ListedFile>();
        var hidden = new List<ListedFile>();
        foreach (var file in all.Take(MaxGroupedFiles))
        {
            var listed = new ListedFile(Path.GetRelativePath(root, file.FullPath).Replace('\\', '/'), file.Size, file.Modified.ToUnixTimeSeconds());
            (file.Hidden ? hidden : files).Add(listed);
        }
        return (files, hidden, truncated);
    }

    /// <summary>
    /// 下载流程产生的隐藏文件：未完成下载的说明文件(及其写入时的临时文件)、合并分片或混流的暂存文件
    /// </summary>
    private static bool IsEngineHiddenFile(string name) =>
        DownloadWorkFolder.IsMetadataFileName(name) || DownloadWorkFolder.TryParseStagedName(name, out _);

    /// <summary>
    /// 分片的续传状态(下载流程写入的 JSON)；读不出或不是有效状态时为null
    /// </summary>
    private static DownloadResumeState? ReadResumeState(string path)
    {
        try
        {
            var file = new FileInfo(path);
            return file.Exists && file.Length <= 64 * 1024 ? DownloadResumeState.Parse(File.ReadAllText(path)) : null;
        }
        catch (Exception e)
        {
            Logger.LogDebug("读取续传状态失败: {0}", e.Message);
            return null;
        }
    }

    /// <summary>
    /// 数据目录(登录信息、配置、下载历史)或程序目录在下载根目录之内时，其中的文件不分组、不删除
    /// </summary>
    internal bool IsInsideDataDirectory(string fullPath)
    {
        var root = Path.TrimEndingDirectorySeparator(DownloadRootFullPath);
        foreach (var dir in new[] { Program.APP_DIR, Program.EXE_DIR })
        {
            if (string.IsNullOrEmpty(dir)) continue;
            var normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(dir));
            // 数据目录就是下载根目录时，只保护其中的登录、配置等文件(IsProtectedFile)
            if (string.Equals(normalized, root, PathComparison)) continue;
            if (IsSameOrInside(normalized, fullPath)) return true;
        }
        return false;
    }

    private static StringComparison PathComparison => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private static bool IsSameOrInside(string directory, string fullPath)
    {
        var dir = Path.TrimEndingDirectorySeparator(directory);
        return string.Equals(fullPath, dir, PathComparison)
            || fullPath.StartsWith(dir + Path.DirectorySeparatorChar, PathComparison);
    }

    /// <summary>
    /// 下载根目录内的子文件夹(不能是根目录本身)；越出根目录或指向数据目录时返回null
    /// </summary>
    internal string? ResolveDownloadDirectory(string? relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath)) return null;
        var root = Path.TrimEndingDirectorySeparator(DownloadRootFullPath);
        string fullPath;
        try
        {
            fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(relativePath, root));
        }
        catch (Exception)
        {
            return null;
        }
        if (!fullPath.StartsWith(root + Path.DirectorySeparatorChar, PathComparison)) return null;
        if (IsInsideDataDirectory(fullPath) || !IsRealPathInsideRoot(fullPath)) return null;
        return fullPath;
    }

    /// <summary>
    /// 解析符号链接后仍在下载根目录内(防止经由指向外部的链接删除根目录以外的文件)
    /// </summary>
    private bool IsRealPathInsideRoot(string fullPath)
    {
        var realRoot = Path.TrimEndingDirectorySeparator(ResolveRealPath(DownloadRootFullPath));
        var realPath = ResolveRealPath(fullPath);
        return realPath.StartsWith(realRoot + Path.DirectorySeparatorChar, PathComparison);
    }

    /// <summary>
    /// Finder、资源管理器自动生成的文件：删除整组后文件夹里只剩它们时一并删除，以便删掉空文件夹
    /// </summary>
    private static bool IsSystemJunkFile(string name) =>
        name is ".DS_Store" or "Thumbs.db" or "desktop.ini" || name.StartsWith("._", StringComparison.Ordinal);

    /// <summary>
    /// 删除一组文件。只删除这一组列出的文件：每个都必须在下载根目录内(解析符号链接后也是)，
    /// 不能是登录、配置、下载历史等受保护的文件，也不能在数据目录里。
    /// 未完成的下载还会删除临时文件夹里的说明文件(计入删除的文件数)；合并中断留下的隐藏暂存文件已在组的文件里。
    /// 删除后为空的文件夹一并删除(不会删除下载根目录)
    /// </summary>
    internal FileGroupDeleteResult DeleteFileGroup(FileGroup group)
    {
        var deleted = 0;
        var failed = 0;
        var directories = new HashSet<string>(StringComparer.Ordinal);
        foreach (var relative in group.MemberPaths)
        {
            var fullPath = ResolveDownloadPath(relative);
            if (fullPath is null || IsInsideDataDirectory(fullPath) || !IsRealPathInsideRoot(fullPath))
            {
                failed++;
                continue;
            }
            try
            {
                if (File.Exists(fullPath))
                {
                    File.Delete(fullPath);
                    deleted++;
                }
                directories.Add(Path.GetDirectoryName(fullPath)!);
            }
            catch (Exception e)
            {
                failed++;
                Logger.LogDebug("删除文件失败: {0}", e.Message);
            }
        }
        if (group.Status == FileGroupStatus.Incomplete && ResolveDownloadDirectory(group.Folder) is { } folder && Directory.Exists(folder))
        {
            try
            {
                foreach (var file in Directory.EnumerateFiles(folder).Where(file => DownloadWorkFolder.IsMetadataFileName(Path.GetFileName(file))))
                {
                    File.Delete(file);
                    deleted++;
                }
            }
            catch (Exception e)
            {
                Logger.LogDebug("删除未完成下载的说明文件失败: {0}", e.Message);
            }
            directories.Add(folder);
        }
        foreach (var directory in directories.OrderByDescending(dir => dir.Length))
        {
            RemoveEmptyDirectories(directory);
        }
        return new FileGroupDeleteResult(deleted, failed);
    }

    /// <summary>
    /// 从 directory 开始向上删除空文件夹(只含 .DS_Store 之类系统文件的也算空)，到下载根目录为止
    /// </summary>
    private void RemoveEmptyDirectories(string directory)
    {
        var root = Path.TrimEndingDirectorySeparator(DownloadRootFullPath);
        var dir = Path.TrimEndingDirectorySeparator(directory);
        while (dir.StartsWith(root + Path.DirectorySeparatorChar, PathComparison) && Directory.Exists(dir) && !IsInsideDataDirectory(dir))
        {
            try
            {
                var entries = Directory.EnumerateFileSystemEntries(dir).ToList();
                if (entries.Any(entry => Directory.Exists(entry) || !IsSystemJunkFile(Path.GetFileName(entry)))) return;
                foreach (var junk in entries) File.Delete(junk);
                Directory.Delete(dir);
            }
            catch (Exception e)
            {
                Logger.LogDebug("删除空文件夹失败: {0}", e.Message);
                return;
            }
            dir = Path.GetDirectoryName(dir) ?? "";
        }
    }
}

/// <summary>
/// 按av号查询视频标题(B站公开的视频信息接口，不带Cookie)。结果缓存在内存；同一av号同时只查一次，
/// 查询失败(网络错误、风控等)后一段时间内不再重试；B站明确返回视频不存在或不可见时也缓存(<see cref="VideoTitleInfo.Gone"/>)
/// </summary>
internal sealed class VideoTitleResolver
{
    internal static readonly TimeSpan LookupTimeout = TimeSpan.FromSeconds(8);
    internal static readonly TimeSpan RetryAfterFailure = TimeSpan.FromMinutes(10);
    private readonly Func<string, CancellationToken, Task<VideoTitleInfo?>> fetch;
    private readonly ConcurrentDictionary<string, VideoTitleInfo> resolved = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Task<VideoTitleInfo?>> inflight = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, DateTime> failedAt = new(StringComparer.Ordinal);

    internal VideoTitleResolver(Func<string, CancellationToken, Task<VideoTitleInfo?>> fetch)
    {
        this.fetch = fetch;
    }

    internal Func<DateTime> UtcNow { get; set; } = () => DateTime.UtcNow;

    internal VideoTitleInfo? TryGet(string aid) => resolved.GetValueOrDefault(aid);

    internal bool IsPending(string aid) => inflight.ContainsKey(aid);

    /// <summary>
    /// 开始查询；已有结果、正在查询、最近失败过或不是av号时返回null
    /// </summary>
    internal Task<VideoTitleInfo?>? Start(string aid)
    {
        if (aid.Length is 0 or > 20 || !aid.All(char.IsAsciiDigit) || resolved.ContainsKey(aid)) return null;
        if (failedAt.TryGetValue(aid, out var at) && UtcNow() - at < RetryAfterFailure) return null;
        var completion = new TaskCompletionSource<VideoTitleInfo?>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!inflight.TryAdd(aid, completion.Task)) return null;
        _ = RunAsync(aid, completion);
        return completion.Task;
    }

    private async Task RunAsync(string aid, TaskCompletionSource<VideoTitleInfo?> completion)
    {
        VideoTitleInfo? info = null;
        try
        {
            using var timeout = new CancellationTokenSource(LookupTimeout);
            info = await fetch(aid, timeout.Token);
            if (info is not null && !info.Gone && string.IsNullOrWhiteSpace(info.Title)) info = null;
        }
        catch (Exception e)
        {
            Logger.LogDebug("查询视频标题失败: {0}", Logger.RedactSensitiveText(e.Message));
            info = null;
        }
        if (info is not null)
        {
            resolved[aid] = info;
            failedAt.TryRemove(aid, out _);
        }
        else
        {
            failedAt[aid] = UtcNow();
        }
        inflight.TryRemove(aid, out _);
        completion.TrySetResult(info);
    }

    /// <summary>
    /// B站公开的视频信息接口(只查标题、UP主、封面、BV号；不带Cookie)
    /// </summary>
    internal static async Task<VideoTitleInfo?> FetchFromViewApiAsync(string aid, CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.bilibili.com/x/web-interface/view?aid={aid}");
        request.Headers.TryAddWithoutValidation("User-Agent", HTTPUtil.UserAgent);
        request.Headers.TryAddWithoutValidation("Referer", "https://www.bilibili.com/");
        using var response = await HTTPUtil.AppHttpClient.SendAsync(request, token);
        if (!response.IsSuccessStatusCode) return null;
        return ParseViewResponse(await response.Content.ReadAsStringAsync(token));
    }

    internal static VideoTitleInfo? ParseViewResponse(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("code", out var code)
                || code.ValueKind != JsonValueKind.Number || !code.TryGetInt32(out var codeValue)) return null;
            // -404：视频不存在；62002：稿件不可见。其他错误(风控 -412、审核中等)当作暂时查不到
            if (codeValue is -404 or 62002) return VideoTitleInfo.Missing;
            if (codeValue != 0) return null;
            if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object) return null;
            var title = Text(data, "title");
            if (title is null) return null;
            var owner = data.TryGetProperty("owner", out var ownerElement) && ownerElement.ValueKind == JsonValueKind.Object
                ? Text(ownerElement, "name") : null;
            return new VideoTitleInfo(title, owner, Text(data, "pic"), Text(data, "bvid"));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()!.Trim()
            : null;
}
