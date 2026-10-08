using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace BBDownT.Tests;

/// <summary>
/// DELETE /remove-pending/{id}、/remove-pending/：取消排队中(还没开始)的任务。
/// 与执行队列取任务互斥；已开始或已结束的任务返回409；不写下载历史、不改动文件；
/// 启用Token时鉴权与其他任务接口相同；未启用Token时与网页前端接口一样只接受同源请求
/// </summary>
public class ApiServerPendingRemovalTests : IDisposable
{
    private readonly string temp = Path.Combine(Path.GetTempPath(), "bbdownt-pending-" + Guid.NewGuid().ToString("N"));
    private readonly string root;
    private readonly string historyPath;

    public ApiServerPendingRemovalTests()
    {
        root = Path.Combine(temp, "downloads");
        historyPath = Path.Combine(temp, "data", DownloadHistory.FileName);
        Directory.CreateDirectory(root);
    }

    public void Dispose()
    {
        Directory.Delete(temp, true);
    }

    private static HttpClient CreateClient() =>
        new(new HttpClientHandler { UseCookies = false, UseProxy = false }) { Timeout = TimeSpan.FromSeconds(10) };

    private async Task<(BBDownTApiServer Server, string Origin)> StartAsync(string? token, Func<ServeRequestOptions, DownloadTask, Task>? work = null)
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        var server = new BBDownTApiServer();
        server.SetUpServer(new BBDownTServerOptions { DownloadRoot = root, HistoryPath = historyPath });
        if (work is not null) server.WorkRunner = work;
        await server.StartWithoutQueueAsync($"http://127.0.0.1:{port}", token);
        return (server, $"http://127.0.0.1:{port}");
    }

    private static HttpRequestMessage Request(HttpMethod method, string url, string? token = null, string? origin = null, string? fetchSite = null)
    {
        var request = new HttpRequestMessage(method, url);
        if (token is not null) request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token);
        if (origin is not null) request.Headers.TryAddWithoutValidation("Origin", origin);
        if (fetchSite is not null) request.Headers.TryAddWithoutValidation("Sec-Fetch-Site", fetchSite);
        return request;
    }

    private static async Task<string> AddTask(HttpClient client, string origin, string url, string? token = null)
    {
        var request = Request(HttpMethod.Post, origin + "/add-task", token);
        request.Content = new StringContent(JsonSerializer.Serialize(new Dictionary<string, string> { ["Url"] = url }), Encoding.UTF8, "application/json");
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        // Results.Accepted 按 ASP.NET 默认的 camelCase 输出(taskId)
        return json.RootElement.EnumerateObject().Single(p => p.NameEquals("taskId") || p.NameEquals("TaskId")).Value.GetString()!;
    }

    private sealed record TaskIds(List<string> Pending, List<string> Running, List<string> Finished);

    /// <summary>
    /// 各状态的 TaskId(DownloadTask 反序列化时会生成新的 TaskId，所以直接读JSON)
    /// </summary>
    private static async Task<TaskIds> GetTasks(HttpClient client, string origin, string? token = null)
    {
        using var response = await client.SendAsync(Request(HttpMethod.Get, origin + "/get-tasks/", token));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        List<string> Ids(string state) => json.RootElement.GetProperty(state).EnumerateArray().Select(t => t.GetProperty("TaskId").GetString()!).ToList();
        return new TaskIds(Ids("Pending"), Ids("Running"), Ids("Finished"));
    }

    private Task<HttpResponseMessage> Delete(HttpClient client, string url, string? token = null) =>
        client.SendAsync(Request(HttpMethod.Delete, url, token));

    [Fact]
    public async Task PendingTask_IsRemoved_ThenUnknown()
    {
        var (server, origin) = await StartAsync(token: null);
        try
        {
            using var client = CreateClient();
            var first = await AddTask(client, origin, "BV1t1YxzWEkz");
            var second = await AddTask(client, origin, "av170001");

            using (var removed = await Delete(client, origin + "/remove-pending/" + first))
                Assert.Equal(HttpStatusCode.OK, removed.StatusCode);
            Assert.Equal([second], (await GetTasks(client, origin)).Pending);

            using (var again = await Delete(client, origin + "/remove-pending/" + first))
            {
                Assert.Equal(HttpStatusCode.NotFound, again.StatusCode);
                Assert.Equal("找不到这个排队中的任务", await again.Content.ReadAsStringAsync());
            }
            using (var unknown = await Delete(client, origin + "/remove-pending/no-such-task"))
                Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);

            // 没有写下载历史，也没有在下载目录里产生文件
            Assert.Equal(0, server.History.Count);
            Assert.False(File.Exists(historyPath));
            Assert.Empty(Directory.EnumerateFileSystemEntries(root));
        }
        finally { await server.StopAsync(); }
    }

    [Fact]
    public async Task TaskTheQueueHasAlreadyStarted_Returns409()
    {
        var (server, origin) = await StartAsync(token: null);
        try
        {
            using var client = CreateClient();
            var id = await AddTask(client, origin, "BV1t1YxzWEkz");
            // 执行队列恰好先取走了这个任务
            var started = server.Tasks.StartNext();
            Assert.Equal(id, started?.TaskId);

            using var response = await Delete(client, origin + "/remove-pending/" + id);

            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
            Assert.Equal("任务已开始，无法取消", await response.Content.ReadAsStringAsync());
            Assert.Equal([id], (await GetTasks(client, origin)).Running);
        }
        finally { await server.StopAsync(); }
    }

    [Fact]
    public async Task FinishedTask_Returns409_SayingItHasFinished()
    {
        var (server, origin) = await StartAsync(token: null);
        try
        {
            using var client = CreateClient();
            var id = await AddTask(client, origin, "BV1t1YxzWEkz");
            var started = server.Tasks.StartNext()!;
            server.Tasks.Complete(started, DateTimeOffset.Now.ToUnixTimeSeconds(), true);

            using var response = await Delete(client, origin + "/remove-pending/" + id);

            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
            Assert.Equal("任务已结束，无法取消", await response.Content.ReadAsStringAsync());
            Assert.Equal([id], (await GetTasks(client, origin)).Finished);
        }
        finally { await server.StopAsync(); }
    }

    [Fact]
    public async Task RemoveAllPending_ReturnsTheCount()
    {
        var (server, origin) = await StartAsync(token: null);
        try
        {
            using var client = CreateClient();
            var running = await AddTask(client, origin, "BV1t1YxzWEkz");
            server.Tasks.StartNext();
            await AddTask(client, origin, "av170001");
            await AddTask(client, origin, "av170002");

            using var response = await Delete(client, origin + "/remove-pending/");

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var result = JsonSerializer.Deserialize(await response.Content.ReadAsStringAsync(), AppJsonSerializerContext.Default.PendingRemovalResult);
            Assert.Equal(new PendingRemovalResult(2), result);
            var tasks = await GetTasks(client, origin);
            Assert.Empty(tasks.Pending);
            Assert.Equal([running], tasks.Running);
        }
        finally { await server.StopAsync(); }
    }

    /// <summary>
    /// 真实的执行队列：A 正在执行时取消排队中的 B，B 不会执行、不写历史；C 照常执行
    /// </summary>
    [Fact]
    public async Task Worker_SkipsCancelledTasks_AndRunsTheRest()
    {
        var executed = new List<string>();
        var aStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseA = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        string? a = null, c = null;
        var (server, origin) = await StartAsync(token: null, work: async (option, task) =>
        {
            lock (executed) executed.Add(task.TaskId);
            if (task.TaskId == a)
            {
                aStarted.TrySetResult();
                await releaseA.Task;
            }
            if (task.TaskId == c) cDone.TrySetResult();
        });
        try
        {
            using var client = CreateClient();
            server.EnsureQueueWorkerStarted();
            a = await AddTask(client, origin, "av170001");
            await aStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var b = await AddTask(client, origin, "av170002");
            c = await AddTask(client, origin, "av170003");

            using (var removed = await Delete(client, origin + "/remove-pending/" + b))
                Assert.Equal(HttpStatusCode.OK, removed.StatusCode);
            using (var running = await Delete(client, origin + "/remove-pending/" + a))
                Assert.Equal(HttpStatusCode.Conflict, running.StatusCode);

            releaseA.SetResult();
            await cDone.Task.WaitAsync(TimeSpan.FromSeconds(10));
            // 等 C 记为已完成(历史在标为完成之前写入)
            for (var i = 0; i < 100 && (await GetTasks(client, origin)).Finished.Count < 2; i++) await Task.Delay(50);

            lock (executed) Assert.Equal([a, c], executed);
            var tasks = await GetTasks(client, origin);
            Assert.Equal(new[] { a, c }.Order(), tasks.Finished.Order());
            Assert.Empty(tasks.Pending);
            Assert.Empty(tasks.Running);
            // 取消的任务没有下载历史
            Assert.DoesNotContain(server.History.Snapshot(), e => e.Url.Contains("170002", StringComparison.Ordinal));
            Assert.Equal(2, server.History.Count);
        }
        finally
        {
            releaseA.TrySetResult();
            await server.StopAsync();
        }
    }

    [Fact]
    public async Task WithToken_PendingRemovalRequiresTheToken_AndTheSessionCookieOnlyWorksSameOrigin()
    {
        const string token = "test-token-0123456789";
        var (server, origin) = await StartAsync(token);
        try
        {
            using var client = CreateClient();
            var id = await AddTask(client, origin, "BV1t1YxzWEkz", token);

            using (var anonymous = await Delete(client, origin + "/remove-pending/" + id))
                Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
            using (var anonymousAll = await Delete(client, origin + "/remove-pending/"))
                Assert.Equal(HttpStatusCode.Unauthorized, anonymousAll.StatusCode);

            var crossCookie = Request(HttpMethod.Delete, origin + "/remove-pending/" + id, origin: "http://127.0.0.1:1", fetchSite: "same-site");
            crossCookie.Headers.TryAddWithoutValidation("Cookie", $"{BBDownTApiServer.SessionCookieName}={token}");
            using (var crossCookieResponse = await client.SendAsync(crossCookie))
                Assert.Equal(HttpStatusCode.Unauthorized, crossCookieResponse.StatusCode);
            Assert.Single((await GetTasks(client, origin, token)).Pending);

            var sameCookie = Request(HttpMethod.Delete, origin + "/remove-pending/" + id, origin: origin, fetchSite: "same-origin");
            sameCookie.Headers.TryAddWithoutValidation("Cookie", $"{BBDownTApiServer.SessionCookieName}={token}");
            using (var sameCookieResponse = await client.SendAsync(sameCookie))
                Assert.Equal(HttpStatusCode.OK, sameCookieResponse.StatusCode);
            Assert.Empty((await GetTasks(client, origin, token)).Pending);

            var other = await AddTask(client, origin, "av170001", token);
            using (var withHeader = await Delete(client, origin + "/remove-pending/" + other, token))
                Assert.Equal(HttpStatusCode.OK, withHeader.StatusCode);
        }
        finally { await server.StopAsync(); }
    }

    /// <summary>
    /// 未启用Token时，本机浏览器里打开的其他网页(包括本机其他端口上的)不能取消或清空排队：请求和CORS预检都返回403、不带CORS许可头；
    /// 网页前端(同源)和不带浏览器请求头的脚本照常可用
    /// </summary>
    [Fact]
    public async Task WithoutToken_CrossOriginRequestsAreRefused_ButSameOriginWorks()
    {
        var (server, origin) = await StartAsync(token: null);
        try
        {
            using var client = CreateClient();
            var first = await AddTask(client, origin, "BV1t1YxzWEkz");
            var second = await AddTask(client, origin, "av170001");

            foreach (var path in new[] { "/remove-pending/" + first, "/remove-pending/" })
            {
                using (var crossSite = await client.SendAsync(Request(HttpMethod.Delete, origin + path, origin: "http://evil.example", fetchSite: "cross-site")))
                    Assert.Equal(HttpStatusCode.Forbidden, crossSite.StatusCode);
                using (var otherPort = await client.SendAsync(Request(HttpMethod.Delete, origin + path, origin: "http://127.0.0.1:1", fetchSite: "same-site")))
                    Assert.Equal(HttpStatusCode.Forbidden, otherPort.StatusCode);
                // 较老的浏览器没有 Sec-Fetch-Site：按 Origin 判断
                using (var oldBrowser = await client.SendAsync(Request(HttpMethod.Delete, origin + path, origin: "http://evil.example")))
                    Assert.Equal(HttpStatusCode.Forbidden, oldBrowser.StatusCode);

                var preflight = Request(HttpMethod.Options, origin + path, origin: "http://evil.example", fetchSite: "cross-site");
                preflight.Headers.TryAddWithoutValidation("Access-Control-Request-Method", "DELETE");
                using var preflightResponse = await client.SendAsync(preflight);
                Assert.Equal(HttpStatusCode.Forbidden, preflightResponse.StatusCode);
                Assert.False(preflightResponse.Headers.Contains("Access-Control-Allow-Origin"));
            }
            Assert.Equal([first, second], (await GetTasks(client, origin)).Pending);

            using (var sameOrigin = await client.SendAsync(Request(HttpMethod.Delete, origin + "/remove-pending/" + first, origin: origin, fetchSite: "same-origin")))
                Assert.Equal(HttpStatusCode.OK, sameOrigin.StatusCode);
            using (var script = await Delete(client, origin + "/remove-pending/" + second))
                Assert.Equal(HttpStatusCode.OK, script.StatusCode);
            Assert.Empty((await GetTasks(client, origin)).Pending);
        }
        finally { await server.StopAsync(); }
    }

    [Fact]
    public async Task WithoutToken_DnsRebindingIsRefused()
    {
        var (server, origin) = await StartAsync(token: null);
        try
        {
            using var client = CreateClient();
            var id = await AddTask(client, origin, "BV1t1YxzWEkz");

            foreach (var path in new[] { "/remove-pending/" + id, "/remove-pending/" })
            {
                // 攻击者的域名解析到127.0.0.1：Host 不是本机地址，一律拒绝
                var rebinding = Request(HttpMethod.Delete, origin + path, origin: "http://evil.example", fetchSite: "same-origin");
                rebinding.Headers.Host = "evil.example";
                using var response = await client.SendAsync(rebinding);
                Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            }
            Assert.Single((await GetTasks(client, origin)).Pending);
        }
        finally { await server.StopAsync(); }
    }
}
