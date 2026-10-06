using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text.Json;
using BBDownT.Core.Entity;
using BBDownT.Core.Util;

namespace BBDownT.Tests;

public class PageDownloadRetryTests
{
    [Fact]
    public void NetworkFailureGetsLongerBudgetThanOrdinaryPageFailure()
    {
        var network = new PageDownloadRetry();
        var socket = new SocketException((int)SocketError.HostNotFound);
        var wrapped = new IOException("clip failure", new HttpRequestException("DNS failed", socket));
        var waits = TakeDelays(network, wrapped);
        var ordinaryWaits = TakeDelays(new PageDownloadRetry(), new InvalidOperationException("page not ready"));

        Assert.True(waits.Count > ordinaryWaits.Count);
        Assert.True(waits.Sum(delay => delay.TotalSeconds) > ordinaryWaits.Sum(delay => delay.TotalSeconds));
        Assert.True(waits.Zip(waits.Skip(1), (first, second) => second > first).All(increases => increases));
    }

    [Fact]
    public void NetworkAndOrdinaryErrorsDoNotConsumeEachOthersBudget()
    {
        var policy = new PageDownloadRetry();
        var ordinary = new InvalidOperationException("page not ready");
        Assert.Equal(2, TakeDelays(policy, ordinary).Count);
        Assert.NotEmpty(TakeDelays(policy, new HttpRequestException("connection failed")));
        Assert.False(policy.TryGetDelay(ordinary, out _));
    }

    [Fact]
    public void PermanentFailuresAndCancellationDoNotRetry()
    {
        Exception[] permanent =
        [
            new HttpRequestException("unauthorized", null, HttpStatusCode.Unauthorized),
            new HttpRequestException("bad request", null, HttpStatusCode.BadRequest),
            new UnauthorizedAccessException("output denied"),
            new IOException("disk is full"),
            new InvalidDataException("invalid range"),
            new JsonException("invalid data"),
            new ArgumentException("invalid path"),
            new NotSupportedException("no range support"),
            new AudioLanguageUnavailableException("unavailable language"),
            new IntlApiException("not available in this region"),
            new OperationCanceledException(),
            new Exception("wrapped cancel", new OperationCanceledException()),
            new OutOfMemoryException()
        ];
        foreach (var error in permanent)
            Assert.False(new PageDownloadRetry().TryGetDelay(error, out _));
    }

    [Fact]
    public void HttpTimeoutMayRefreshPageButExplicitCancellationCannot()
    {
        var policy = new PageDownloadRetry();
        var timeout = new TaskCanceledException("HTTP timeout", new TimeoutException());
        Assert.True(policy.TryGetDelay(timeout, out _));
        Assert.True(policy.TryGetDelay(new IOException("clip failed",
            new DownloadInterruptedException("media request timed out", timeout)), out _));
        Assert.False(policy.TryGetDelay(new TaskCanceledException("user cancelled"), out _));
    }

    [Theory]
    [InlineData(403)]
    [InlineData(404)]
    public void ForbiddenAndNotFoundKeepShortLegacyPageReparseBudget(int status)
    {
        var original = new HttpRequestException("media URL unavailable", null, (HttpStatusCode)status);
        Assert.False(NetworkRetry.IsTransient(original));
        var wrapped = new IOException("clip failed", original);
        var delays = TakeDelays(new PageDownloadRetry(), wrapped);

        Assert.Equal(2, delays.Count);
        Assert.All(delays, delay => Assert.Equal(TimeSpan.FromSeconds(3), delay));
        Assert.Equal(delays, TakeDelays(new PageDownloadRetry(), original));
    }

    private static List<TimeSpan> TakeDelays(PageDownloadRetry policy, Exception error)
    {
        var delays = new List<TimeSpan>();
        while (policy.TryGetDelay(error, out var delay))
        {
            Assert.True(delays.Count < 10, "Page retries must remain bounded");
            delays.Add(delay);
        }
        return delays;
    }
}
