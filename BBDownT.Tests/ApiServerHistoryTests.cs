using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;

namespace BBDownT.Tests;

/// <summary>
/// /history 接口：与 /files 相同的Token鉴权和同源限制；只删除记录，不删除文件；Exists 在查询时检查
/// </summary>
public class ApiServerHistoryTests : IDisposable
{
    private readonly string temp = Path.Combine(Path.GetTempPath(), "bbdownt-history-api-" + Guid.NewGuid().ToString("N"));
    private readonly string root;
    private readonly string historyPath;

    public ApiServerHistoryTests()
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

    private async Task<(BBDownTApiServer Server, string Origin)> StartAsync(string? token)
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        var server = new BBDownTApiServer();
        server.SetUpServer(new BBDownTServerOptions { DownloadRoot = root, HistoryPath = historyPath });
        await server.StartWithoutQueueAsync($"http://127.0.0.1:{port}", token);
        return (server, $"http://127.0.0.1:{port}");
    }

    private static HttpRequestMessage Request(HttpMethod method, string url, string? origin = null, string? fetchSite = null, string? token = null)
    {
        var request = new HttpRequestMessage(method, url);
        if (origin is not null) request.Headers.TryAddWithoutValidation("Origin", origin);
        if (fetchSite is not null) request.Headers.TryAddWithoutValidation("Sec-Fetch-Site", fetchSite);
        if (token is not null) request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token);
        return request;
    }

    private static async Task<DownloadHistoryList> GetList(HttpClient client, string url, string? token = null)
    {
        using var response = await client.SendAsync(Request(HttpMethod.Get, url, token: token));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return JsonSerializer.Deserialize(await response.Content.ReadAsStringAsync(), AppJsonSerializerContext.Default.DownloadHistoryList)!;
    }

    [Fact]
    public async Task WithToken_HistoryRequiresTheToken()
    {
        const string token = "test-token-0123456789";
        var (server, origin) = await StartAsync(token);
        try
        {
            server.History.Add(DownloadHistoryStoreTests.Entry("记录"));
            using var client = CreateClient();

            using (var anonymous = await client.SendAsync(Request(HttpMethod.Get, origin + "/history/")))
                Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
            using (var anonymousDelete = await client.SendAsync(Request(HttpMethod.Delete, origin + "/history/")))
                Assert.Equal(HttpStatusCode.Unauthorized, anonymousDelete.StatusCode);
            Assert.Equal(1, server.History.Count);

            var list = await GetList(client, origin + "/history/", token);
            Assert.Equal(["记录"], list.Items.Select(e => e.Title));

            // 网页登录Cookie只接受同源请求
            var crossCookie = Request(HttpMethod.Get, origin + "/history", origin: "http://127.0.0.1:1", fetchSite: "same-site");
            crossCookie.Headers.TryAddWithoutValidation("Cookie", $"{BBDownTApiServer.SessionCookieName}={token}");
            using (var crossResponse = await client.SendAsync(crossCookie))
                Assert.Equal(HttpStatusCode.Unauthorized, crossResponse.StatusCode);
        }
        finally { await server.StopAsync(); }
    }

    [Fact]
    public async Task WithoutToken_CrossOriginBrowserRequestsAreRefused()
    {
        var (server, origin) = await StartAsync(null);
        try
        {
            server.History.Add(DownloadHistoryStoreTests.Entry("记录"));
            using var client = CreateClient();

            using (var cross = await client.SendAsync(Request(HttpMethod.Get, origin + "/history/", origin: "http://evil.example")))
            {
                Assert.Equal(HttpStatusCode.Forbidden, cross.StatusCode);
                Assert.False(cross.Headers.Contains("Access-Control-Allow-Origin"));
            }
            using (var crossDelete = await client.SendAsync(Request(HttpMethod.Delete, origin + "/history/", fetchSite: "cross-site")))
                Assert.Equal(HttpStatusCode.Forbidden, crossDelete.StatusCode);
            var preflight = Request(HttpMethod.Options, origin + "/history/", origin: "http://evil.example");
            preflight.Headers.TryAddWithoutValidation("Access-Control-Request-Method", "DELETE");
            using (var preflightResponse = await client.SendAsync(preflight))
                Assert.Equal(HttpStatusCode.Forbidden, preflightResponse.StatusCode);
            Assert.Equal(1, server.History.Count);

            using (var same = await client.SendAsync(Request(HttpMethod.Get, origin + "/history/", origin: origin, fetchSite: "same-origin")))
                Assert.Equal(HttpStatusCode.OK, same.StatusCode);
            Assert.Single((await GetList(client, origin + "/history")).Items);
        }
        finally { await server.StopAsync(); }
    }

    [Fact]
    public async Task Get_FiltersPagesAndChecksWhetherFilesStillExist()
    {
        var (server, origin) = await StartAsync(null);
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "sub"));
            File.WriteAllText(Path.Combine(root, "sub", "a.mp4"), "a");
            File.WriteAllText(Path.Combine(temp, "outside.txt"), "secret");
            server.History.Add(DownloadHistoryStoreTests.Entry("城市夜景", "风景UP", "BV1AAAAAAAAA", files: "sub/a.mp4"));
            server.History.Add(DownloadHistoryStoreTests.Entry("猫咪", "萌宠UP", "BV1BBBBBBBBB", files: ["gone.mp4", "../outside.txt", Path.Combine(temp, "outside.txt"), "sub/../../outside.txt"]));
            server.History.Add(DownloadHistoryStoreTests.Entry("城市延时", "萌宠UP", "BV1CCCCCCCCC"));
            using var client = CreateClient();

            var all = await GetList(client, origin + "/history/");
            Assert.Equal(["城市延时", "猫咪", "城市夜景"], all.Items.Select(e => e.Title));
            Assert.Equal((3, 3, 0, BBDownTApiServer.DefaultHistoryPageSize), (all.Total, all.Matched, all.Offset, all.Limit));

            var night = all.Items[2];
            Assert.Equal([new DownloadHistoryFile("sub/a.mp4", 1, true)], night.Files);
            // 不存在的文件、越出下载根目录的路径一律视为不存在(网页上不会给出链接)
            Assert.All(all.Items[1].Files, file => Assert.False(file.Exists));

            File.Delete(Path.Combine(root, "sub", "a.mp4"));
            Assert.False((await GetList(client, origin + "/history/?q=夜景")).Items.Single().Files.Single().Exists);

            var filtered = await GetList(client, origin + "/history/?q=" + Uri.EscapeDataString("萌宠") + "&offset=1&limit=1");
            Assert.Equal((3, 2, 1, 1), (filtered.Total, filtered.Matched, filtered.Offset, filtered.Limit));
            Assert.Equal(["猫咪"], filtered.Items.Select(e => e.Title));
            Assert.Equal(["城市夜景"], (await GetList(client, origin + "/history/?q=bv1aaaaaaaaa")).Items.Select(e => e.Title));
            Assert.Empty((await GetList(client, origin + "/history/?q=城市&offset=5")).Items);

            // 不合法的分页参数按默认值处理，limit 有上限
            var lenient = await GetList(client, origin + "/history/?offset=-3&limit=abc");
            Assert.Equal((0, BBDownTApiServer.DefaultHistoryPageSize), (lenient.Offset, lenient.Limit));
            Assert.Equal(BBDownTApiServer.MaxHistoryPageSize, (await GetList(client, origin + "/history/?limit=100000")).Limit);

            // 文件接口同样拒绝越出下载根目录的路径
            using var traversal = await client.GetAsync(origin + "/files/download?path=" + Uri.EscapeDataString("../outside.txt"));
            Assert.Equal(HttpStatusCode.NotFound, traversal.StatusCode);
        }
        finally { await server.StopAsync(); }
    }

    [Fact]
    public async Task Delete_RemovesRecordsButNeverFiles()
    {
        var (server, origin) = await StartAsync(null);
        try
        {
            var file = Path.Combine(root, "a.mp4");
            File.WriteAllText(file, "a");
            var first = DownloadHistoryStoreTests.Entry("第一个", files: "a.mp4");
            var second = DownloadHistoryStoreTests.Entry("第二个", files: "a.mp4");
            server.History.AddRange([first, second]);
            using var client = CreateClient();

            using (var deleted = await client.DeleteAsync(origin + "/history/" + first.Id))
                Assert.Equal(HttpStatusCode.OK, deleted.StatusCode);
            using (var missing = await client.DeleteAsync(origin + "/history/" + first.Id))
                Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
            Assert.Equal([second.Id], (await GetList(client, origin + "/history/")).Items.Select(e => e.Id));
            Assert.True(File.Exists(file));

            using (var cleared = await client.DeleteAsync(origin + "/history/"))
                Assert.Equal(HttpStatusCode.OK, cleared.StatusCode);
            var empty = await GetList(client, origin + "/history/");
            Assert.Equal((0, 0), (empty.Total, empty.Matched));
            Assert.True(File.Exists(file));
            // 记录的删除已写入文件
            Assert.Equal(0, new DownloadHistoryStore(historyPath).Count);
        }
        finally { await server.StopAsync(); }
    }

    [Fact]
    public async Task Files_ShowTheVideoTitleOfTheirNewestHistoryEntry()
    {
        var (server, origin) = await StartAsync(null);
        try
        {
            File.WriteAllText(Path.Combine(root, "a.mp4"), "a");
            File.WriteAllText(Path.Combine(root, "other.mp4"), "b");
            server.History.Add(DownloadHistoryStoreTests.Entry("旧标题", files: "a.mp4"));
            server.History.Add(DownloadHistoryStoreTests.Entry("新标题", files: "a.mp4"));
            using var client = CreateClient();

            var json = JsonNode.Parse(await client.GetStringAsync(origin + "/files/"))!.AsArray();

            Assert.Equal("新标题", json.Single(f => f!["Path"]!.GetValue<string>() == "a.mp4")!["Title"]!.GetValue<string>());
            Assert.Null(json.Single(f => f!["Path"]!.GetValue<string>() == "other.mp4")!["Title"]);
        }
        finally { await server.StopAsync(); }
    }

    [Theory]
    [InlineData("/history", true)]
    [InlineData("/history/", true)]
    [InlineData("/history/abc", true)]
    [InlineData("/historyx", false)]
    public void HistoryIsAWebFrontendPath(string path, bool expected)
    {
        Assert.Equal(expected, BBDownTApiServer.IsBrowserUiPath(new PathString(path)));
    }

    [Theory]
    [InlineData("history.json", true)]
    [InlineData("history.json.0123abcd.tmp", true)]
    [InlineData("history.json.corrupt-20261008-101010", true)]
    [InlineData("HISTORY.JSON", true)]
    [InlineData("history.jsonx", false)]
    [InlineData("my history.json", false)]
    public void HistoryFileInTheDataDirectory_IsNotServedByTheFileApi(string fileName, bool expected)
    {
        Assert.Equal(expected, BBDownTApiServer.IsProtectedFile(Path.Combine(Program.APP_DIR, fileName)));
    }

    [Fact]
    public void ListJson_KeepsTheSourceGeneratedContract()
    {
        var entry = new DownloadHistoryEntry
        {
            Id = "id-1", TaskId = "task-1", FinishedAt = 1760000000,
            Url = "https://www.bilibili.com/video/BV1t1YxzWEkz/?p=2", PageUrl = "https://www.bilibili.com/video/BV1t1YxzWEkz/?p=2",
            Kind = "video", Aid = "170001", Bvid = "BV1t1YxzWEkz", Title = "标题", Owner = "UP主", Pic = "https://i0.hdslb.com/x.jpg", Api = "WEB",
            Pages = [new DownloadHistoryPage(2, "第二P")],
            Streams = ["P2 · WEB · 4K 超清 3840x2160 HEVC · 192K M4A"],
            StreamTags = ["4K 超清", "HEVC", "192K"],
            Files = [new DownloadHistoryFile("标题/[P2]第二P.mp4", 1024, true)],
            TotalBytes = 1024, Success = true,
            Request = new DownloadHistoryRequest { Url = "https://www.bilibili.com/video/BV1t1YxzWEkz/?p=2", VideoStream = "120:HEVC", DfnPriority = "4K 超清", EncodingPriority = "hevc", AudioStream = "30280" }
        };

        var json = JsonSerializer.Serialize(new DownloadHistoryList(1, 1, 0, 50, [entry]), AppJsonSerializerContext.Default.DownloadHistoryList);

        var expected = JsonNode.Parse("""
            {
              "Total":1, "Matched":1, "Offset":0, "Limit":50,
              "Items":[{
                "Id":"id-1", "TaskId":"task-1", "FinishedAt":1760000000,
                "Url":"https://www.bilibili.com/video/BV1t1YxzWEkz/?p=2", "PageUrl":"https://www.bilibili.com/video/BV1t1YxzWEkz/?p=2",
                "Kind":"video", "Aid":"170001", "Bvid":"BV1t1YxzWEkz", "Ep":null, "Title":"标题", "Owner":"UP主",
                "Pic":"https://i0.hdslb.com/x.jpg", "Api":"WEB",
                "Pages":[{"Index":2,"Title":"第二P"}],
                "Streams":["P2 · WEB · 4K 超清 3840x2160 HEVC · 192K M4A"],
                "StreamTags":["4K 超清","HEVC","192K"],
                "Files":[{"Path":"标题/[P2]第二P.mp4","Size":1024,"Exists":true}],
                "TotalBytes":1024, "Success":true, "Skipped":false, "Error":null,
                "Request":{"Url":"https://www.bilibili.com/video/BV1t1YxzWEkz/?p=2","DfnPriority":"4K 超清","EncodingPriority":"hevc","VideoStream":"120:HEVC","AudioStream":"30280"}
              }]
            }
            """)!;
        Assert.True(JsonNode.DeepEquals(expected, JsonNode.Parse(json)), json);
    }

    [Fact]
    public async Task RedownloadRequest_BindsAsAnAddTaskRequest()
    {
        var request = DownloadHistoryRequest.From(new ServeRequestOptions
        {
            Url = "https://www.bilibili.com/video/BV1t1YxzWEkz?vd_source=x&p=3",
            VideoOnly = true, SelectPage = "1,3", AudioLanguage = "en", SkipSubtitle = true, DownloadDanmaku = true,
            SkipAi = false, FilePattern = "<videoTitle>", Cookie = "SESSDATA=x", AccessToken = "y", UserAgent = "ua", WorkDir = "/tmp/x", Aria2cArgs = "-x"
        });
        var json = JsonSerializer.Serialize(request, AppJsonSerializerContext.Default.DownloadHistoryRequest);
        Assert.DoesNotContain("Cookie", json);
        Assert.DoesNotContain("AccessToken", json);
        Assert.DoesNotContain("WorkDir", json);
        Assert.DoesNotContain("UserAgent", json);
        Assert.DoesNotContain("Aria2c", json);

        var context = new DefaultHttpContext();
        context.Request.Body = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(json));
        context.Request.ContentType = "application/json";
        var binding = await MyOptionBindingResult<ServeRequestOptions>.BindAsync(context);

        Assert.True(binding.IsValid);
        var bound = binding.Result!;
        Assert.Equal("https://www.bilibili.com/video/BV1t1YxzWEkz?p=3", bound.Url);
        Assert.False(bound.UseAppApi);
        Assert.True(bound.VideoOnly);
        Assert.Equal("1,3", bound.SelectPage);
        Assert.Equal("en", bound.AudioLanguage);
        Assert.True(bound.SkipSubtitle);
        Assert.True(bound.DownloadDanmaku);
        Assert.False(bound.SkipAi);
        Assert.Equal("<videoTitle>", bound.FilePattern);
        Assert.Equal("", bound.Cookie);
        Assert.Null(new BBDownTApiServer(new BBDownTServerOptions { DownloadRoot = root, HistoryPath = historyPath }).ValidateAndNormalizeServerRequest(bound));
    }

    [Fact]
    public async Task RedownloadRequest_KeepsDownloadAllForASpaceTask()
    {
        var request = DownloadHistoryRequest.From(new ServeRequestOptions
        {
            Url = "https://space.bilibili.com/42?spm_id_from=333.1007", DownloadAll = true, DelayPerVideo = 3
        });
        Assert.Equal(new DownloadHistoryRequest { Url = "https://space.bilibili.com/42", DownloadAll = true, DelayPerVideo = 3 }, request);
        // 不下载全部投稿时投稿间隔没有意义，不保存
        Assert.Equal(new DownloadHistoryRequest { Url = "https://space.bilibili.com/42" },
            DownloadHistoryRequest.From(new ServeRequestOptions { Url = "https://space.bilibili.com/42", DelayPerVideo = 5 }));

        var json = JsonSerializer.Serialize(request, AppJsonSerializerContext.Default.DownloadHistoryRequest);
        var context = new DefaultHttpContext();
        context.Request.Body = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(json));
        context.Request.ContentType = "application/json";
        var binding = await MyOptionBindingResult<ServeRequestOptions>.BindAsync(context);

        Assert.True(binding.IsValid);
        Assert.True(binding.Result!.DownloadAll);
        Assert.Equal(3, binding.Result.DelayPerVideo);
        Assert.Null(new BBDownTApiServer(new BBDownTServerOptions { DownloadRoot = root, HistoryPath = historyPath }).ValidateAndNormalizeServerRequest(binding.Result));
    }

    [Fact]
    public void WebUi_HasTheHistoryTabAndTitleDisplay()
    {
        using var stream = typeof(BBDownTApiServer).Assembly.GetManifestResourceStream("BBDownT.WebUi.index.html")!;
        using var reader = new StreamReader(stream);
        var html = reader.ReadToEnd();

        Assert.Contains("<button class=\"tab\" data-tab=\"history\" type=\"button\">历史<span class=\"count\" id=\"historyCount\"></span></button>", html);
        Assert.Contains("<section id=\"historyPane\" hidden>", html);
        Assert.Contains("id=\"historySearch\"", html);
        Assert.Contains("id=\"clearHistory\"", html);
        Assert.Contains("api(`/history/?q=${encodeURIComponent(q)}&offset=${offset}&limit=${limit}`)", html);
        Assert.Contains("api('/history/' + encodeURIComponent(h.Id), { method: 'DELETE' })", html);
        Assert.Contains("api('/add-task', { method: 'POST', body: h.Request && h.Request.Url ? h.Request : { Url: h.Url } })", html);
        Assert.Contains("文件已删除", html);
        Assert.Contains("还没有下载历史", html);
        // 封面不带 Referer；标题链接在外部浏览器打开
        Assert.Contains("loading=\"lazy\" referrerpolicy=\"no-referrer\"", html);
        Assert.Contains("target=\"_blank\" rel=\"noopener noreferrer\"", html);
        // 删除记录、清空历史前确认
        Assert.Contains("confirm(`删除「${name}」的下载记录？已下载的文件不会被删除。`)", html);
        Assert.Contains("confirm(`清空全部 ${hist.total} 条下载历史？只删除记录，已下载的文件不会被删除。`)", html);
        // 已下载文件按视频分组、以视频标题为主(见 ApiServerWebUiTests)，任务卡片在标题获取前显示BV号或链接
        Assert.Contains("const title = esc(g.Title) + (g.TitlePending ?", html);
        Assert.Contains("const title = t.Title || shortLink(t.Url);", html);
        // 窄屏上徽标换到标题下一行
        Assert.Contains(".task.hist .task-head { flex-wrap: wrap;", html);
        Assert.Contains(".hist-badges { order: 1; flex-basis: 100%;", html);
        // 文件已存在而跳过的记录；文件还在时重新下载先确认
        Assert.Contains("已存在，未重新下载", html);
        Assert.Contains("的视频文件还在下载目录里，重新下载会直接跳过（不覆盖，也不换画质）", html);
        // 只给B站图床的封面加缩略图后缀，加载失败时换成图标
        Assert.Contains("host.endsWith('.hdslb.com') ? src + '@320w_200h_1c.webp' : src", html);
        Assert.Contains("failedCovers.add(img.getAttribute('src'));", html);
        Assert.DoesNotContain(".replace(/^http:/, 'https:') + '@320w_200h_1c.webp'", html);
        // 「加载更多」按 Id 去重
        Assert.Contains("res.Items.filter((h) => !seen.has(h.Id))", html);
    }
}
