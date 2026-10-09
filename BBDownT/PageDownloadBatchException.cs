using System;
using System.Collections.Generic;
using System.Linq;
using BBDownT.Core.Util;

namespace BBDownT;

internal sealed class PageDownloadBatchException : AggregateException
{
    internal IReadOnlyList<int> FailedPages { get; }

    internal int SkippedPages { get; }

    internal PageDownloadBatchException(IReadOnlyList<(int PageIndex, Exception Error)> failures, int skippedPages = 0)
        : base($"本批次有 {failures.Count} 个分P下载失败：{string.Join("、", failures.Select(failure => $"P{failure.PageIndex}"))}"
            + (skippedPages > 0 ? $"；因连续网络错误，剩余 {skippedPages} 个分P未处理" : ""),
            failures.Select(failure => new InvalidOperationException(
                $"P{failure.PageIndex}：{ErrorText.Describe(failure.Error)}", failure.Error)))
    {
        FailedPages = Array.AsReadOnly(failures.Select(failure => failure.PageIndex).ToArray());
        SkippedPages = skippedPages;
    }
}
