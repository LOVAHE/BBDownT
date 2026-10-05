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
using BBDownT.Core.Util;
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
        internal string? RestrictedOutputRoot { get; set; }
    }

    /// <summary>
    /// 内置下载器的重试预算与 aria2 保持一致(--max-tries=5 --retry-wait=3),
    /// 等待时间在此基础上指数增长并封顶, 以便扛过分钟级的网络抖动。
    /// </summary>
    internal const int ClipMaxAttempts = 5;
    internal const int SingleFileMaxAttempts = 5;

    private static readonly Func<int, Task> DefaultRetryDelay = milliseconds => Task.Delay(milliseconds);

    internal static async Task RangeDownloadToTmpAsync(
        int id,
        string url,
        string tmpName,
        long fromPosition,
        long? toPosition,
        Action<int, long, long> onProgress,
        bool failOnRangeNotSupported = false,
        HttpClient? httpClient = null,
        string? restrictedOutputRoot = null)
    {
        tmpName = OutputPathPolicy.ResolveArtifact(tmpName, restrictedOutputRoot);
        var validatorPath = tmpName + ".resume";
        OutputPathPolicy.ResolveArtifact(validatorPath, restrictedOutputRoot);
        // clipLength > 0 表示有界分片: 完成后仍保留校验器, 页面级重试才可能靠探活跳过已下载部分。
        // 单文件下载的 toPosition 为 null, 临时文件完成后会被整体改名, 校验器必须同步删除。
        var clipLength = toPosition is > 0 ? toPosition.Value - fromPosition + 1 : 0;
        var resumeValidator = await DownloadResumeValidator.LoadAsync(validatorPath);
        using var fileStream = new FileStream(tmpName, FileMode.OpenOrCreate);
        fileStream.Seek(0, SeekOrigin.End);
        if (fileStream.Position > 0 && resumeValidator is null)
        {
            fileStream.SetLength(0);
            fileStream.Position = 0;
        }
        if (clipLength > 0 && fileStream.Position == clipLength)
        {
            // 完整分片仍要重新验证远端实体: 同名分片可能是另一档清晰度留下的, 直接拼进成品会损坏文件。
            // 上一段判断已保证此时一定有校验器; 校验不过即清空重下。
            if (resumeValidator is not null && await IsRemoteClipUnchanged(url, fromPosition, resumeValidator, httpClient))
            {
                onProgress(id, clipLength, clipLength);
                return;
            }
            fileStream.SetLength(0);
            fileStream.Position = 0;
            resumeValidator = null;
        }
        var existingLength = fileStream.Position;
        var downloadedBytes = fromPosition + existingLength;

        var international = BBDownT.Core.Config.COOKIE_IS_INTL;
        using var httpRequestMessage = MediaRequestPolicy.CreateRequest(url,
            international, downloadedBytes, toPosition);
        if (existingLength > 0)
        {
            resumeValidator?.Apply(httpRequestMessage);
        }
        using var response = await (httpClient ?? GetMediaHttpClient(international)).SendAsync(httpRequestMessage, HttpCompletionOption.ResponseHeadersRead);
        if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
        {
            var remoteLength = response.Content.Headers.ContentRange?.Length;
            if (existingLength > 0
                && remoteLength == downloadedBytes
                && resumeValidator is not null
                && resumeValidator.Matches(response))
            {
                if (clipLength == 0) File.Delete(validatorPath);
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
            throw new IOException($"下载长度不符: 期望{expectedTempLength}字节, 实际{fileStream.Length}字节");
        if (clipLength == 0) File.Delete(validatorPath);
    }

    /// <summary>
    /// 用单字节探针确认远端实体仍是校验器所指的那一份。服务端不支持条件请求时会返回整份资源(200),
    /// 因此只认 206 + 校验器一致 + Content-Range 起点正确, 其余情况一律判为"已变化"并回到重下路径。
    /// </summary>
    private static async Task<bool> IsRemoteClipUnchanged(
        string url,
        long fromPosition,
        DownloadResumeValidator validator,
        HttpClient? httpClient)
    {
        try
        {
            var international = BBDownT.Core.Config.COOKIE_IS_INTL;
            using var probe = MediaRequestPolicy.CreateRequest(url, international, fromPosition, fromPosition);
            validator.Apply(probe);
            using var response = await (httpClient ?? GetMediaHttpClient(international))
                .SendAsync(probe, HttpCompletionOption.ResponseHeadersRead);
            return response.StatusCode == HttpStatusCode.PartialContent
                && validator.Matches(response)
                && response.Content.Headers.ContentRange?.From == fromPosition;
        }
        catch (Exception ex)
        {
            LogDebug("分片探活失败, 该分片将重新下载: {0}", ex.Message);
            return false;
        }
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
        path = OutputPathPolicy.ResolveArtifact(path, config.RestrictedOutputRoot);
        OutputPathPolicy.ResolveArtifact(path + ".aria2", config.RestrictedOutputRoot);
        if (config.ForceHttp) url = ReplaceUrl(url);
        LogDebug("Start downloading: {0}", url);
        string desDir = Path.GetDirectoryName(path)!;
        if (!string.IsNullOrEmpty(desDir) && !Directory.Exists(desDir)) Directory.CreateDirectory(desDir);
        if (config.UseAria2c)
        {
            await DownloadWithAria2cAsync(url, path, config.Aria2cArgs);
            Console.WriteLine();
            return;
        }
        string tmpName = Path.Combine(desDir, Path.GetFileNameWithoutExtension(path) + ".tmp");
        try
        {
            await NetworkRetry.RunWithDownloadRetryAsync(
                async () =>
                {
                    using var progress = new ProgressBar(config.RelatedTask);
                    await RangeDownloadToTmpAsync(0, url, tmpName, 0, null,
                        (_, downloaded, total) => progress.Report((double)downloaded / total, downloaded),
                        restrictedOutputRoot: config.RestrictedOutputRoot);
                    File.Move(tmpName, path, true);
                },
                DefaultRetryDelay, SingleFileMaxAttempts, $"下载{Path.GetFileName(path)}", message => LogWarn(message));
        }
        catch (Exception ex)
        {
            throw new Exception($"已重试{SingleFileMaxAttempts}次仍失败: {NetworkRetry.DescribeRootCause(ex)}", ex);
        }
    }

    public static async Task<string[]> MultiThreadDownloadFileAsync(string url, string path, DownloadConfig config, HttpClient? httpClient = null, Func<int, Task>? retryDelay = null)
    {
        path = OutputPathPolicy.ResolveArtifact(path, config.RestrictedOutputRoot);
        OutputPathPolicy.ResolveArtifact(path + ".aria2", config.RestrictedOutputRoot);
        if (config.ForceHttp) url = ReplaceUrl(url);
        LogDebug("Start downloading: {0}", url);
        if (config.UseAria2c)
        {
            await DownloadWithAria2cAsync(url, path, config.Aria2cArgs);
            DeleteStaleClipFiles(path);
            Console.WriteLine();
            return [];
        }
        long fileSize;
        try
        {
            fileSize = await GetFileSizeAsync(url, httpClient);
        }
        catch (InvalidDataException ex)
        {
            LogWarn($"{ex.Message}，自动切换为单线程下载");
            await DownloadFileAsync(url, path, new DownloadConfig
            {
                ForceHttp = false,
                RelatedTask = config.RelatedTask,
                RestrictedOutputRoot = config.RestrictedOutputRoot
            });
            DeleteStaleClipFiles(path);
            return [];
        }
        LogDebug("文件大小：{0} bytes", fileSize);
        // A same-size clip can belong to another quality or codec, so a clip without its
        // entity validator is always re-fetched; validated completed clips are probed and kept.
        List<Clip> allClips = GetAllClips(fileSize);
        var clipPaths = allClips.Select(clip => Path.Combine(Path.GetDirectoryName(path)!,
            clip.index.ToString("00000") + "_" + Path.GetFileNameWithoutExtension(path)
            + (Path.GetExtension(path).Equals(".mp4", StringComparison.OrdinalIgnoreCase) ? ".vclip" : ".aclip")))
            .ToArray();
        int total = allClips.Count;
        LogDebug("分段数量：{0}", total);
        ConcurrentDictionary<int, long> clipProgress = new();
        foreach (var i in allClips) clipProgress[i.index] = 0;

        using var progress = new ProgressBar(config.RelatedTask);
        progress.Report(0);
        ConcurrentDictionary<int, Exception> clipFailures = new();
        await Parallel.ForEachAsync(allClips, async (clip, _) =>
        {
            // 已有分片耗尽预算: 不再启动新分片。继续在断链上让每个分片各自空转重试只会把整轮拖死。
            if (!clipFailures.IsEmpty) return;
            string tmp = clipPaths[clip.index];
            try
            {
                await NetworkRetry.RunWithDownloadRetryAsync(
                    () => RangeDownloadToTmpAsync(clip.index, url, tmp, clip.from, clip.to == -1 ? null : clip.to,
                        (index, downloaded, _) =>
                        {
                            clipProgress[index] = downloaded;
                            progress.Report((double)clipProgress.Values.Sum() / fileSize, clipProgress.Values.Sum());
                        }, true, httpClient, config.RestrictedOutputRoot),
                    retryDelay ?? DefaultRetryDelay, ClipMaxAttempts, $"下载分片 {clip.index}", message => LogWarn(message));
            }
            catch (NotSupportedException ex)
            {
                // 服务端忽略 Range: 重试只会得到同样的 200, 直接给出可照做的关闭多线程建议
                clipFailures[clip.index] = new NotSupportedException(
                    "服务器可能并不支持多线程下载, 请使用 --multi-thread false 关闭多线程", ex);
            }
            catch (Exception ex)
            {
                clipFailures[clip.index] = ex;
            }
        });
        if (!clipFailures.IsEmpty)
        {
            // 必须在返回分片清单之前抛出: 缺片的分片清单会让上层继续合并出损坏文件
            var failure = clipFailures.OrderBy(item => item.Key).First();
            if (failure.Value is NotSupportedException) throw failure.Value;
            throw new Exception(
                $"分片 {failure.Key} 下载失败(已重试{ClipMaxAttempts}次): {NetworkRetry.DescribeRootCause(failure.Value)}",
                failure.Value);
        }
        return clipPaths;
    }

    internal static void MergeTrackClips(string[] files, string destination)
    {
        if (files.Length == 0) return;
        BBDownTUtil.CombineMultipleFilesIntoSingleFile(files, destination);
        foreach (var file in files)
        {
            MediaOutput.DeleteInput(file, destination);
            File.Delete(file + ".resume");
        }
    }

    private static async Task DownloadWithAria2cAsync(string url, string path, string extraArgs)
    {
        var exitCode = await BBDownTAria2c.DownloadFileByAria2cAsync(url, path, extraArgs);
        EnsureAria2cDownloadSucceeded(exitCode, path);
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
    internal static List<Clip> GetAllClips(long fileSize)
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

    internal static async Task<long> GetFileSizeAsync(string url, HttpClient? httpClient = null,
        bool? international = null)
    {
        var intl = international ?? BBDownT.Core.Config.COOKIE_IS_INTL;
        using var httpRequestMessage = MediaRequestPolicy.CreateRequest(url, intl);
        using var response = (await (httpClient ?? GetMediaHttpClient(intl)).SendAsync(httpRequestMessage, HttpCompletionOption.ResponseHeadersRead)).EnsureSuccessStatusCode();
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
