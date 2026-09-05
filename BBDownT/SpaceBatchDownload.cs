using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using BBDownT.Core;

namespace BBDownT;

internal static class SpaceBatchDownload
{
    internal const int MaxDelaySeconds = int.MaxValue / 1000;

    internal static bool IsSpaceUrl(string input)
    {
        if (!Uri.TryCreate(input, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp)
            || !uri.Host.Equals("space.bilibili.com", StringComparison.OrdinalIgnoreCase))
            return false;

        var parts = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length > 0 && parts[0].All(char.IsAsciiDigit)
            && !parts.Skip(1).Any(part => part is "lists" or "favlist" or "channel");
    }

    internal static bool IsExportOnly(MyOption option) =>
        IsSpaceUrl(option.Url) && (!option.DownloadAll || option.OnlyShowInfo);

    internal static string? ValidateOptions(MyOption option)
    {
        if (option.DelayPerVideo < 0 || option.DelayPerVideo > MaxDelaySeconds)
            return $"DelayPerVideo（--delay-per-video）必须是0到{MaxDelaySeconds}之间的整数秒数";
        if (option.DownloadAll && !IsSpaceUrl(option.Url))
            return "DownloadAll（--download-all）仅适用于UP主空间投稿链接";
        return null;
    }

    internal static async Task HandleExportAsync(
        string path,
        MyOption option,
        Func<MyOption, Task> downloadVideo,
        DownloadTask? relatedTask = null,
        Func<int, Task>? delay = null)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("投稿TXT清单尚未生成", path);
        relatedTask?.AddSavePath(path);
        Logger.Log($"投稿链接清单已导出：{path}");
        if (!option.DownloadAll || option.OnlyShowInfo)
        {
            Logger.Log("导出完成；加 --download-all 可按清单逐条下载。");
            return;
        }

        var validationError = ValidateOptions(option);
        if (validationError is not null) throw new ArgumentException(validationError);
        // Read and validate the complete file before any media request. Do not
        // fall back to an in-memory list when export/read/validation fails.
        var urls = ParseUrls(await File.ReadAllLinesAsync(path));
        var workDir = Path.GetFullPath(Environment.CurrentDirectory);
        var wait = delay ?? (milliseconds => Task.Delay(milliseconds));
        var failed = 0;
        for (var index = 0; index < urls.Count; index++)
        {
            if (index > 0 && option.DelayPerVideo > 0)
                await wait(checked(option.DelayPerVideo * 1000));

            Logger.Log($"投稿 {index + 1}/{urls.Count}：{urls[index]}");
            try
            {
                await downloadVideo(option.ForBatchVideo(urls[index], workDir));
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception error)
            {
                failed++;
                Logger.LogError($"投稿下载失败 {urls[index]}：{(Config.DEBUG_LOG ? error.ToString() : error.Message)}");
            }
        }

        Logger.Log($"批量处理完成：成功 {urls.Count - failed}，失败 {failed}");
        if (failed > 0)
            throw new InvalidOperationException($"投稿批量处理完成，其中 {failed}/{urls.Count} 项失败；详情见前面的失败记录");
    }

    internal static List<string> ParseUrls(IEnumerable<string> lines)
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var lineNumber = 0;
        foreach (var line in lines)
        {
            lineNumber++;
            var url = line.Trim();
            if (url.Length == 0) continue;
            const string prefix = "/video/av";
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
                || uri.Scheme != Uri.UriSchemeHttps
                || !uri.Host.Equals("www.bilibili.com", StringComparison.OrdinalIgnoreCase)
                || !uri.AbsolutePath.StartsWith(prefix, StringComparison.Ordinal)
                || uri.AbsolutePath.Length == prefix.Length
                || !uri.AbsolutePath[prefix.Length..].All(char.IsAsciiDigit)
                || uri.Query.Length > 0 || uri.Fragment.Length > 0 || uri.UserInfo.Length > 0
                || !uri.IsDefaultPort)
                throw new InvalidDataException($"投稿TXT清单第 {lineNumber} 行不是有效的导出视频链接");
            if (seen.Add(url)) result.Add(url);
        }
        return result;
    }
}
