using System.Net;
using System.Net.Sockets;
using BBDownT.Core.Util;

namespace BBDownT.Tests;

public class HttpUtilRetryTests : IDisposable
{
    private readonly Func<HttpClient, HttpRequestMessage, Task<HttpResponseMessage>> originalTransport = HTTPUtil.SendTransport;
    private readonly Func<int, Task> originalRetryDelay = HTTPUtil.SendRetryDelay;

    [Fact]
    public async Task GetWebSourceAsync_TransientDnsFailure_RebuildsRequestAndRecovers()
    {
        var attempts = 0;
        var delays = new List<int>();
        var userAgents = new List<string?>();
        HTTPUtil.SendTransport = (client, request) =>
        {
            attempts++;
            userAgents.Add(request.Headers.TryGetValues("User-Agent", out var values)
                ? string.Join(" ", values)
                : null);
            if (attempts == 1)
                throw new HttpRequestException("不知道这样的主机。 (api.bilibili.com:443)", new SocketException(11001));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("ok") });
        };
        HTTPUtil.SendRetryDelay = milliseconds =>
        {
            delays.Add(milliseconds);
            return Task.CompletedTask;
        };

        var body = await HTTPUtil.GetWebSourceAsync("https://api.bilibili.com/x/web-interface/nav");

        Assert.Equal("ok", body);
        Assert.Equal(2, attempts);
        Assert.Equal(new[] { 1000 }, delays);
        // HttpRequestMessage 不可重复发送, 重试必须重建请求, 因此请求头要与首次一致
        Assert.All(userAgents, userAgent => Assert.False(string.IsNullOrEmpty(userAgent)));
    }

    [Fact]
    public async Task GetWebSourceAsync_StopsAfterBoundedNetworkRetries()
    {
        var attempts = 0;
        var delays = new List<int>();
        HTTPUtil.SendTransport = (_, _) =>
        {
            attempts++;
            throw new HttpRequestException("不知道这样的主机。 (api.bilibili.com:443)", new SocketException(11001));
        };
        HTTPUtil.SendRetryDelay = milliseconds =>
        {
            delays.Add(milliseconds);
            return Task.CompletedTask;
        };

        await Assert.ThrowsAsync<HttpRequestException>(
            () => HTTPUtil.GetWebSourceAsync("https://api.bilibili.com/x/web-interface/nav"));

        Assert.Equal(3, attempts);
        Assert.Equal(new[] { 1000, 2000 }, delays);
    }

    [Fact]
    public async Task GetWebSourceAsync_NonNetworkError_IsNotRetried()
    {
        var attempts = 0;
        HTTPUtil.SendTransport = (_, _) =>
        {
            attempts++;
            throw new InvalidOperationException("代理配置错误");
        };
        HTTPUtil.SendRetryDelay = _ => Task.CompletedTask;

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => HTTPUtil.GetWebSourceAsync("https://api.bilibili.com/x/web-interface/nav"));

        Assert.Equal(1, attempts);
    }

    public void Dispose()
    {
        HTTPUtil.SendTransport = originalTransport;
        HTTPUtil.SendRetryDelay = originalRetryDelay;
    }
}
