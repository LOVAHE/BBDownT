using System.Net;
using System.Net.Sockets;
using Microsoft.AspNetCore.Http;

namespace BBDownT.Tests;

public class ApiServerOriginTests
{
    [Theory]
    [InlineData("127.0.0.1:23333", null, null, false)]                              // curl、脚本
    [InlineData("127.0.0.1:23333", "http://127.0.0.1:23333", "same-origin", false)] // 网页前端自己
    [InlineData("127.0.0.1:23333", null, "same-origin", false)]
    [InlineData("127.0.0.1:23333", null, "none", false)]                           // 地址栏直接打开
    [InlineData("127.0.0.1:23333", "http://evil.example", null, true)]
    [InlineData("127.0.0.1:23333", "http://127.0.0.1:8080", null, true)]            // 同一主机的其他端口
    [InlineData("127.0.0.1:23333", "null", null, true)]                             // 沙箱iframe、本地文件
    [InlineData("127.0.0.1:23333", null, "cross-site", true)]                       // 图片、表单等不带Origin的请求
    [InlineData("127.0.0.1:23333", null, "same-site", true)]
    [InlineData("localhost:23333", "http://LOCALHOST:23333", null, false)]
    [InlineData("[::1]:23333", "http://[::1]:23333", null, false)]
    [InlineData("bbdown.example.com", "https://bbdown.example.com", "same-origin", false)] // HTTPS反向代理
    [InlineData("bbdown.example.com", "https://bbdown.example.com:8443", null, true)]
    // 反向代理改写了 Host(如 nginx 默认的 proxy_set_header Host $proxy_host)：有 Sec-Fetch-Site 时只按它判断
    [InlineData("127.0.0.1:23333", "https://bb.example.com", "same-origin", false)]
    [InlineData("127.0.0.1:23333", "https://bb.example.com", "cross-site", true)]
    [InlineData("127.0.0.1:23333", "http://127.0.0.1:23333", "same-site", true)]
    public void IsCrossOriginBrowserRequest_ComparesOriginWithTheRequestedHost(string host, string? origin, string? fetchSite, bool expected)
    {
        var context = new DefaultHttpContext();
        context.Request.Host = HostString.FromUriComponent(host);
        if (origin is not null) context.Request.Headers.Origin = origin;
        if (fetchSite is not null) context.Request.Headers["Sec-Fetch-Site"] = fetchSite;

        Assert.Equal(expected, BBDownTApiServer.IsCrossOriginBrowserRequest(context.Request));
    }

    [Theory]
    [InlineData("bb.example.com", false)]
    [InlineData("bb.example.com, 127.0.0.1", false)]
    [InlineData("evil.example", true)]
    public void WithoutFetchMetadata_OriginMayMatchTheForwardedHost(string forwardedHost, bool expected)
    {
        var context = new DefaultHttpContext();
        context.Request.Host = HostString.FromUriComponent("127.0.0.1:23333");
        context.Request.Headers.Origin = "https://bb.example.com";
        context.Request.Headers["X-Forwarded-Host"] = forwardedHost;

        Assert.Equal(expected, BBDownTApiServer.IsCrossOriginBrowserRequest(context.Request));
    }

    [Theory]
    [InlineData("127.0.0.1:23333", true)]
    [InlineData("127.0.0.2", true)]
    [InlineData("[::1]:23333", true)]
    [InlineData("localhost:23333", true)]
    [InlineData("LOCALHOST", true)]
    [InlineData("bbdownt.localhost:23333", true)]
    [InlineData("evil.example:23333", false)]       // DNS rebinding：攻击者的域名解析到 127.0.0.1
    [InlineData("localhost.evil.example", false)]
    [InlineData("0.0.0.0:23333", false)]
    [InlineData("192.168.1.2:23333", false)]
    [InlineData("bb.example.com", true, "bb.example.com")]
    [InlineData("BB.example.com:443", true, "https://bb.example.com/")]
    [InlineData("other.example.com", false, "bb.example.com")]
    public void HostCheckWithoutToken_OnlyAcceptsLoopbackNamesAndConfiguredHosts(string host, bool expected, string? allowed = null)
    {
        Assert.Equal(expected, BBDownTApiServer.IsAllowedHostWithoutToken(
            HostString.FromUriComponent(host), BBDownTApiServer.ParseAllowedHosts(allowed)));
    }

    [Fact]
    public void ParseAllowedHosts_TakesHostNamesFromACommaSeparatedList()
    {
        Assert.Equal(["bb.example.com", "nas.lan", "::1"], BBDownTApiServer.ParseAllowedHosts(" bb.example.com , https://nas.lan:8443/x,[::1] "));
        Assert.Empty(BBDownTApiServer.ParseAllowedHosts(" "));
    }

    [Theory]
    [InlineData("/parse", true)]
    [InlineData("/ui/status", true)]
    [InlineData("/ui/bili-login", true)]
    [InlineData("/files/", true)]
    [InlineData("/files/download", true)]
    [InlineData("/remove-pending", true)]
    [InlineData("/remove-pending/abc", true)]
    [InlineData("/add-task", false)]
    [InlineData("/get-tasks/", false)]
    [InlineData("/remove-finished/", false)]
    [InlineData("/", false)]
    [InlineData("/uiother", false)]
    public void BrowserUiPaths_AreTheWebFrontendEndpoints(string path, bool expected)
    {
        Assert.Equal(expected, BBDownTApiServer.IsBrowserUiPath(new PathString(path)));
    }

    [Fact]
    public async Task WithoutToken_CrossOriginFrontendRequestsAreRefused_ButSameOriginAndApiCallsStillWork()
    {
        await using var server = await RunningServer.StartAsync(token: null);
        using var client = RunningServer.CreateClient();

        using var crossStatus = await client.SendAsync(server.Request(HttpMethod.Get, "/ui/status", origin: "http://evil.example"));
        Assert.Equal(HttpStatusCode.Forbidden, crossStatus.StatusCode);
        Assert.False(crossStatus.Headers.Contains("Access-Control-Allow-Origin"));

        using var crossFiles = await client.SendAsync(server.Request(HttpMethod.Get, "/files/", fetchSite: "cross-site"));
        Assert.Equal(HttpStatusCode.Forbidden, crossFiles.StatusCode);

        var preflight = server.Request(HttpMethod.Options, "/parse", origin: "http://evil.example");
        preflight.Headers.TryAddWithoutValidation("Access-Control-Request-Method", "POST");
        preflight.Headers.TryAddWithoutValidation("Access-Control-Request-Headers", "content-type");
        using var preflightResponse = await client.SendAsync(preflight);
        Assert.Equal(HttpStatusCode.Forbidden, preflightResponse.StatusCode);
        Assert.False(preflightResponse.Headers.Contains("Access-Control-Allow-Origin"));

        using var sameOrigin = await client.SendAsync(server.Request(HttpMethod.Get, "/files/", origin: server.Origin, fetchSite: "same-origin"));
        Assert.Equal(HttpStatusCode.OK, sameOrigin.StatusCode);

        using var noBrowser = await client.SendAsync(server.Request(HttpMethod.Get, "/files/"));
        Assert.Equal(HttpStatusCode.OK, noBrowser.StatusCode);

        // 其他API仍允许跨源调用(与以前一致)
        var addTask = server.Request(HttpMethod.Post, "/add-task", origin: "http://evil.example");
        addTask.Content = new StringContent("{\"Url\":\"\"}", System.Text.Encoding.UTF8, "application/json");
        using var addTaskResponse = await client.SendAsync(addTask);
        Assert.Equal(HttpStatusCode.BadRequest, addTaskResponse.StatusCode);
        Assert.Equal("*", addTaskResponse.Headers.GetValues("Access-Control-Allow-Origin").Single());
    }

    [Fact]
    public async Task WithoutToken_DnsRebindingIsRefusedOnEveryPath_UnlessTheHostIsAllowed()
    {
        await using var server = await RunningServer.StartAsync(token: null);
        using var client = RunningServer.CreateClient();

        foreach (var path in new[] { "/", "/ui/status", "/files/", "/get-tasks/" })
        {
            // 攻击者的域名解析到127.0.0.1：Origin 与 Host 一致，同源检查挡不住，Host 检查可以
            var rebinding = server.Request(HttpMethod.Get, path, origin: "http://evil.example", fetchSite: "same-origin");
            rebinding.Headers.Host = "evil.example";
            using var response = await client.SendAsync(rebinding);
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            Assert.False(response.Headers.Contains("Access-Control-Allow-Origin"));
        }

        var local = server.Request(HttpMethod.Get, "/files/");
        local.Headers.Host = "localhost";
        using (var localResponse = await client.SendAsync(local)) Assert.Equal(HttpStatusCode.OK, localResponse.StatusCode);

        await using var proxied = await RunningServer.StartAsync(token: null, allowedHosts: "bb.example.com");
        var viaProxy = proxied.Request(HttpMethod.Get, "/files/", origin: "https://bb.example.com", fetchSite: "same-origin");
        viaProxy.Headers.Host = "bb.example.com";
        using (var proxiedResponse = await client.SendAsync(viaProxy)) Assert.Equal(HttpStatusCode.OK, proxiedResponse.StatusCode);
    }

    [Fact]
    public async Task WithToken_SessionCookieWorksBehindAProxyThatRewritesTheHost()
    {
        const string token = "test-token-0123456789";
        await using var server = await RunningServer.StartAsync(token);
        using var client = RunningServer.CreateClient();

        // nginx 默认 proxy_set_header Host $proxy_host：Host 是 127.0.0.1:端口，Origin 是对外的域名
        var proxied = server.Request(HttpMethod.Get, "/files/", origin: "https://bb.example.com", fetchSite: "same-origin");
        proxied.Headers.TryAddWithoutValidation("Cookie", $"{BBDownTApiServer.SessionCookieName}={token}");
        using var response = await client.SendAsync(proxied);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task WithToken_HeaderTokenWorksCrossOrigin_ButTheSessionCookieOnlyWorksSameOrigin()
    {
        const string token = "test-token-0123456789";
        await using var server = await RunningServer.StartAsync(token);
        using var client = RunningServer.CreateClient();

        var withHeader = server.Request(HttpMethod.Get, "/files/", origin: "http://other-tool.example");
        withHeader.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token);
        using var headerResponse = await client.SendAsync(withHeader);
        Assert.Equal(HttpStatusCode.OK, headerResponse.StatusCode);

        var crossCookie = server.Request(HttpMethod.Get, "/files/", origin: "http://127.0.0.1:1", fetchSite: "same-site");
        crossCookie.Headers.TryAddWithoutValidation("Cookie", $"{BBDownTApiServer.SessionCookieName}={token}");
        using var crossCookieResponse = await client.SendAsync(crossCookie);
        Assert.Equal(HttpStatusCode.Unauthorized, crossCookieResponse.StatusCode);

        var sameCookie = server.Request(HttpMethod.Get, "/files/", origin: server.Origin, fetchSite: "same-origin");
        sameCookie.Headers.TryAddWithoutValidation("Cookie", $"{BBDownTApiServer.SessionCookieName}={token}");
        using var sameCookieResponse = await client.SendAsync(sameCookie);
        Assert.Equal(HttpStatusCode.OK, sameCookieResponse.StatusCode);

        // 启用Token时预检照常放行，真正的请求再按Token鉴权
        var preflight = server.Request(HttpMethod.Options, "/parse", origin: "http://other-tool.example");
        preflight.Headers.TryAddWithoutValidation("Access-Control-Request-Method", "POST");
        using var preflightResponse = await client.SendAsync(preflight);
        Assert.Equal(HttpStatusCode.NoContent, preflightResponse.StatusCode);
    }

    private sealed class RunningServer : IAsyncDisposable
    {
        private readonly BBDownTApiServer server;
        private readonly string root;

        private RunningServer(BBDownTApiServer server, string root, int port)
        {
            this.server = server;
            this.root = root;
            Origin = $"http://127.0.0.1:{port}";
        }

        public string Origin { get; }

        public static async Task<RunningServer> StartAsync(string? token, string? allowedHosts = null)
        {
            var root = Path.Combine(Path.GetTempPath(), "bbdownt-origin-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            var port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            var server = new BBDownTApiServer();
            server.SetUpServer(new BBDownTServerOptions { DownloadRoot = root, AllowedHosts = BBDownTApiServer.ParseAllowedHosts(allowedHosts) });
            await server.StartWithoutQueueAsync($"http://127.0.0.1:{port}", token);
            return new RunningServer(server, root, port);
        }

        public static HttpClient CreateClient() =>
            new(new HttpClientHandler { UseCookies = false, UseProxy = false }) { Timeout = TimeSpan.FromSeconds(10) };

        public HttpRequestMessage Request(HttpMethod method, string path, string? origin = null, string? fetchSite = null)
        {
            var request = new HttpRequestMessage(method, Origin + path);
            if (origin is not null) request.Headers.TryAddWithoutValidation("Origin", origin);
            if (fetchSite is not null) request.Headers.TryAddWithoutValidation("Sec-Fetch-Site", fetchSite);
            return request;
        }

        public async ValueTask DisposeAsync()
        {
            await server.StopAsync();
            Directory.Delete(root, true);
        }
    }
}
