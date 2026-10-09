using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BBDownT.Core.Util;
using static BBDownT.Core.Entity.Entity;

namespace BBDownT;

// Owns serial scheduling and archive outcomes only. Network, files, credentials,
// selection and per-page retries belong to the injected page operation.
internal sealed class PageDownloadRunner(
    Func<string, bool> isArchived,
    Action<string> archive,
    Func<int, CancellationToken, Task> delayMilliseconds,
    Action<string> log)
{
    internal const int MaxConsecutiveNetworkFailures = 3;

    internal async Task RunAsync(
        List<Page> pages, bool saveArchives, int delaySeconds,
        Func<Page, Task<DownloadPageOutcome>> downloadPage, IReadOnlyCollection<Page>? allPages = null,
        CancellationToken cancellationToken = default, string archiveVariant = "")
    {
        // A legacy AID cannot prove completion of every page. Only the original
        // unfiltered list can confirm that an AID still represents one page.
        var legacySinglePageAids = allPages?.GroupBy(page => page.DownloadId)
            .Where(group => group.Count() == 1).Select(group => group.Key).ToHashSet()
            ?? new HashSet<string>();
        var failures = new List<(int PageIndex, Exception Error)>();
        var consecutiveNetworkFailures = 0;
        for (var position = 0; position < pages.Count; position++)
        {
            var page = pages[position];
            cancellationToken.ThrowIfCancellationRequested();
            // Preserve the existing wait before every selected page, including
            // the first page and pages subsequently skipped by the archive check.
            if (pages.Count > 1 && delaySeconds > 0)
            {
                log($"停顿{delaySeconds}秒...");
                await delayMilliseconds(delaySeconds * 1000, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
            }
            log($"开始解析P{page.index}: {page.DownloadId}... ({position + 1} of {pages.Count})");

            var archiveKey = $"{page.DownloadId}:{page.cid}{archiveVariant}";
            if (saveArchives && (isArchived(archiveKey)
                || (archiveVariant.Length == 0 && legacySinglePageAids.Contains(page.DownloadId) && isArchived(page.DownloadId))))
            {
                log($"P{page.index}已下载过, 跳过下载...");
                continue;
            }

            DownloadPageOutcome outcome;
            try
            {
                outcome = await downloadPage(page);
                if (!outcome.IsSuccessful())
                    throw new InvalidOperationException("下载未完成");
            }
            catch (Exception error) when (!cancellationToken.IsCancellationRequested
                && !MustStopBatch(error))
            {
                failures.Add((page.index, error));
                consecutiveNetworkFailures = IsNetworkFailure(error) ? consecutiveNetworkFailures + 1 : 0;
                if (consecutiveNetworkFailures >= MaxConsecutiveNetworkFailures && position < pages.Count - 1)
                {
                    log($"P{page.index} 下载失败：{ErrorText.Describe(error)}；连续 {consecutiveNetworkFailures} 个分P因网络错误失败，停止处理剩余分P。");
                    throw new PageDownloadBatchException(failures, pages.Count - position - 1);
                }
                log($"P{page.index} 下载失败：{ErrorText.Describe(error)}；继续处理其余分P。");
                continue;
            }

            consecutiveNetworkFailures = 0;
            if (saveArchives && outcome.ShouldArchive())
                archive(archiveKey);
        }

        if (failures.Count > 0) throw new PageDownloadBatchException(failures);
        log("任务完成");
    }

    private static bool MustStopBatch(Exception error)
    {
        var transient = NetworkRetry.IsTransient(error);
        for (Exception? cause = error; cause is not null; cause = cause.InnerException)
            if (cause is OutOfMemoryException or AccessViolationException or SubtitleUnavailableException
                || (cause is OperationCanceledException && !transient)) return true;
        return false;
    }

    private static bool IsNetworkFailure(Exception error)
    {
        if (NetworkRetry.IsTransient(error)) return true;
        for (Exception? cause = error; cause is not null; cause = cause.InnerException)
            if (cause is BilibiliApiException api) return BilibiliApi.IsRateLimited(api.Code);
        return false;
    }
}
