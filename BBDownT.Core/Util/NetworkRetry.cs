using System.Net;
using System.Net.Sockets;
using System.Security.Authentication;

namespace BBDownT.Core.Util;

internal static class NetworkRetry
{
    internal static IReadOnlyList<TimeSpan> RequestDelays { get; } = Array.AsReadOnly(
        new[] { TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(6) });
    internal static IReadOnlyList<TimeSpan> DownloadDelays { get; } = Array.AsReadOnly(
        new[] { TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(8),
            TimeSpan.FromSeconds(16), TimeSpan.FromSeconds(32) });
    private static readonly TimeSpan MaximumRetryAfter = TimeSpan.FromSeconds(60);

    internal static bool IsTransient(Exception error, CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested) return false;
        var causes = Causes(error).ToArray();
        if (causes.Any(cause => cause is AuthenticationException
            || cause is HttpRequestException { HttpRequestError: HttpRequestError.SecureConnectionError
                or HttpRequestError.UserAuthenticationError }
            || cause is HttpIOException { HttpRequestError: HttpRequestError.SecureConnectionError
                or HttpRequestError.UserAuthenticationError })) return false;
        if (causes.Any(cause => cause is OperationCanceledException))
            return causes.Any(cause => cause is TimeoutException)
                && (error is OperationCanceledException || causes.Any(cause => cause is DownloadInterruptedException));
        // A status response is authoritative, even if an unrelated inner error looks transient.
        var statusError = causes.OfType<HttpRequestException>().FirstOrDefault(cause => cause.StatusCode.HasValue);
        if (statusError is not null) return IsTransientStatus(statusError.StatusCode!.Value);
        if (causes.Any(cause => cause is DownloadInterruptedException)) return true;
        if (causes.OfType<SocketException>().Any(cause => IsTransientSocket(cause.SocketErrorCode))) return true;
        if (causes.OfType<HttpIOException>().Any(cause => IsTransientTransport(cause.HttpRequestError))) return true;
        // Older handlers can report only HttpRequestException, without an error
        // code or inner cause. Keep a bounded transport fallback for that shape;
        // a supplied local IO, status, protocol or TLS cause remains authoritative.
        return causes.OfType<HttpRequestException>().Any(cause =>
            IsTransientTransport(cause.HttpRequestError)
            || (cause.HttpRequestError == HttpRequestError.Unknown
                && (cause.InnerException is null || causes.Any(inner => inner is TimeoutException))));
    }

    internal static async Task<T> ExecuteAsync<T>(
        Func<CancellationToken, Task<T>> action,
        IReadOnlyList<TimeSpan> delays,
        string operation,
        CancellationToken cancellationToken = default,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        Action<string>? log = null)
    {
        delay ??= Task.Delay;
        log ??= message => Logger.LogWarn(message);
        for (var attempt = 0; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return await action(cancellationToken);
            }
            catch (Exception error) when (attempt < delays.Count && IsTransient(error, cancellationToken))
            {
                var wait = delays[attempt];
                if (error is RetryableHttpStatusException { RetryAfter: { } retryAfter })
                {
                    retryAfter = retryAfter < TimeSpan.Zero ? TimeSpan.Zero
                        : retryAfter > MaximumRetryAfter ? MaximumRetryAfter : retryAfter;
                    if (retryAfter > wait) wait = retryAfter;
                }
                log($"{operation}遇到临时网络错误，重试 {attempt + 1}/{delays.Count}，"
                    + $"等待 {wait.TotalSeconds:0.###} 秒：{ErrorText.Describe(error)}");
                await delay(wait, cancellationToken);
            }
        }
    }

    internal static Task ExecuteAsync(
        Func<CancellationToken, Task> action,
        IReadOnlyList<TimeSpan> delays,
        string operation,
        CancellationToken cancellationToken = default,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        Action<string>? log = null)
        => ExecuteAsync(async token => { await action(token); return true; },
            delays, operation, cancellationToken, delay, log);

    internal static HttpResponseMessage EnsureSuccessStatusCode(HttpResponseMessage response)
    {
        if (!response.IsSuccessStatusCode && IsTransientStatus(response.StatusCode))
        {
            var retryAfter = response.Headers.RetryAfter;
            var wait = retryAfter?.Delta;
            if (wait is null && retryAfter?.Date is { } date) wait = date - DateTimeOffset.UtcNow;
            throw new RetryableHttpStatusException(response.StatusCode, wait);
        }
        return response.EnsureSuccessStatusCode();
    }

    private static bool IsTransientStatus(HttpStatusCode statusCode) => (int)statusCode is 408 or 429 or 500 or 502 or 503 or 504;

    private static bool IsTransientTransport(HttpRequestError error) => error is
        HttpRequestError.NameResolutionError or HttpRequestError.ConnectionError or HttpRequestError.ResponseEnded;

    private static bool IsTransientSocket(SocketError error) => error is
        SocketError.HostNotFound or SocketError.TryAgain or SocketError.NoData or SocketError.NetworkDown
        or SocketError.NetworkUnreachable or SocketError.HostDown or SocketError.HostUnreachable
        or SocketError.ConnectionAborted or SocketError.ConnectionReset or SocketError.ConnectionRefused
        or SocketError.TimedOut;

    private static IEnumerable<Exception> Causes(Exception error)
    {
        for (Exception? cause = error; cause is not null; cause = cause.InnerException) yield return cause;
    }
}

internal sealed class DownloadInterruptedException(string message, Exception? inner = null) : IOException(message, inner);

internal sealed class RetryableHttpStatusException(HttpStatusCode statusCode, TimeSpan? retryAfter = null)
    : HttpRequestException($"HTTP {(int)statusCode}", null, statusCode)
{
    internal TimeSpan? RetryAfter { get; } = retryAfter;
}
