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
using System.Security.Cryptography;
using System.Threading;
using System.Runtime.ExceptionServices;
using BBDownT.Core.Util;

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
        internal string? ResourceIdentity { get; set; }
        internal Func<TimeSpan, CancellationToken, Task>? RetryDelay { get; set; }
        internal CancellationToken CancellationToken { get; set; }
        internal int? MaxParallelDownloads { get; set; }
    }

    internal static async Task RangeDownloadToTmpAsync(
        int id, string url, string tmpName, long fromPosition, long? toPosition,
        Action<int, long, long> onProgress, bool failOnRangeNotSupported = false,
        HttpClient? httpClient = null, string? restrictedOutputRoot = null,
        string? resourceIdentity = null, DownloadResourceMetadata? expectedResource = null,
        Func<TimeSpan, CancellationToken, Task>? retryDelay = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        tmpName = OutputPathPolicy.ResolveArtifact(tmpName, restrictedOutputRoot);
        var validatorPath = OutputPathPolicy.ResolveArtifact(tmpName + ".resume", restrictedOutputRoot);
        var identity = DownloadResumeState.Scope(url, resourceIdentity);
        var sourceHash = DownloadResumeState.SourceHash(url);
        var state = await DownloadResumeState.LoadAsync(validatorPath);
        var legacyValidator = state is null ? await DownloadResumeValidator.LoadAsync(validatorPath) : null;
        await using var local = new FileStream(tmpName, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None,
            262144, FileOptions.Asynchronous);
        var existing = local.Length;
        var localMatches = state is not null && state.MatchesRange(identity, fromPosition, toPosition)
            && await state.MatchesStreamAsync(local);
        var sameSource = localMatches && state!.SourceUriHash == sourceHash;
        var fullCheckpoint = localMatches && existing == state!.LocalLength && existing == state.RangeLength;
        if (fullCheckpoint && sameSource && state!.Validator.IsUsable
            && (state.Complete || toPosition is not null || expectedResource is not null))
        {
            // A complete part still needs remote validation. Multi-thread callers
            // share one header probe; direct callers perform their own probe.
            expectedResource ??= await TryGetResourceMetadataAsync(url, httpClient, cancellationToken, retryDelay);
            if (expectedResource is not null && state.TotalLength == expectedResource.TotalLength
                && state.Validator.Matches(expectedResource.Validator))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!state.Complete) await (state with { Complete = true }).SaveAsync(validatorPath, restrictedOutputRoot);
                onProgress(id, existing, state.TotalLength);
                return;
            }
        }
        var changedVersion = expectedResource is not null && localMatches && sameSource
            && (state!.TotalLength != expectedResource.TotalLength
                || state.Validator.KnownChanged(expectedResource.Validator));
        var invalidState = state is not null && !localMatches;
        var boundedFull = toPosition is not null && (localMatches ? state!.LocalLength : existing) >= toPosition - fromPosition + 1;
        var append = sameSource && !state!.Complete && !boundedFull && !changedVersion
            && state.Validator.IsUsable && (expectedResource is null || state.Validator.Matches(expectedResource.Validator));
        var comparePrefix = existing > 0 && !append && !invalidState && !changedVersion;
        if (append && existing > state!.LocalLength) comparePrefix = true;
        if (comparePrefix) Log("校验已有分片数据...");
        var retainedLength = append ? state!.LocalLength : 0;
        var requestedFrom = append ? checked(fromPosition + retainedLength) : fromPosition;
        var international = BBDownT.Core.Config.COOKIE_IS_INTL;
        using var request = MediaRequestPolicy.CreateRequest(url, international, requestedFrom, toPosition);
        var requestValidator = expectedResource?.Validator ?? (sameSource ? state!.Validator : legacyValidator);
        if (requestValidator is { IsUsable: true }) requestValidator.Apply(request);
        var client = httpClient ?? GetMediaHttpClient(international);
        using var response = await SendMediaRequestAsync(client, request, cancellationToken);
        if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
        {
            var length = response.Content.Headers.ContentRange?.Length;
            if (append && toPosition is null && length == requestedFrom && state!.TotalLength == length
                && state.Validator.Matches(response))
            {
                local.SetLength(retainedLength);
                await local.FlushAsync();
                await (state with { Complete = true }).SaveAsync(validatorPath, restrictedOutputRoot);
                onProgress(id, retainedLength, length.Value);
                return;
            }
            throw new IOException("续传范围或远端版本已变化，已保留本地数据，请重新解析后重试");
        }
        if (response.StatusCode == HttpStatusCode.PreconditionFailed)
            throw new IOException("续传校验条件已变化，已保留本地数据，请重新解析后重试");
        NetworkRetry.EnsureSuccessStatusCode(response);
        var remoteValidator = DownloadResumeValidator.FromResponse(response);
        var responseLength = response.Content.Headers.ContentLength;
        long totalLength;
        if (response.StatusCode == HttpStatusCode.PartialContent)
        {
            responseLength = ValidatePartialContentRange(response.Content.Headers.ContentRange,
                responseLength, requestedFrom, toPosition);
            totalLength = response.Content.Headers.ContentRange!.Length ?? 0;
            if (totalLength <= 0) throw new InvalidDataException("分片响应未提供有效的总长度");
            if (append && !state!.Validator.Matches(response))
            {
                if (state.Validator.KnownChanged(remoteValidator))
                    throw new InvalidDataException("续传响应的远端实体校验器已变化，已保留本地数据");
                // A stronger validator appearing is not evidence that the old
                // bytes match it. Re-read the complete local prefix first.
                response.Dispose();
                await local.DisposeAsync();
                await RangeDownloadToTmpAsync(id, url, tmpName, fromPosition, toPosition, onProgress,
                    failOnRangeNotSupported, httpClient, restrictedOutputRoot, resourceIdentity,
                    new(totalLength, remoteValidator), retryDelay, cancellationToken);
                return;
            }
        }
        else if (response.StatusCode == HttpStatusCode.OK)
        {
            if (failOnRangeNotSupported && (requestedFrom > 0 || toPosition is not null))
                throw new NotSupportedException("Range request is not supported.");
            if (fromPosition != 0) throw new NotSupportedException("Range request is not supported.");
            totalLength = responseLength ?? 0;
            // If-Range yielding 200 replaces the old representation rather than
            // appending a whole object at the old offset.
            if (append) { append = false; comparePrefix = false; }
        }
        else throw new InvalidDataException($"不支持的下载响应状态: {(int)response.StatusCode}");
        if (expectedResource is not null)
        {
            if (totalLength != expectedResource.TotalLength)
                throw new InvalidDataException("分片响应与本次资源探测的长度不一致");
            if (expectedResource.Validator.IsUsable && !expectedResource.Validator.Matches(response))
            {
                if (string.IsNullOrEmpty(expectedResource.Validator.EntityTag) && !string.IsNullOrEmpty(remoteValidator.EntityTag))
                {
                    response.Dispose();
                    await local.DisposeAsync();
                    await RangeDownloadToTmpAsync(id, url, tmpName, fromPosition, toPosition, onProgress,
                        failOnRangeNotSupported, httpClient, restrictedOutputRoot, resourceIdentity,
                        new(totalLength, remoteValidator), retryDelay, cancellationToken);
                    return;
                }
                throw new InvalidDataException("分片响应与本次资源探测的版本不一致");
            }
        }
        // No truncation happens until a successful, correctly ranged response.
        if (!append && !comparePrefix) { local.SetLength(0); existing = 0; }
        using var remote = await ReadRemoteAsync(token => new ValueTask<Stream>(response.Content.ReadAsStreamAsync(token)),
            client.Timeout, cancellationToken);
        await CopyVerifiedRangeAsync(local, remote, append ? retainedLength : 0,
            comparePrefix, responseLength, bytes => onProgress(id, bytes, totalLength > 0 ? totalLength : bytes),
            async (length, hash, complete) =>
            {
                if (totalLength <= 0 && !complete) return;
                await new DownloadResumeState(identity, sourceHash, fromPosition, toPosition,
                    totalLength > 0 ? totalLength : fromPosition + length, complete, length, hash, remoteValidator)
                    .SaveAsync(validatorPath, restrictedOutputRoot);
            }, client.Timeout, cancellationToken);
    }

    // The same streaming state machine is exercised with MemoryStreams in tests.
    // Without a validator (or after a signed URL changes), every retained byte
    // must match the new response before any suffix is appended.
    internal static async Task CopyVerifiedRangeAsync(Stream local, Stream remote,
        long appendLength, bool comparePrefix, long? responseLength, Action<long> progress,
        Func<long, string, bool, Task> checkpoint, TimeSpan? readTimeout = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[262144];
        var comparison = new byte[buffer.Length];
        var originalLength = local.Length;
        var position = appendLength;
        if (appendLength > 0)
        {
            local.Position = 0;
            long remaining = appendLength;
            while (remaining > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var read = await local.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)));
                if (read == 0) throw new IOException("本地续传文件读取不完整");
                hash.AppendData(buffer, 0, read);
                remaining -= read;
            }
        }
        var replaced = !comparePrefix;
        var received = 0L;
        var savedAt = position;
        async Task SaveAsync(bool complete)
        {
            if (!replaced && position < originalLength) return;
            await local.FlushAsync();
            await checkpoint(position, Convert.ToHexString(hash.GetCurrentHash()), complete);
            savedAt = position;
        }
        try
        {
            while (responseLength is null || received < responseLength)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var count = (int)Math.Min(buffer.Length, responseLength is null ? buffer.Length : responseLength.Value - received);
                var read = await ReadRemoteAsync(token => remote.ReadAsync(buffer.AsMemory(0, count), token),
                    readTimeout, cancellationToken);
                if (read == 0) break;
                var retained = !replaced ? (int)Math.Min(read, Math.Max(0, originalLength - position)) : 0;
                if (retained > 0)
                {
                    local.Position = position;
                    await local.ReadExactlyAsync(comparison.AsMemory(0, retained));
                    if (!buffer.AsSpan(0, retained).SequenceEqual(comparison.AsSpan(0, retained)))
                    {
                        local.SetLength(position);
                        retained = 0;
                        replaced = true;
                    }
                }
                if (retained < read)
                {
                    local.Position = position + retained;
                    await local.WriteAsync(buffer.AsMemory(retained, read - retained));
                }
                hash.AppendData(buffer, 0, read);
                position += read;
                received += read;
                progress(position);
                if (position - savedAt >= 4 * 1024 * 1024) await SaveAsync(false);
            }
            if (responseLength is not null && received != responseLength)
                throw new DownloadInterruptedException("下载响应提前结束，已保留已验证的数据",
                    new EndOfStreamException("远端响应未达到声明长度"));
            local.SetLength(position);
            replaced = true;
            await SaveAsync(true);
        }
        catch
        {
            await SaveAsync(false);
            throw;
        }
    }

    private static async Task<T> ReadRemoteAsync<T>(Func<CancellationToken, ValueTask<T>> read,
        TimeSpan? readTimeout, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (readTimeout is { } timeout && timeout != Timeout.InfiniteTimeSpan) deadline.CancelAfter(timeout);
        try { return await read(deadline.Token); }
        catch (OperationCanceledException error) when (!cancellationToken.IsCancellationRequested && deadline.IsCancellationRequested)
        {
            throw new DownloadInterruptedException("远端响应读取超时", new TimeoutException("远端读取超过闲置超时", error));
        }
        catch (IOException error)
        {
            if (cancellationToken.IsCancellationRequested)
                throw new OperationCanceledException("下载已取消", error, cancellationToken);
            if (error is HttpIOException && !NetworkRetry.IsTransient(error, cancellationToken))
                throw;
            throw new DownloadInterruptedException("远端响应读取中断", error);
        }
    }

    private static async Task<HttpResponseMessage> SendMediaRequestAsync(HttpClient client,
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        try { return await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken); }
        catch (OperationCanceledException error) when (NetworkRetry.IsTransient(error, cancellationToken))
        {
            // Keep true HTTP deadlines distinct from caller/peer cancellation
            // even when an outer clip error preserves this cancellation chain.
            throw new DownloadInterruptedException("请求媒体服务器超时", error);
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
        if (contentRange.To < contentRange.From || (contentRange.Length is { } length
            && (length <= 0 || contentRange.To >= length)))
            throw new InvalidDataException("服务器返回的 Content-Range 超出资源范围");

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

    public static async Task DownloadFileAsync(string url, string path, DownloadConfig config,
        HttpClient? httpClient = null, DownloadResourceMetadata? expectedResource = null)
    {
        config.CancellationToken.ThrowIfCancellationRequested();
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
        // Audio and video paths often share a stem; include the extension.
        string tmpName = Path.Combine(desDir, Path.GetFileName(path) + ".tmp");
        DownloadResourceMetadata? metadata = expectedResource;
        if (config.ResourceIdentity is not null)
        {
            metadata ??= await TryGetResourceMetadataAsync(url, httpClient, config.CancellationToken, config.RetryDelay);
            if (await IsCompletedTrackAsync(url, path, config, metadata))
            {
                config.CancellationToken.ThrowIfCancellationRequested();
                return;
            }
            await SeedCompletedTrackAsync(path, tmpName, config);
        }
        using var progress = new ProgressBar(config.RelatedTask);
        await NetworkRetry.ExecuteAsync(token => RangeDownloadToTmpAsync(0, url, tmpName, 0, null,
            (_, downloaded, total) => progress.Report(total > 0 ? (double)downloaded / total : 0, downloaded),
            httpClient: httpClient, restrictedOutputRoot: config.RestrictedOutputRoot,
            resourceIdentity: config.ResourceIdentity, expectedResource: metadata,
            retryDelay: config.RetryDelay, cancellationToken: token), NetworkRetry.DownloadDelays,
            "下载媒体数据", config.CancellationToken, config.RetryDelay, message => LogWarn(message));
        // Publication and local sidecar writes are deliberately outside retry.
        config.CancellationToken.ThrowIfCancellationRequested();
        File.Move(tmpName, path, true);
        if (config.ResourceIdentity is not null)
        {
            var state = await DownloadResumeState.LoadAsync(tmpName + ".resume")
                ?? throw new InvalidDataException("完整轨道缺少续传记录");
            await state.SaveAsync(path + ".resume", config.RestrictedOutputRoot);
        }
        File.Delete(tmpName + ".resume");
    }

    public static async Task<string[]> MultiThreadDownloadFileAsync(string url, string path, DownloadConfig config, HttpClient? httpClient = null)
    {
        config.CancellationToken.ThrowIfCancellationRequested();
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
        DownloadResourceMetadata metadata;
        try
        {
            metadata = await GetResourceMetadataAsync(url, httpClient, cancellationToken: config.CancellationToken,
                retryDelay: config.RetryDelay);
        }
        catch (InvalidDataException ex)
        {
            LogWarn($"{ex.Message}，自动切换为单线程下载");
            await DownloadFileAsync(url, path, new DownloadConfig
            {
                ForceHttp = false,
                RelatedTask = config.RelatedTask,
                RestrictedOutputRoot = config.RestrictedOutputRoot,
                ResourceIdentity = config.ResourceIdentity,
                RetryDelay = config.RetryDelay,
                CancellationToken = config.CancellationToken,
                MaxParallelDownloads = config.MaxParallelDownloads
            }, httpClient);
            DeleteStaleClipFiles(path);
            return [];
        }
        var fileSize = metadata.TotalLength;
        LogDebug("文件大小：{0} bytes", fileSize);
        if (await IsCompletedTrackAsync(url, path, config, metadata))
        {
            config.CancellationToken.ThrowIfCancellationRequested();
            using var cachedProgress = new ProgressBar(config.RelatedTask);
            cachedProgress.Report(1, fileSize);
            return [];
        }
        var trackState = await DownloadResumeState.LoadAsync(OutputPathPolicy.ResolveArtifact(path + ".resume", config.RestrictedOutputRoot));
        if (trackState is { Complete: true } && trackState.MatchesRange(
            DownloadResumeState.Scope(url, config.ResourceIdentity), 0, null)
            && await trackState.MatchesFileAsync(path))
        {
            // A changed URL or missing validator requires full byte comparison,
            // using a scratch copy so a failed replacement preserves the track.
            var temporary = OutputPathPolicy.ResolveArtifact(path + ".verify.tmp", config.RestrictedOutputRoot);
            await SeedCompletedTrackAsync(path, temporary, config);
            using var verifiedProgress = new ProgressBar(config.RelatedTask);
            await NetworkRetry.ExecuteAsync(token => RangeDownloadToTmpAsync(0, url, temporary, 0, null,
                (_, bytes, total) => verifiedProgress.Report((double)bytes / total, bytes),
                httpClient: httpClient, restrictedOutputRoot: config.RestrictedOutputRoot,
                resourceIdentity: config.ResourceIdentity, expectedResource: metadata,
                retryDelay: config.RetryDelay, cancellationToken: token), NetworkRetry.DownloadDelays,
                "核验已下载轨道", config.CancellationToken, config.RetryDelay, message => LogWarn(message));
            config.CancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, path, true);
            var verified = await DownloadResumeState.LoadAsync(temporary + ".resume")
                ?? throw new InvalidDataException("完整轨道缺少续传记录");
            await verified.SaveAsync(path + ".resume", config.RestrictedOutputRoot);
            File.Delete(temporary + ".resume");
            return [];
        }
        List<Clip> allClips = GetAllClips(fileSize);
        var clipPaths = allClips.Select(clip => Path.Combine(Path.GetDirectoryName(path)!,
            clip.index.ToString("00000") + "_" + Path.GetFileNameWithoutExtension(path)
            + (Path.GetExtension(path).Equals(".mp4", StringComparison.OrdinalIgnoreCase) ? ".vclip" : ".aclip")))
            .ToArray();
        if (allClips.Count > 1 && !metadata.Validator.IsUsable)
        {
            // Multiple independent responses without a version validator cannot
            // establish a common snapshot. Compare the existing contiguous
            // prefix against one full response instead of mixing generations.
            Log("服务器未提供资源版本校验器，使用单个响应核验已有数据并续传...");
            await SeedClipPrefixAsync(clipPaths, allClips, path + ".tmp", config.RestrictedOutputRoot);
            await DownloadFileAsync(url, path, config, httpClient, metadata);
            DeleteStaleClipFiles(path);
            return [];
        }
        int total = allClips.Count;
        LogDebug("分段数量：{0}", total);
        ConcurrentDictionary<int, long> clipProgress = new();
        var identity = DownloadResumeState.Scope(url, config.ResourceIdentity);
        var sourceHash = DownloadResumeState.SourceHash(url);
        foreach (var clip in allClips)
        {
            config.CancellationToken.ThrowIfCancellationRequested();
            clipProgress[clip.index] = 0;
            var saved = await DownloadResumeState.LoadAsync(OutputPathPolicy.ResolveArtifact(
                clipPaths[clip.index] + ".resume", config.RestrictedOutputRoot));
            if (saved is not null && saved.SourceUriHash == sourceHash
                && saved.MatchesRange(identity, clip.from, clip.to, fileSize)
                && saved.Validator.IsUsable && saved.Validator.Matches(metadata.Validator)
                && await saved.MatchesFileAsync(clipPaths[clip.index]))
                clipProgress[clip.index] = saved.LocalLength;
        }

        using var progress = new ProgressBar(config.RelatedTask);
        progress.Report((double)clipProgress.Values.Sum() / fileSize, clipProgress.Values.Sum());
        using var batch = CancellationTokenSource.CreateLinkedTokenSource(config.CancellationToken);
        IOException? firstFailure = null;
        var parallelOptions = new ParallelOptions { CancellationToken = batch.Token };
        if (config.MaxParallelDownloads is { } maximum) parallelOptions.MaxDegreeOfParallelism = maximum;
        try
        {
            await Parallel.ForEachAsync(allClips, parallelOptions, async (clip, token) =>
            {
                try
                {
                    await NetworkRetry.ExecuteAsync(attemptToken => RangeDownloadToTmpAsync(clip.index, url,
                        clipPaths[clip.index], clip.from, clip.to == -1 ? null : clip.to, (index, downloaded, _) =>
                        {
                            clipProgress[index] = downloaded;
                            progress.Report((double)clipProgress.Values.Sum() / fileSize, clipProgress.Values.Sum());
                        }, true, httpClient, config.RestrictedOutputRoot, config.ResourceIdentity, metadata,
                        config.RetryDelay, attemptToken), NetworkRetry.DownloadDelays, $"下载分片 {clip.index}",
                        token, config.RetryDelay, message => LogWarn(message));
                }
                catch (OperationCanceledException) when (batch.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception error)
                {
                    var message = error is NotSupportedException
                        ? "服务器可能并不支持多线程下载，请使用 --multi-thread false 关闭多线程"
                        : $"分片 {clip.index} 下载失败：{NetworkRetry.Describe(error)}";
                    var failure = new IOException(message, error);
                    if (Interlocked.CompareExchange(ref firstFailure, failure, null) is null) batch.Cancel();
                    throw failure;
                }
            });
        }
        catch
        {
            // ForEachAsync has awaited every worker, including uncancelled
            // checkpoint writes. A peer's cancellation cannot hide the cause.
            if (firstFailure is not null) ExceptionDispatchInfo.Capture(firstFailure).Throw();
            config.CancellationToken.ThrowIfCancellationRequested();
            throw;
        }
        return clipPaths;
    }

    internal static void MergeTrackClips(string[] files, string destination)
        => MergeTrackClipsAsync(files, destination).GetAwaiter().GetResult();

    internal static async Task MergeTrackClipsAsync(string[] files, string destination, DownloadConfig? config = null)
    {
        if (files.Length == 0) return;
        destination = OutputPathPolicy.ResolveArtifact(destination, config?.RestrictedOutputRoot);
        OutputPathPolicy.ResolveArtifact(destination + ".resume", config?.RestrictedOutputRoot);
        var states = new List<DownloadResumeState>();
        foreach (var file in files)
        {
            OutputPathPolicy.ResolveArtifact(file, config?.RestrictedOutputRoot);
            var state = await DownloadResumeState.LoadAsync(OutputPathPolicy.ResolveArtifact(
                file + ".resume", config?.RestrictedOutputRoot));
            if (state is null || !state.Complete || !await state.MatchesFileAsync(file))
            {
                if (config?.ResourceIdentity is not null) throw new InvalidDataException("分片缺少完整且有效的续传记录");
                states.Clear();
                break;
            }
            states.Add(state);
        }
        if (states.Count > 0)
        {
            long next = 0;
            var first = states[0];
            foreach (var state in states)
            {
                if (state.FromPosition != next || state.ResourceIdentity != first.ResourceIdentity
                    || state.SourceUriHash != first.SourceUriHash || state.TotalLength != first.TotalLength
                    || state.Validator != first.Validator
                    || (config?.ResourceIdentity is not null && state.ResourceIdentity != config.ResourceIdentity))
                    throw new InvalidDataException("分片不属于同一个轨道、范围或资源版本");
                next += state.LocalLength;
            }
            if (next != first.TotalLength) throw new InvalidDataException("完整分片未覆盖整个轨道");
        }
        BBDownTUtil.CombineMultipleFilesIntoSingleFile(files, destination);
        if (states.Count > 0)
        {
            await using var stream = File.OpenRead(destination);
            var first = states[0];
            var completed = first with
            {
                FromPosition = 0, ToPosition = null, Complete = true, LocalLength = stream.Length,
                LocalSha256 = Convert.ToHexString(await SHA256.HashDataAsync(stream))
            };
            await completed.SaveAsync(destination + ".resume", config?.RestrictedOutputRoot);
        }
        foreach (var file in files)
        {
            MediaOutput.DeleteInput(file, destination);
        }
    }

    private static async Task<bool> IsCompletedTrackAsync(string url, string path, DownloadConfig config,
        DownloadResourceMetadata? metadata)
    {
        if (metadata is null || !metadata.Validator.IsUsable) return false;
        var state = await DownloadResumeState.LoadAsync(OutputPathPolicy.ResolveArtifact(path + ".resume", config.RestrictedOutputRoot));
        return state is { Complete: true } && state.SourceUriHash == DownloadResumeState.SourceHash(url)
            && state.MatchesRange(DownloadResumeState.Scope(url, config.ResourceIdentity), 0, null, metadata.TotalLength)
            && state.Validator.Matches(metadata.Validator) && await state.MatchesFileAsync(path);
    }

    private static async Task SeedCompletedTrackAsync(string path, string temporary, DownloadConfig config)
    {
        temporary = OutputPathPolicy.ResolveArtifact(temporary, config.RestrictedOutputRoot);
        if (File.Exists(temporary)) return;
        var state = await DownloadResumeState.LoadAsync(OutputPathPolicy.ResolveArtifact(path + ".resume", config.RestrictedOutputRoot));
        if (state is not { Complete: true } || (config.ResourceIdentity is not null && state.ResourceIdentity != config.ResourceIdentity)
            || !await state.MatchesFileAsync(path)) return;
        OutputPathPolicy.ResolveArtifact(temporary + ".resume", config.RestrictedOutputRoot);
        File.Copy(path, temporary);
        await state.SaveAsync(temporary + ".resume", config.RestrictedOutputRoot);
    }

    private static async Task SeedClipPrefixAsync(string[] paths, List<Clip> clips, string temporary, string? restrictedOutputRoot)
    {
        temporary = OutputPathPolicy.ResolveArtifact(temporary, restrictedOutputRoot);
        if (File.Exists(temporary)) return;
        await using var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            262144, FileOptions.Asynchronous);
        for (var index = 0; index < paths.Length; index++)
        {
            var path = OutputPathPolicy.ResolveArtifact(paths[index], restrictedOutputRoot);
            if (!File.Exists(path)) break;
            var expected = clips[index].to - clips[index].from + 1;
            var length = new FileInfo(path).Length;
            if (length <= 0 || length > expected) break;
            await using var input = File.OpenRead(path);
            await input.CopyToAsync(output);
            if (length < expected) break;
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
        bool? international = null, Func<TimeSpan, CancellationToken, Task>? retryDelay = null,
        CancellationToken cancellationToken = default)
        => (await GetResourceMetadataAsync(url, httpClient, international, retryDelay, cancellationToken)).TotalLength;

    internal static async Task<DownloadResourceMetadata> GetResourceMetadataAsync(string url,
        HttpClient? httpClient = null, bool? international = null,
        Func<TimeSpan, CancellationToken, Task>? retryDelay = null, CancellationToken cancellationToken = default)
        => await NetworkRetry.ExecuteAsync(token => GetResourceMetadataOnceAsync(url, httpClient, international, token),
            NetworkRetry.RequestDelays, "探测媒体资源", cancellationToken, retryDelay, message => LogWarn(message));

    private static async Task<DownloadResourceMetadata> GetResourceMetadataOnceAsync(string url,
        HttpClient? httpClient, bool? international, CancellationToken cancellationToken)
    {
        var intl = international ?? BBDownT.Core.Config.COOKIE_IS_INTL;
        using var httpRequestMessage = MediaRequestPolicy.CreateRequest(url, intl);
        using var response = await SendMediaRequestAsync(httpClient ?? GetMediaHttpClient(intl),
            httpRequestMessage, cancellationToken);
        NetworkRetry.EnsureSuccessStatusCode(response);
        var size = GetTotalFileSize(
            response.StatusCode,
            response.Content.Headers.ContentLength,
            response.Content.Headers.ContentRange);
        return new DownloadResourceMetadata(size, DownloadResumeValidator.FromResponse(response));
    }

    private static async Task<DownloadResourceMetadata?> TryGetResourceMetadataAsync(string url,
        HttpClient? httpClient = null, CancellationToken cancellationToken = default,
        Func<TimeSpan, CancellationToken, Task>? retryDelay = null)
    {
        try { return await GetResourceMetadataAsync(url, httpClient, retryDelay: retryDelay, cancellationToken: cancellationToken); }
        catch (InvalidDataException) { return null; }
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
