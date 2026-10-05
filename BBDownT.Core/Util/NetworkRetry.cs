using System.Net.Sockets;
using static BBDownT.Core.Logger;

namespace BBDownT.Core.Util;

/// <summary>
/// 传输层瞬时故障(断网、DNS 解析失败、连接被重置、超时)的判定、退避与重试。
/// 与 Parser 的播放风控重试不同: 风控看响应内容, 这里看异常类型。
/// </summary>
internal static class NetworkRetry
{
    private const int NetworkMaxAttempts = 3;
    private const int NetworkBaseDelayMilliseconds = 1000;
    private const int NetworkMaxDelayMilliseconds = 3000;

    private const int DownloadBaseDelayMilliseconds = 3000;
    private const int DownloadMaxDelayMilliseconds = 15000;

    /// <summary>
    /// 是否为网络类瞬时错误。沿 InnerException 与 AggregateException 分支向下查找,
    /// 因为上层会把根因包装成普通 Exception(例如分片重试耗尽后的包装)。
    /// </summary>
    internal static bool IsTransientNetworkError(Exception? error)
    {
        if (error is null) return false;
        var pending = new Queue<Exception>();
        pending.Enqueue(error);
        // 环形的 InnerException 理论上不存在, 仍给上限兜底
        for (var seen = 0; pending.Count > 0 && seen < 64; seen++)
        {
            var current = pending.Dequeue();
            if (IsNetworkError(current)) return true;
            if (current.InnerException is not null) pending.Enqueue(current.InnerException);
            if (current is AggregateException aggregate)
            {
                foreach (var inner in aggregate.Flatten().InnerExceptions) pending.Enqueue(inner);
            }
        }
        return false;
    }

    private static bool IsNetworkError(Exception error) => error switch
    {
        TaskCanceledException => true,
        OperationCanceledException => false,
        HttpRequestException or SocketException or TimeoutException => true,
        _ => false
    };

    /// <summary>
    /// 下载重试的终止型错误: 服务端明确不支持 Range(重试只会浪费预算), 或用户真实取消。
    /// 其余异常(HTTP 状态变化、续传位置失效、长度不符)都按可重试处理。
    /// </summary>
    internal static bool IsTerminalDownloadError(Exception error) =>
        error is NotSupportedException || (error is OperationCanceledException and not TaskCanceledException);

    /// <summary>最内层异常: 包装层只会重复外层已经说过的话, 定位问题要看根因。</summary>
    internal static Exception RootCause(Exception error)
    {
        var root = error;
        for (var depth = 0; depth < 16; depth++)
        {
            var next = root switch
            {
                AggregateException { InnerExceptions.Count: > 0 } aggregate => aggregate.Flatten().InnerExceptions[0],
                _ => root.InnerException
            };
            if (next is null || ReferenceEquals(next, root)) break;
            root = next;
        }
        return root;
    }

    /// <summary>最内层异常的类型与消息, 用于把包装过的失败还原成可定位的一行。</summary>
    internal static string DescribeRootCause(Exception error)
    {
        var root = RootCause(error);
        return $"{root.GetType().Name}: {RedactSensitiveText(root.Message)}";
    }

    /// <summary>第 attempt 次失败后的等待毫秒数: 指数增长并封顶, attempt 从 1 起算。</summary>
    internal static int GetBackoffMilliseconds(int attempt, int baseMilliseconds, int maxMilliseconds)
    {
        var grown = attempt <= 1
            ? (long)baseMilliseconds
            : (long)baseMilliseconds << Math.Min(attempt - 1, 20);
        return (int)Math.Min(grown, maxMilliseconds);
    }

    internal static Task RunWithNetworkRetryAsync(
        Func<Task> operation, Func<int, Task> delay, string what, Action<string>? onRetry = null) =>
        RunAsync(
            async () => { await operation(); return (object?)null; },
            delay, NetworkMaxAttempts, NetworkBaseDelayMilliseconds, NetworkMaxDelayMilliseconds,
            IsTransientNetworkError, what, onRetry);

    internal static Task<TResponse> RunWithNetworkRetryAsync<TResponse>(
        Func<Task<TResponse>> operation, Func<int, Task> delay, string what, Action<string>? onRetry = null) =>
        RunAsync(operation, delay, NetworkMaxAttempts, NetworkBaseDelayMilliseconds, NetworkMaxDelayMilliseconds,
            IsTransientNetworkError, what, onRetry);

    internal static Task RunWithDownloadRetryAsync(
        Func<Task> operation, Func<int, Task> delay, int maxAttempts, string what, Action<string>? onRetry = null) =>
        RunAsync(
            async () => { await operation(); return (object?)null; },
            delay, maxAttempts, DownloadBaseDelayMilliseconds, DownloadMaxDelayMilliseconds,
            error => !IsTerminalDownloadError(error), what, onRetry);

    /// <summary>
    /// 预算耗尽或遇到终止型错误时原样抛出, 保留异常类型与堆栈, 由调用方补充业务上下文。
    /// </summary>
    private static async Task<TResponse> RunAsync<TResponse>(
        Func<Task<TResponse>> operation,
        Func<int, Task> delay,
        int maxAttempts,
        int baseDelayMilliseconds,
        int maxDelayMilliseconds,
        Func<Exception, bool> isRetryable,
        string what,
        Action<string>? onRetry)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await operation();
            }
            catch (Exception error) when (attempt < maxAttempts && isRetryable(error))
            {
                onRetry?.Invoke($"{what}(第{attempt}/{maxAttempts}次尝试失败): {DescribeRootCause(error)}");
                await delay(GetBackoffMilliseconds(attempt, baseDelayMilliseconds, maxDelayMilliseconds));
            }
        }
    }
}
