using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net;
using System.Threading.Tasks;
using static BBDownT.Core.Entity.Entity;
using static BBDownT.Core.Logger;
using static BBDownT.Core.Util.HTTPUtil;
using System.Collections.Concurrent;

namespace BBDownT;

internal static class BBDownTDownloadUtil
{
    public class DownloadConfig
    {
        public bool UseAria2c { get; set; } = false;
        public string Aria2cArgs { get; set; } = string.Empty;
        public bool ForceHttp { get; set; } = false;
        public bool MultiThread { get; set; } = false;
        public DownloadTask? RelatedTask { get; set; } = null;
    }

    internal static async Task RangeDownloadToTmpAsync(
        int id,
        string url,
        string tmpName,
        long fromPosition,
        long? toPosition,
        Action<int, long, long> onProgress,
        bool failOnRangeNotSupported = false,
        HttpClient? httpClient = null)
    {
        var validatorPath = tmpName + ".resume";
        var resumeValidator = await DownloadResumeValidator.LoadAsync(validatorPath);
        using var fileStream = new FileStream(tmpName, FileMode.OpenOrCreate);
        fileStream.Seek(0, SeekOrigin.End);
        if (fileStream.Position > 0 && resumeValidator is null)
        {
            fileStream.SetLength(0);
            fileStream.Position = 0;
        }
        if (toPosition > 0 && fileStream.Position == toPosition - fromPosition + 1)
        {
            // 完整旧分片仍需重新验证远端实体；从头请求可避免跨版本拼接。
            fileStream.SetLength(0);
            fileStream.Position = 0;
            resumeValidator = null;
        }
        var existingLength = fileStream.Position;
        var downloadedBytes = fromPosition + existingLength;

        using var httpRequestMessage = new HttpRequestMessage();
        if (!url.Contains("platform=android_tv_yst") && !url.Contains("platform=android"))
            httpRequestMessage.Headers.TryAddWithoutValidation("Referer", "https://www.bilibili.com");
        httpRequestMessage.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0");
        TryAddCookieHeader(httpRequestMessage, url);
        httpRequestMessage.Headers.Range = new(downloadedBytes, toPosition);
        if (existingLength > 0)
        {
            resumeValidator?.Apply(httpRequestMessage);
        }
        httpRequestMessage.RequestUri = new(url);

        using var response = await (httpClient ?? AppHttpClient).SendAsync(httpRequestMessage, HttpCompletionOption.ResponseHeadersRead);
        if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
        {
            var remoteLength = response.Content.Headers.ContentRange?.Length;
            if (existingLength > 0
                && remoteLength == downloadedBytes
                && resumeValidator is not null
                && resumeValidator.Matches(response))
            {
                File.Delete(validatorPath);
                onProgress(id, existingLength, downloadedBytes);
                return;
            }

            fileStream.SetLength(0);
            fileStream.Position = 0;
            File.Delete(validatorPath);
            throw new IOException("续传位置不再有效，已清空临时文件以便重试");
        }
        response.EnsureSuccessStatusCode();
        long? responseContentLength = response.Content.Headers.ContentLength;

        if (response.StatusCode == HttpStatusCode.OK) // server doesn't response a partial content
        {
            if (failOnRangeNotSupported && (downloadedBytes > 0 || toPosition != null)) throw new NotSupportedException("Range request is not supported.");
            downloadedBytes = 0;
            existingLength = 0;
            fileStream.SetLength(0);
            fileStream.Position = 0;
        }
        else if (response.StatusCode == HttpStatusCode.PartialContent)
        {
            if (existingLength > 0 && resumeValidator is not null && !resumeValidator.Matches(response))
            {
                throw new InvalidDataException("续传响应的远端实体校验器已变化");
            }
            responseContentLength = ValidatePartialContentRange(
                response.Content.Headers.ContentRange,
                responseContentLength,
                downloadedBytes,
                toPosition);
        }
        else
        {
            throw new InvalidDataException($"不支持的下载响应状态: {(int)response.StatusCode}");
        }

        var responseValidator = DownloadResumeValidator.FromResponse(response);
        if (responseValidator.IsUsable)
        {
            await responseValidator.SaveAsync(validatorPath);
        }

        using var stream = await response.Content.ReadAsStreamAsync();
        var totalBytes = downloadedBytes + (responseContentLength ?? long.MaxValue - downloadedBytes);

        const int blockSize = 1048576 / 4;
        var buffer = new byte[blockSize];

        while (downloadedBytes < totalBytes)
        {
            var recevied = await stream.ReadAsync(buffer);
            if (recevied == 0) break;
            await fileStream.WriteAsync(buffer.AsMemory(0, recevied));
            await fileStream.FlushAsync();
            downloadedBytes += recevied;
            onProgress(id, downloadedBytes - fromPosition, totalBytes);
        }

        var expectedTempLength = GetExpectedTempLength(existingLength, responseContentLength);
        if (expectedTempLength != null && expectedTempLength != fileStream.Length)
            throw new Exception("Retry...");
        File.Delete(validatorPath);
    }

    internal static long? GetExpectedTempLength(long existingLength, long? responseContentLength)
    {
        return responseContentLength is null
            ? null
            : checked(existingLength + responseContentLength.Value);
    }

    internal static long ValidatePartialContentRange(
        ContentRangeHeaderValue? contentRange,
        long? contentLength,
        long requestedFrom,
        long? requestedTo)
    {
        if (contentRange?.From != requestedFrom || contentRange.To is null)
        {
            throw new InvalidDataException("服务器返回的 Content-Range 与请求起点不一致");
        }

        if (requestedTo is not null && contentRange.To != requestedTo)
        {
            throw new InvalidDataException("服务器返回的 Content-Range 未完整覆盖请求范围");
        }

        if (requestedTo is null
            && (contentRange.Length is null || contentRange.To != contentRange.Length - 1))
        {
            throw new InvalidDataException("服务器返回的 Content-Range 未到达资源末尾");
        }

        var declaredRangeLength = contentRange.To.Value - contentRange.From.Value + 1;
        if (contentLength is not null && declaredRangeLength != contentLength)
        {
            throw new InvalidDataException("服务器返回的 Content-Range 与 Content-Length 不一致");
        }

        return declaredRangeLength;
    }

    public static async Task DownloadFileAsync(string url, string path, DownloadConfig config)
    {
        if (string.IsNullOrEmpty(url)) return;
        if (config.ForceHttp) url = ReplaceUrl(url);
        LogDebug("Start downloading: {0}", url);
        string desDir = Path.GetDirectoryName(path)!;
        if (!string.IsNullOrEmpty(desDir) && !Directory.Exists(desDir)) Directory.CreateDirectory(desDir);
        if (config.UseAria2c)
        {
            var exitCode = await BBDownTAria2c.DownloadFileByAria2cAsync(url, path, config.Aria2cArgs);
            EnsureAria2cDownloadSucceeded(exitCode, path);
            Console.WriteLine();
            return;
        }
        int retry = 0;
        string tmpName = Path.Combine(desDir, Path.GetFileNameWithoutExtension(path) + ".tmp");
        reDown:
        try
        {
            using var progress = new ProgressBar(config.RelatedTask);
            await RangeDownloadToTmpAsync(0, url, tmpName, 0, null, (_, downloaded, total) => progress.Report((double)downloaded / total, downloaded));
            File.Move(tmpName, path, true);
        }
        catch (Exception)
        {
            if (++retry == 3) throw;
            goto reDown;
        }
    }

    public static async Task<bool> MultiThreadDownloadFileAsync(string url, string path, DownloadConfig config)
    {
        if (config.ForceHttp) url = ReplaceUrl(url);
        LogDebug("Start downloading: {0}", url);
        if (config.UseAria2c)
        {
            var exitCode = await BBDownTAria2c.DownloadFileByAria2cAsync(url, path, config.Aria2cArgs);
            EnsureAria2cDownloadSucceeded(exitCode, path);
            DeleteStaleClipFiles(path);
            Console.WriteLine();
            return false;
        }
        long fileSize;
        try
        {
            fileSize = await GetFileSizeAsync(url);
        }
        catch (InvalidDataException ex)
        {
            LogWarn($"{ex.Message}，自动切换为单线程下载");
            await DownloadFileAsync(url, path, new DownloadConfig
            {
                ForceHttp = false,
                RelatedTask = config.RelatedTask
            });
            DeleteStaleClipFiles(path);
            return false;
        }
        LogDebug("文件大小：{0} bytes", fileSize);
        //已下载过, 跳过下载
        if (File.Exists(path) && new FileInfo(path).Length == fileSize)
        {
            LogDebug("文件已下载过, 跳过下载");
            DeleteStaleClipFiles(path);
            return false;
        }
        List<Clip> allClips = GetAllClips(url, fileSize);
        int total = allClips.Count;
        LogDebug("分段数量：{0}", total);
        ConcurrentDictionary<int, long> clipProgress = new();
        foreach (var i in allClips) clipProgress[i.index] = 0;

        using var progress = new ProgressBar(config.RelatedTask);
        progress.Report(0);
        await Parallel.ForEachAsync(allClips, async (clip, _) =>
        {
            int retry = 0;
            string tmp = Path.Combine(Path.GetDirectoryName(path)!, clip.index.ToString("00000") + "_" + Path.GetFileNameWithoutExtension(path) + (Path.GetExtension(path).EndsWith(".mp4") ? ".vclip" : ".aclip"));
            reDown:
            try
            {
                await RangeDownloadToTmpAsync(clip.index, url, tmp, clip.from, clip.to == -1 ? null : clip.to, (index, downloaded, _) =>
                {
                    clipProgress[index] = downloaded;
                    progress.Report((double)clipProgress.Values.Sum() / fileSize, clipProgress.Values.Sum());
                }, true);
            }
            catch (NotSupportedException)
            {
                if (++retry == 3) throw new Exception($"服务器可能并不支持多线程下载, 请使用 --multi-thread false 关闭多线程");
                goto reDown;
            }
            catch (Exception)
            {
                if (++retry == 3) throw new Exception($"Failed to download clip {clip.index}");
                goto reDown;
            }
        });
        return true;
    }

    internal static void EnsureAria2cDownloadSucceeded(int exitCode, string path)
    {
        if (exitCode != 0 || File.Exists(path + ".aria2") || !File.Exists(path))
        {
            throw new InvalidOperationException($"aria2下载失败，退出码: {exitCode}");
        }
    }

    internal static int DeleteStaleClipFiles(string destinationPath)
    {
        var directory = Path.GetDirectoryName(destinationPath);
        if (string.IsNullOrEmpty(directory))
        {
            directory = Directory.GetCurrentDirectory();
        }
        if (!Directory.Exists(directory))
        {
            return 0;
        }

        var destinationName = Path.GetFileNameWithoutExtension(destinationPath);
        var clipExtension = Path.GetExtension(destinationPath).EndsWith(".mp4", StringComparison.OrdinalIgnoreCase)
            ? ".vclip"
            : ".aclip";
        var expectedSuffix = $"_{destinationName}{clipExtension}";
        var deletedCount = 0;
        foreach (var candidate in Directory.EnumerateFiles(directory))
        {
            var fileName = Path.GetFileName(candidate);
            var generatedSuffix = fileName.Length >= 5 ? fileName[5..] : string.Empty;
            if (fileName.Length < 5
                || (generatedSuffix != expectedSuffix && generatedSuffix != expectedSuffix + ".resume")
                || !fileName.AsSpan(0, 5).ToString().All(char.IsDigit))
            {
                continue;
            }

            File.Delete(candidate);
            deletedCount++;
        }
        return deletedCount;
    }

    //此函数主要是切片下载逻辑
    internal static List<Clip> GetAllClips(string url, long fileSize)
    {
        List<Clip> clips = [];
        int index = 0;
        long from = 0;
        const int perSize = 20 * 1024 * 1024;
        while (from < fileSize)
        {
            var to = Math.Min(checked(from + perSize - 1), fileSize - 1);
            clips.Add(new Clip
            {
                index = index,
                from = from,
                to = to
            });
            from = checked(to + 1);
            index++;
        }
        return clips;
    }

    private static async Task<long> GetFileSizeAsync(string url)
    {
        using var httpRequestMessage = new HttpRequestMessage();
        if (!url.Contains("platform=android_tv_yst") && !url.Contains("platform=android"))
            httpRequestMessage.Headers.TryAddWithoutValidation("Referer", "https://www.bilibili.com");
        httpRequestMessage.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0");
        TryAddCookieHeader(httpRequestMessage, url);
        httpRequestMessage.RequestUri = new(url);
        using var response = (await AppHttpClient.SendAsync(httpRequestMessage, HttpCompletionOption.ResponseHeadersRead)).EnsureSuccessStatusCode();
        return GetTotalFileSize(
            response.StatusCode,
            response.Content.Headers.ContentLength,
            response.Content.Headers.ContentRange);
    }

    internal static long GetTotalFileSize(
        HttpStatusCode statusCode,
        long? contentLength,
        ContentRangeHeaderValue? contentRange)
    {
        return statusCode switch
        {
            HttpStatusCode.OK => EnsureKnownPositiveFileSize(contentLength),
            HttpStatusCode.PartialContent => EnsureKnownPositiveFileSize(contentRange?.Length),
            _ => throw new InvalidDataException($"不支持的文件大小响应状态: {(int)statusCode}")
        };
    }

    internal static long EnsureKnownPositiveFileSize(long? contentLength)
    {
        if (contentLength is null or <= 0)
        {
            throw new InvalidDataException("服务器未返回有效的 Content-Length，无法进行多线程分段下载");
        }

        return contentLength.Value;
    }

    /// <summary>
    /// 将下载地址强制转换为HTTP
    /// </summary>
    /// <param name="url"></param>
    /// <returns></returns>
    private static string ReplaceUrl(string url)
    {
        if (url.Contains(".mcdn.bilivideo.cn:"))
        {
            LogDebug("对[*.mcdn.bilivideo.cn:xxx]域名不做处理");
            return url;
        }

        LogDebug("将https更改为http");
        return url.Replace("https:", "http:");
    }
}
