using System.Net;
using System.Net.Sockets;
using System.Security.Authentication;
using BBDownT.Core.Util;

namespace BBDownT.Tests;

public class NetworkRetryTests
{
    [Fact]
    public void UnclassifiedHttpRequestExceptionKeepsBoundedLegacyTransportFallback()
    {
        Assert.True(NetworkRetry.IsTransient(new HttpRequestException("unclassified transport error")));
        Assert.False(NetworkRetry.IsTransient(new HttpRequestException("local failure", new IOException("disk error"))));
        Assert.False(NetworkRetry.IsTransient(new HttpRequestException(HttpRequestError.InvalidResponse)));
    }

    [Theory]
    [InlineData(408)]
    [InlineData(429)]
    [InlineData(500)]
    [InlineData(502)]
    [InlineData(503)]
    [InlineData(504)]
    public void TemporaryHttpStatus_IsTransient(int status)
        => Assert.True(NetworkRetry.IsTransient(new HttpRequestException("private", null, (HttpStatusCode)status)));

    [Theory]
    [InlineData(400)]
    [InlineData(401)]
    [InlineData(403)]
    [InlineData(404)]
    [InlineData(412)]
    [InlineData(416)]
    [InlineData(501)]
    public void PermanentHttpStatus_IsNotTransient(int status)
        => Assert.False(NetworkRetry.IsTransient(new HttpRequestException("private",
            new SocketException((int)SocketError.ConnectionReset), (HttpStatusCode)status)));

    [Fact]
    public void Classifier_DistinguishesTransportErrorsFromLocalIoAndTls()
    {
        Assert.True(NetworkRetry.IsTransient(new HttpRequestException(HttpRequestError.NameResolutionError)));
        Assert.True(NetworkRetry.IsTransient(new HttpRequestException(HttpRequestError.ConnectionError)));
        Assert.True(NetworkRetry.IsTransient(new HttpIOException(HttpRequestError.ResponseEnded)));
        Assert.True(NetworkRetry.IsTransient(new DownloadInterruptedException("remote ended")));
        Assert.True(NetworkRetry.IsTransient(new IOException("transport",
            new SocketException((int)SocketError.ConnectionReset))));
        Assert.False(NetworkRetry.IsTransient(new IOException("local write failed")));
        Assert.False(NetworkRetry.IsTransient(new UnauthorizedAccessException("local access failed")));
        Assert.False(NetworkRetry.IsTransient(new HttpRequestException(HttpRequestError.SecureConnectionError,
            "private", new SocketException((int)SocketError.ConnectionReset))));
        Assert.False(NetworkRetry.IsTransient(new HttpRequestException("private",
            new AuthenticationException("private", new SocketException((int)SocketError.ConnectionReset)))));
    }

    [Fact]
    public void Cancellation_IsTransientOnlyForExplicitHttpTimeout()
    {
        var timeout = new OperationCanceledException("private", new TimeoutException("private"));
        Assert.True(NetworkRetry.IsTransient(timeout));
        Assert.False(NetworkRetry.IsTransient(new OperationCanceledException("cancelled")));
        using var caller = new CancellationTokenSource();
        caller.Cancel();
        Assert.False(NetworkRetry.IsTransient(timeout, caller.Token));
        Assert.False(NetworkRetry.IsTransient(new OperationCanceledException("private",
            new AuthenticationException("certificate"))));
    }

    [Fact]
    public void DownloadIdleTimeoutMarker_IsTransientButCallerCancellationIsNot()
    {
        var cancellation = new OperationCanceledException("idle read deadline");
        var timeout = new TimeoutException("remote read stalled", cancellation);
        var interruption = new DownloadInterruptedException("remote read timed out", timeout);
        Assert.True(NetworkRetry.IsTransient(interruption));
        Assert.False(NetworkRetry.IsTransient(new IOException("local", timeout)));
        Assert.False(NetworkRetry.IsTransient(new DownloadInterruptedException("ordinary cancellation", cancellation)));
        using var caller = new CancellationTokenSource();
        caller.Cancel();
        Assert.False(NetworkRetry.IsTransient(interruption, caller.Token));
    }

    [Fact]
    public async Task DownloadBudget_RecoversAfterSixtySecondVirtualOutage()
    {
        var elapsed = TimeSpan.Zero;
        var attempts = 0;
        var waits = new List<TimeSpan>();
        var logs = new List<string>();
        var result = await NetworkRetry.ExecuteAsync(_ =>
        {
            attempts++;
            if (elapsed < TimeSpan.FromSeconds(60))
                throw new HttpRequestException(HttpRequestError.NameResolutionError, "secret URL",
                    new SocketException((int)SocketError.HostNotFound));
            return Task.FromResult("recovered");
        }, NetworkRetry.DownloadDelays, "下载", delay: (wait, _) =>
        {
            waits.Add(wait);
            elapsed += wait;
            return Task.CompletedTask;
        }, log: logs.Add);

        Assert.Equal("recovered", result);
        Assert.Equal(6, attempts);
        Assert.Equal(TimeSpan.FromSeconds(62), elapsed);
        Assert.Equal(NetworkRetry.DownloadDelays, waits);
        Assert.Equal(5, logs.Count);
        Assert.Contains("5/5", logs[^1]);
        Assert.Contains("域名解析失败", logs[0]);
        Assert.DoesNotContain("secret URL", string.Join('\n', logs));
    }

    [Fact]
    public async Task Exhaustion_PreservesFinalExceptionAndOriginStack()
    {
        var inner = new SocketException((int)SocketError.ConnectionReset);
        var failure = new HttpRequestException(HttpRequestError.ConnectionError, "private", inner);
        var attempts = 0;
        var actual = await Assert.ThrowsAsync<HttpRequestException>(() => NetworkRetry.ExecuteAsync<int>(_ =>
        {
            attempts++;
            ThrowFromTransport(failure);
            return Task.FromResult(0);
        }, NetworkRetry.RequestDelays, "获取网页", delay: (_, _) => Task.CompletedTask, log: _ => { }));

        Assert.Same(failure, actual);
        Assert.Same(inner, actual.InnerException);
        Assert.Contains(nameof(ThrowFromTransport), actual.StackTrace!);
        Assert.Equal(4, attempts);
    }

    [Fact]
    public async Task CancellationDuringBackoff_StopsBeforeAnotherAttempt()
    {
        using var caller = new CancellationTokenSource();
        var attempts = 0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => NetworkRetry.ExecuteAsync<int>(_ =>
        {
            attempts++;
            throw new HttpRequestException(HttpRequestError.ConnectionError);
        }, NetworkRetry.RequestDelays, "获取网页", caller.Token, delay: (wait, token) =>
        {
            Assert.Equal(caller.Token, token);
            caller.Cancel();
            return Task.Delay(Timeout.InfiniteTimeSpan, token);
        }, log: _ => { }));
        Assert.Equal(1, attempts);
    }

    [Fact]
    public async Task OrdinaryCancellation_IsNotWrappedOrDelayed()
    {
        var failure = new OperationCanceledException("explicit cancellation");
        var actual = await Assert.ThrowsAsync<OperationCanceledException>(() => NetworkRetry.ExecuteAsync<int>(
            _ => throw failure, NetworkRetry.RequestDelays, "获取网页",
            delay: (_, _) => throw new InvalidOperationException("must not delay"), log: _ => { }));
        Assert.Same(failure, actual);
    }

    [Fact]
    public async Task PermanentError_IsNotDelayed()
    {
        var failure = new HttpRequestException("private", null, HttpStatusCode.Unauthorized);
        var actual = await Assert.ThrowsAsync<HttpRequestException>(() => NetworkRetry.ExecuteAsync<int>(
            _ => throw failure, NetworkRetry.RequestDelays, "获取网页",
            delay: (_, _) => throw new InvalidOperationException("must not delay"), log: _ => { }));
        Assert.Same(failure, actual);
    }

    [Theory]
    [InlineData(-1, 1)]
    [InlineData(5, 5)]
    [InlineData(3600, 60)]
    public async Task RetryAfter_IsRespectedAndCapped(int retryAfterSeconds, int expectedWait)
    {
        var attempts = 0;
        var waits = new List<TimeSpan>();
        await NetworkRetry.ExecuteAsync(_ =>
        {
            if (++attempts == 1)
                throw new RetryableHttpStatusException(HttpStatusCode.TooManyRequests,
                    TimeSpan.FromSeconds(retryAfterSeconds));
            return Task.CompletedTask;
        }, NetworkRetry.RequestDelays, "获取网页", delay: (wait, _) =>
        {
            waits.Add(wait);
            return Task.CompletedTask;
        }, log: _ => { });
        Assert.Equal(TimeSpan.FromSeconds(expectedWait), Assert.Single(waits));
    }

    [Fact]
    public void RetrySchedules_CannotBeMutated()
    {
        Assert.Throws<NotSupportedException>(() => ((IList<TimeSpan>)NetworkRetry.RequestDelays)[0] = TimeSpan.Zero);
        Assert.Throws<NotSupportedException>(() => ((IList<TimeSpan>)NetworkRetry.DownloadDelays)[0] = TimeSpan.Zero);
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static void ThrowFromTransport(Exception failure) => throw failure;
}
