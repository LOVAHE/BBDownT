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
    internal async Task RunAsync(
        List<Page> pages, bool saveArchives, int delaySeconds,
        Func<Page, Task<DownloadPageOutcome>> downloadPage, IReadOnlyCollection<Page>? allPages = null,
        CancellationToken cancellationToken = default)
    {
        // A legacy AID cannot prove completion of every page. Only the original
        // unfiltered list can confirm that an AID still represents one page.
        var legacySinglePageAids = allPages?.GroupBy(page => page.DownloadId)
            .Where(group => group.Count() == 1).Select(group => group.Key).ToHashSet()
            ?? new HashSet<string>();
        var failures = new List<(int PageIndex, Exception Error)>();
        foreach (var page in pages)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Preserve the existing wait before every selected page, including
            // the first page and pages subsequently skipped by the archive check.
            if (pages.Count > 1 && delaySeconds > 0)
            {
                log($"停顿{delaySeconds}秒...");
                await delayMilliseconds(delaySeconds * 1000, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
            }
            log($"开始解析P{page.index}: {page.DownloadId}... ({pages.IndexOf(page) + 1} of {pages.Count})");

            var archiveKey = $"{page.DownloadId}:{page.cid}";
            if (saveArchives && (isArchived(archiveKey)
                || (legacySinglePageAids.Contains(page.DownloadId) && isArchived(page.DownloadId))))
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
                log($"P{page.index} 下载失败：{NetworkRetry.Describe(error)}；继续处理其余分P。");
                continue;
            }

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
            if (cause is OutOfMemoryException or AccessViolationException
                || (cause is OperationCanceledException && !transient)) return true;
        return false;
    }
}
