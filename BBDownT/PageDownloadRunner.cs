using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using static BBDownT.Core.Entity.Entity;

namespace BBDownT;

// Owns serial scheduling and archive outcomes only. Network, files, credentials,
// selection and per-page retries belong to the injected page operation.
internal sealed class PageDownloadRunner(
    Func<string, bool> isArchived,
    Action<string> archive,
    Func<int, Task> delayMilliseconds,
    Action<string> log)
{
    internal async Task RunAsync(
        List<Page> pages, bool saveArchives, int delaySeconds,
        Func<Page, Task<DownloadPageOutcome>> downloadPage)
    {
        foreach (var page in pages)
        {
            // Preserve the existing wait before every selected page, including
            // the first page and pages subsequently skipped by the archive check.
            if (pages.Count > 1 && delaySeconds > 0)
            {
                log($"停顿{delaySeconds}秒...");
                await delayMilliseconds(delaySeconds * 1000);
            }
            log($"开始解析P{page.index}: {page.aid}... ({pages.IndexOf(page) + 1} of {pages.Count})");

            if (saveArchives && isArchived(page.aid))
            {
                log($"aid: {page.aid}已下载过, 跳过下载...");
                continue;
            }

            var outcome = await downloadPage(page);
            if (!outcome.IsSuccessful())
                throw new InvalidOperationException($"P{page.index} 下载失败");

            if (saveArchives && outcome.ShouldArchive())
                archive(page.aid);
        }

        log("任务完成");
    }
}
