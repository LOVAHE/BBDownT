using System.Net;
using System.Net.Sockets;
using System.Text;
using BBDownT.Core.Util;

namespace BBDownT.Tests;

/// <summary>
/// AppHttpClient 不使用自动Cookie容器：响应里的 Set-Cookie 不会被暗中保存并附加到之后的请求，
/// 但登录和Cookie刷新流程仍能从响应头读到 Set-Cookie。
/// </summary>
public class AppHttpClientCookieTests
{
    [Fact]
    public async Task SetCookieStaysReadableButIsNeverReplayedOnLaterRequests()
    {
        await using var server = new RecordingHttpServer(index => index == 0
            ? "Set-Cookie: SESSDATA=test-session; Path=/; HttpOnly\r\nSet-Cookie: bili_jct=test-csrf; Path=/\r\n"
            : "");

        using (var login = await HTTPUtil.AppHttpClient.GetAsync(server.Url("/login")))
        {
            // 扫码登录轮询(BBDownTLoginUtil)和Cookie刷新都从响应头读取 Set-Cookie
            Assert.True(login.Headers.TryGetValues("Set-Cookie", out var setCookies));
            Assert.Contains(setCookies, value => value.StartsWith("SESSDATA=test-session", StringComparison.Ordinal));
        }

        using (await HTTPUtil.AppHttpClient.GetAsync(server.Url("/guest"))) { }

        using (var request = new HttpRequestMessage(HttpMethod.Get, server.Url("/explicit")))
        {
            request.Headers.TryAddWithoutValidation("Cookie", "buvid3=explicit");
            using (await HTTPUtil.AppHttpClient.SendAsync(request)) { }
        }

        Assert.Equal(3, server.Requests.Count);
        Assert.DoesNotContain("cookie:", server.Requests[1], StringComparison.OrdinalIgnoreCase);
        var explicitCookies = server.Requests[2].Split("\r\n").Where(line => line.StartsWith("Cookie:", StringComparison.OrdinalIgnoreCase)).ToList();
        Assert.Equal(["Cookie: buvid3=explicit"], explicitCookies);
    }

    /// <summary>
    /// 只接受本机连接的极简HTTP服务器，记录每个请求的请求头
    /// </summary>
    private sealed class RecordingHttpServer : IAsyncDisposable
    {
        private readonly TcpListener listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource stop = new();
        private readonly Task loop;
        private readonly Func<int, string> extraHeaders;
        private readonly List<string> requests = [];

        public RecordingHttpServer(Func<int, string> extraHeaders)
        {
            this.extraHeaders = extraHeaders;
            listener.Start();
            loop = Task.Run(AcceptLoopAsync);
        }

        public List<string> Requests
        {
            get { lock (requests) return [.. requests]; }
        }

        public string Url(string path) => $"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}{path}";

        private async Task AcceptLoopAsync()
        {
            while (!stop.IsCancellationRequested)
            {
                TcpClient client;
                try { client = await listener.AcceptTcpClientAsync(stop.Token); }
                catch (OperationCanceledException) { return; }
                catch (SocketException) { return; }
                using (client)
                {
                    var stream = client.GetStream();
                    var buffer = new byte[16384];
                    var received = new StringBuilder();
                    while (!received.ToString().Contains("\r\n\r\n"))
                    {
                        var read = await stream.ReadAsync(buffer, stop.Token);
                        if (read == 0) break;
                        received.Append(Encoding.ASCII.GetString(buffer, 0, read));
                    }
                    int index;
                    lock (requests)
                    {
                        index = requests.Count;
                        requests.Add(received.ToString());
                    }
                    var response = "HTTP/1.1 200 OK\r\n" + extraHeaders(index) + "Content-Length: 2\r\nConnection: close\r\n\r\nok";
                    await stream.WriteAsync(Encoding.ASCII.GetBytes(response), stop.Token);
                }
            }
        }

        public async ValueTask DisposeAsync()
        {
            stop.Cancel();
            listener.Stop();
            try { await loop; } catch { }
            stop.Dispose();
        }
    }
}
