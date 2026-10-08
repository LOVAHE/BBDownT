using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using BBDownT.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace BBDownT;

/// <summary>
/// 下载历史：任务结束时(成功或失败)记录到数据目录的 history.json，服务重启、「清除已完成」后仍保留。
/// 接口与 /files 一样只供网页前端使用：需要Token(启用时)，未启用Token时只接受同源请求。
/// </summary>
public partial class BBDownTApiServer
{
    internal const int DefaultHistoryPageSize = 50;
    internal const int MaxHistoryPageSize = 500;
    private DownloadHistoryStore? history;

    internal DownloadHistoryStore History
    {
        get
        {
            if (history is not null) return history;
            var path = serverOptions.HistoryPath ?? Path.Combine(Program.APP_DIR, DownloadHistory.FileName);
            Interlocked.CompareExchange(ref history, new DownloadHistoryStore(path, serverOptions.MaxHistoryEntries), null);
            return history;
        }
    }

    private void MapHistoryApi(WebApplication app)
    {
        var historyApi = app.MapGroup("/history");
        historyApi.MapGet("/", (HttpContext context) =>
        {
            var query = context.Request.Query;
            var result = QueryHistory(query["q"].ToString(), ParseInt(query["offset"].ToString(), 0), ParseInt(query["limit"].ToString(), DefaultHistoryPageSize));
            return Results.Json(result, AppJsonSerializerContext.Default.DownloadHistoryList);
        });
        // 只删除记录，不删除文件
        historyApi.MapDelete("/{id}", (string id) => History.Remove(id) ? Results.Ok() : Results.NotFound());
        historyApi.MapDelete("/", () =>
        {
            History.Clear();
            return Results.Ok();
        });
    }

    private static int ParseInt(string text, int fallback) => int.TryParse(text, out var value) ? value : fallback;

    /// <summary>
    /// 查询下载历史(最新的在前)；每个文件的 Exists 在此时按下载根目录重新检查
    /// </summary>
    internal DownloadHistoryList QueryHistory(string? q, int offset, int limit)
    {
        limit = limit <= 0 ? DefaultHistoryPageSize : Math.Min(limit, MaxHistoryPageSize);
        var result = History.Query(q, Math.Max(0, offset), limit);
        return result with
        {
            Items = result.Items.Select(entry => entry with
            {
                Files = entry.Files.Select(file => file with { Exists = DownloadedFileExists(file.Path) }).ToList()
            }).ToList()
        };
    }

    private bool DownloadedFileExists(string relativePath) =>
        ResolveDownloadPath(relativePath) is { } fullPath && File.Exists(fullPath);

    /// <summary>
    /// 文件相对路径 -> 视频标题；同一文件出现在多条记录里时取最新的
    /// </summary>
    private Dictionary<string, string> HistoryTitlesByFile()
    {
        var titles = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            foreach (var entry in History.Snapshot())
            {
                if (string.IsNullOrWhiteSpace(entry.Title)) continue;
                foreach (var file in entry.Files) titles.TryAdd(file.Path, entry.Title);
            }
        }
        catch (Exception e)
        {
            Logger.LogDebug("读取下载历史失败: {0}", e.Message);
        }
        return titles;
    }

    /// <summary>
    /// 普通任务：在任务上挂好记录回调，返回重新下载用的选项；只看信息的任务不记录，返回null
    /// </summary>
    private DownloadHistoryRequest? BeginHistory(ServeRequestOptions option, DownloadTask task)
    {
        if (option.OnlyShowInfo) return null;
        var taskId = task.TaskId;
        task.VideoFinished = video => RecordHistory(taskId, video);
        return DownloadHistoryRequest.From(option);
    }

    /// <summary>
    /// 任务结束：没有逐个视频的记录时(解析前就失败、只导出UP主投稿清单等)按整个任务记一条
    /// </summary>
    private void RecordTaskHistory(DownloadTask task, DownloadHistoryRequest request, bool succeeded)
    {
        task.VideoFinished = null;
        if (task.HasVideoRecords) return;
        RecordHistory(task.TaskId, FinishedVideo.FromTask(task.CreateSnapshot(), request, succeeded));
    }

    internal void RecordHistory(string taskId, FinishedVideo video)
    {
        try
        {
            History.Add(DownloadHistory.CreateEntry(taskId, DateTimeOffset.Now.ToUnixTimeSeconds(), video, ToDownloadRelativePath,
                RelativizeDownloadPaths));
        }
        catch (Exception e)
        {
            Logger.LogWarn($"记录下载历史失败：{e.Message}");
        }
    }

    /// <summary>
    /// 把文字(如失败原因)中下载根目录下的绝对路径换成相对路径，下载根目录本身换成「下载目录」，
    /// 历史里不长期保存服务器上的绝对路径。配置的路径和解析符号链接后的真实路径都认
    /// </summary>
    internal string RelativizeDownloadPaths(string text)
    {
        var root = Path.TrimEndingDirectorySeparator(DownloadRootFullPath);
        var options = RegexOptions.CultureInvariant | (OperatingSystem.IsWindows() ? RegexOptions.IgnoreCase : RegexOptions.None);
        // 先换较长的(如 /private/tmp/x 先于 /tmp/x)
        foreach (var prefix in new[] { root, Path.TrimEndingDirectorySeparator(ResolveRealPath(root)) }
                     .Distinct().OrderByDescending(prefix => prefix.Length))
        {
            if (Path.GetPathRoot(prefix) == prefix) continue; // 下载根目录是磁盘根目录时不替换
            // 只换完整路径的开头：前面是开头、空白、引号、括号、冒号或逗号
            var start = @"(?<=^|[\s'""(（:：,，])" + Regex.Escape(prefix);
            text = Regex.Replace(text, start + @"[\\/]", "", options);
            text = Regex.Replace(text, start + @"(?=$|['""\s,，。；;:：)）])", "下载目录", options);
        }
        return text;
    }

    /// <summary>
    /// 绝对路径 -> 相对下载根目录的路径(用 / 分隔)；不在下载根目录内或是受保护的文件时返回null。
    /// 任务执行时的工作目录取自系统，符号链接已解析(如 macOS 的 /tmp 实为 /private/tmp)，
    /// 所以先按原样比较，不在根目录内时再把两边都换成解析符号链接后的真实路径比较。
    /// </summary>
    internal string? ToDownloadRelativePath(string path)
    {
        var root = DownloadRootFullPath;
        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(path, root);
        }
        catch (Exception)
        {
            return null;
        }
        return RelativeInside(root, fullPath) ?? RelativeInside(ResolveRealPath(root), RealFilePath(fullPath));
    }

    private string? RelativeInside(string root, string fullPath)
    {
        string relative;
        try
        {
            relative = Path.GetRelativePath(root, fullPath);
        }
        catch (Exception)
        {
            return null;
        }
        if (Path.IsPathRooted(relative) || relative == ".") return null;
        var normalized = relative.Replace('\\', '/');
        if (normalized == ".." || normalized.StartsWith("../", StringComparison.Ordinal)) return null;
        // 再按配置的下载根目录检查一次(同时排除登录、配置等受保护的文件)
        return ResolveDownloadPath(normalized) is null ? null : normalized;
    }

    private static string RealFilePath(string fullPath)
    {
        var directory = Path.GetDirectoryName(fullPath);
        return string.IsNullOrEmpty(directory) ? fullPath : Path.Combine(ResolveRealPath(directory), Path.GetFileName(fullPath));
    }

    /// <summary>
    /// 逐级解析路径中的符号链接(链接指向的路径也继续解析)；无法解析的部分保持原样
    /// </summary>
    internal static string ResolveRealPath(string fullPath) => ResolveRealPath(fullPath, 0);

    private static string ResolveRealPath(string fullPath, int depth)
    {
        if (depth > 16) return fullPath;
        try
        {
            var pathRoot = Path.GetPathRoot(fullPath);
            if (string.IsNullOrEmpty(pathRoot)) return fullPath;
            var current = pathRoot;
            foreach (var part in fullPath[pathRoot.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
            {
                var next = Path.Combine(current, part);
                var info = new DirectoryInfo(next);
                if (info.LinkTarget is not null && info.ResolveLinkTarget(returnFinalTarget: true) is { } target)
                {
                    next = ResolveRealPath(target.FullName, depth + 1);
                }
                current = next;
            }
            return current;
        }
        catch (Exception)
        {
            return fullPath;
        }
    }
}
