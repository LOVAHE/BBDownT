using System.Net;
using System.Net.Sockets;
using BBDownT.Core.Util;

namespace BBDownT.Tests;

/// <summary>
/// HTTPUtil.UseCancellation：解析预览超时或客户端断开后，正在进行的请求立即中止，不必等 HttpClient 自身的2分钟超时
/// </summary>
public class HttpFlowCancellationTests
{
    [Fact]
    public async Task RequestsInsideTheScopeStopAsSoonAsTheTokenIsCancelled()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var accepted = new List<TcpClient>();
        // 只接受连接、从不响应，模拟卡住的B站接口
        var acceptLoop = Task.Run(async () =>
        {
            try { while (true) accepted.Add(await listener.AcceptTcpClientAsync()); }
            catch (Exception) { }
        });
        var url = $"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/x/web-interface/nav";
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        var watch = System.Diagnostics.Stopwatch.StartNew();

        using (HTTPUtil.UseCancellation(cts.Token))
        {
            Assert.Equal(cts.Token, HTTPUtil.FlowCancellation);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => HTTPUtil.GetWebSourceAsync(url));
        }

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), watch.Elapsed.ToString());
        Assert.Equal(CancellationToken.None, HTTPUtil.FlowCancellation);
        listener.Stop();
        foreach (var client in accepted) client.Dispose();
        await acceptLoop;
    }

    [Fact]
    public async Task TheTokenFlowsIntoAwaitedChildMethodsAndIsRestoredAfterwards()
    {
        using var cts = new CancellationTokenSource();
        CancellationToken seenInChild = default;

        using (HTTPUtil.UseCancellation(cts.Token))
        {
            await Task.Run(async () =>
            {
                await Task.Yield();
                seenInChild = HTTPUtil.FlowCancellation;
            });
        }

        Assert.Equal(cts.Token, seenInChild);
        Assert.Equal(CancellationToken.None, HTTPUtil.FlowCancellation);
    }
}
