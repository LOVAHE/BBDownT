using System.Net.Sockets;

using BBDownT.Core.Util;

namespace BBDownT.Tests;

public class NetworkRetryTests
{
    [Theory]
    [InlineData("socket")]
    [InlineData("http")]
    [InlineData("timeout")]
    [InlineData("taskCanceled")]
    public void IsTransientNetworkError_DetectsTransportFailures(string kind)
    {
        Assert.True(NetworkRetry.IsTransientNetworkError(Failure(kind)));
    }

    [Theory]
    [InlineData("unsupported")]
    [InlineData("invalid")]
    [InlineData("canceled")]
    [InlineData("other")]
    public void IsTransientNetworkError_IgnoresProtocolAndRealCancellation(string kind)
    {
        Assert.False(NetworkRetry.IsTransientNetworkError(Failure(kind)));
    }

    [Fact]
    public void IsTransientNetworkError_FindsWrappedAndAggregatedFailures()
    {
        var dns = Failure("http");

        Assert.True(NetworkRetry.IsTransientNetworkError(
            new Exception("P1 下载失败", new IOException("包装", dns))));
        Assert.True(NetworkRetry.IsTransientNetworkError(
            new AggregateException(Failure("unsupported"), dns)));
        Assert.False(NetworkRetry.IsTransientNetworkError(
            new AggregateException(Failure("unsupported"))));
    }

    [Theory]
    [InlineData(1, 3000)]
    [InlineData(2, 6000)]
    [InlineData(3, 12000)]
    [InlineData(4, 15000)]
    [InlineData(9, 15000)]
    public void GetBackoffMilliseconds_GrowsExponentiallyAndIsCapped(int attempt, int expected)
    {
        Assert.Equal(expected, NetworkRetry.GetBackoffMilliseconds(attempt, 3000, 15000));
    }

    [Fact]
    public async Task RunWithNetworkRetryAsync_StopsAtBudgetAndKeepsRootException()
    {
        var delays = new List<int>();
        var attempts = 0;
        var failure = Failure("http");

        var error = await Assert.ThrowsAsync<HttpRequestException>(() => NetworkRetry.RunWithNetworkRetryAsync(
            () =>
            {
                attempts++;
                throw failure;
            },
            milliseconds =>
            {
                delays.Add(milliseconds);
                return Task.CompletedTask;
            },
            "获取网页内容"));

        Assert.Same(failure, error);
        Assert.Equal(3, attempts);
        Assert.Equal(new[] { 1000, 2000 }, delays);
    }

    [Fact]
    public async Task RunWithNetworkRetryAsync_ReturnsAsSoonAsLinkRecovers()
    {
        var delays = new List<int>();
        var attempts = 0;

        var value = await NetworkRetry.RunWithNetworkRetryAsync(
            () =>
            {
                attempts++;
                return attempts < 3
                    ? throw Failure("taskCanceled")
                    : Task.FromResult("ok");
            },
            milliseconds =>
            {
                delays.Add(milliseconds);
                return Task.CompletedTask;
            },
            "获取网页内容");

        Assert.Equal("ok", value);
        Assert.Equal(3, attempts);
        Assert.Equal(new[] { 1000, 2000 }, delays);
    }

    [Fact]
    public async Task RunWithNetworkRetryAsync_DoesNotRetryProtocolErrors()
    {
        var attempts = 0;

        await Assert.ThrowsAsync<InvalidDataException>(() => NetworkRetry.RunWithNetworkRetryAsync(
            () =>
            {
                attempts++;
                throw Failure("invalid");
            },
            _ => Task.CompletedTask,
            "获取网页内容"));

        Assert.Equal(1, attempts);
    }

    [Fact]
    public async Task RunWithDownloadRetryAsync_RetriesBrokenLink()
    {
        var attempts = 0;
        var delays = new List<int>();

        await Assert.ThrowsAsync<IOException>(() => NetworkRetry.RunWithDownloadRetryAsync(
            () =>
            {
                attempts++;
                throw new IOException("续传位置不再有效，已清空临时文件以便重试");
            },
            milliseconds =>
            {
                delays.Add(milliseconds);
                return Task.CompletedTask;
            },
            5,
            "下载分片 0"));

        Assert.Equal(5, attempts);
        Assert.Equal(new[] { 3000, 6000, 12000, 15000 }, delays);
    }

    [Fact]
    public async Task RunWithDownloadRetryAsync_DoesNotRetryUnsupportedRange()
    {
        var attempts = 0;

        await Assert.ThrowsAsync<NotSupportedException>(() => NetworkRetry.RunWithDownloadRetryAsync(
            () =>
            {
                attempts++;
                throw Failure("unsupported");
            },
            _ => Task.CompletedTask,
            5,
            "下载分片 1"));

        Assert.Equal(1, attempts);
    }

    [Fact]
    public void DescribeRootCause_ReportsInnermostType()
    {
        var wrapped = new Exception("分片 70 下载失败",
            new AggregateException(new HttpRequestException("不知道这样的主机。", new SocketException(11001))));

        Assert.StartsWith("SocketException:", NetworkRetry.DescribeRootCause(wrapped));
    }

    [Fact]
    public void DescribeRootCause_RedactsCookieMaterial()
    {
        var error = new Exception("失败", new HttpRequestException("请求头 Cookie: SESSDATA=secret 被拒绝"));

        Assert.Contains("<redacted>", NetworkRetry.DescribeRootCause(error));
    }

    private static Exception Failure(string kind) => kind switch
    {
        "socket" => new SocketException(11001),
        "http" => new HttpRequestException("不知道这样的主机。 (api.bilibili.com:443)", new SocketException(11001)),
        "timeout" => new TimeoutException(),
        "taskCanceled" => new TaskCanceledException("请求超时", new TimeoutException()),
        "canceled" => new OperationCanceledException(),
        "unsupported" => new NotSupportedException("Range request is not supported."),
        "invalid" => new InvalidDataException("响应不是合法 JSON"),
        _ => new InvalidOperationException("其他")
    };
}
