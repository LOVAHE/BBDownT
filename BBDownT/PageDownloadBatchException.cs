using System;
using System.Collections.Generic;
using System.Linq;
using BBDownT.Core.Util;

namespace BBDownT;

internal sealed class PageDownloadBatchException : AggregateException
{
    internal IReadOnlyList<int> FailedPages { get; }

    internal PageDownloadBatchException(IReadOnlyList<(int PageIndex, Exception Error)> failures)
        : base($"本批次有 {failures.Count} 个分P下载失败：{string.Join("、", failures.Select(failure => $"P{failure.PageIndex}"))}",
            failures.Select(failure => new InvalidOperationException(
                $"P{failure.PageIndex}：{NetworkRetry.Describe(failure.Error)}", failure.Error)))
    {
        FailedPages = Array.AsReadOnly(failures.Select(failure => failure.PageIndex).ToArray());
    }
}
