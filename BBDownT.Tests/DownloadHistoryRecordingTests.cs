using BBDownT.Core.Entity;
using static BBDownT.Core.Entity.Entity;

namespace BBDownT.Tests;

/// <summary>
/// 任务结束时写入下载历史：成功、失败各记一条；只看信息的任务不记；UP主空间批量下载每个视频一条
/// </summary>
public class DownloadHistoryRecordingTests : IDisposable
{
    private const string VideoUrl = "https://www.bilibili.com/video/BV1t1YxzWEkz/?spm_id_from=333.788&vd_source=abc&p=2";
    private const string SpaceUrl = "https://space.bilibili.com/42";
    private readonly string temp = Path.Combine(Path.GetTempPath(), "bbdownt-history-rec-" + Guid.NewGuid().ToString("N"));
    private readonly string root;
    private readonly string historyPath;

    public DownloadHistoryRecordingTests()
    {
        root = Path.Combine(temp, "downloads");
        historyPath = Path.Combine(temp, "data", DownloadHistory.FileName);
        Directory.CreateDirectory(root);
    }

    public void Dispose()
    {
        Directory.Delete(temp, true);
    }

    private BBDownTApiServer CreateServer(Func<ServeRequestOptions, DownloadTask, Task> work) =>
        new(new BBDownTServerOptions { DownloadRoot = root, HistoryPath = historyPath }) { WorkRunner = work };

    private string WriteFile(string relative, int size = 3)
    {
        var path = Path.Combine(root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[size]);
        return path;
    }

    private static VInfo Video(string title, string owner, params (int Index, string Title)[] pages) => new()
    {
        Title = title,
        Desc = "",
        Pic = "http://i0.hdslb.com/bfs/archive/cover.jpg",
        PubTime = 0,
        PagesInfo = pages.Select(p => new Page(p.Index, "170001", "100" + p.Index, "", p.Title, 60, "", 0) { ownerName = owner }).ToList()
    };

    private static async Task<DownloadTask> Run(BBDownTApiServer server, ServeRequestOptions request)
    {
        var task = new DownloadTask("", request.Url, 1);
        return await server.AddDownloadTaskAsync(new QueuedDownloadTask(request, task));
    }

    [Fact]
    public async Task SuccessfulVideo_RecordsTitleOwnerPagesStreamsFilesAndRedownloadOptions()
    {
        var video = WriteFile("视频标题/[P2]第二P.mp4", 5);
        var subtitle = WriteFile("视频标题/[P2]第二P.zh-Hans.srt", 2);
        var outside = Path.Combine(temp, "outside.mp4");
        File.WriteAllText(outside, "x");
        var server = CreateServer((option, task) => Program.ExecuteWorkAsync(option, task, prepare: _ =>
            Task.FromResult(new PreparedVideoDownload("170001", Video("视频标题", "某UP主", (1, "第一P"), (2, "第二P")), related =>
            {
                related!.SetStream("170001/1002/2", "P2 · TV · 4K 超清 3840x2160 HEVC · 192K M4A", ["4K 超清", "HEVC", "192K"]);
                related.AddPage(2, "第二P");
                related.AddSavePath(video);
                related.AddSavePath(subtitle);
                related.AddSavePath(video);
                related.AddSavePath(outside);
                return Task.CompletedTask;
            }, "TV"))));

        var task = await Run(server, new ServeRequestOptions
        {
            Url = VideoUrl, UseTvApi = true, DfnPriority = "4K 超清", EncodingPriority = "hevc", VideoStream = "120:HEVC", AudioStream = "30280",
            Cookie = "SESSDATA=secret", AccessToken = "token-secret", SkipCover = true
        });

        Assert.True(task.IsSuccessful);
        var entry = Assert.Single(server.History.Snapshot());
        Assert.Equal(task.TaskId, entry.TaskId);
        Assert.True(entry.Success);
        Assert.Null(entry.Error);
        Assert.Equal("https://www.bilibili.com/video/BV1t1YxzWEkz/?p=2", entry.Url);
        Assert.Equal(entry.Url, entry.PageUrl);
        Assert.Equal("video", entry.Kind);
        Assert.Equal("170001", entry.Aid);
        Assert.Equal(BBDownT.Core.Util.BilibiliBvConverter.Encode(170001), entry.Bvid);
        Assert.Equal("视频标题", entry.Title);
        Assert.Equal("某UP主", entry.Owner);
        Assert.Equal("http://i0.hdslb.com/bfs/archive/cover.jpg", entry.Pic);
        Assert.Equal("TV", entry.Api);
        Assert.Equal([new DownloadHistoryPage(2, "第二P")], entry.Pages);
        Assert.Equal(["P2 · TV · 4K 超清 3840x2160 HEVC · 192K M4A"], entry.Streams);
        Assert.Equal(["4K 超清", "HEVC", "192K"], entry.StreamTags);
        // 只记录下载根目录内的文件(相对路径、去重)，outside 无法通过文件接口访问
        Assert.Equal([new DownloadHistoryFile("视频标题/[P2]第二P.mp4", 5, true), new DownloadHistoryFile("视频标题/[P2]第二P.zh-Hans.srt", 2, true)], entry.Files);
        Assert.Equal(7, entry.TotalBytes);
        Assert.Equal(new DownloadHistoryRequest
        {
            Url = "https://www.bilibili.com/video/BV1t1YxzWEkz/?p=2", UseTvApi = true, DfnPriority = "4K 超清", EncodingPriority = "hevc",
            VideoStream = "120:HEVC", AudioStream = "30280", SkipCover = true
        }, entry.Request);
        var json = File.ReadAllText(historyPath);
        Assert.DoesNotContain("secret", json);
        Assert.DoesNotContain("Cookie", json);
        Assert.DoesNotContain("AccessToken", json);
    }

    [Fact]
    public async Task FailedVideo_RecordsARedactedShortError()
    {
        var server = CreateServer((option, task) => Program.ExecuteWorkAsync(option, task, prepare: _ =>
            Task.FromResult(new PreparedVideoDownload("170001", Video("会失败的视频", "UP", (1, "P1")), related =>
            {
                related!.AddPage(1, "P1");
                throw new InvalidOperationException("请求失败 SESSDATA=abcdef; " + new string('长', 400));
            }))));

        var task = await Run(server, new ServeRequestOptions { Url = "BV1t1YxzWEkz" });

        Assert.False(task.IsSuccessful);
        var entry = Assert.Single(server.History.Snapshot());
        Assert.False(entry.Success);
        Assert.Equal("会失败的视频", entry.Title);
        Assert.StartsWith("请求失败 SESSDATA=<redacted>;", entry.Error);
        Assert.DoesNotContain("abcdef", entry.Error);
        Assert.True(entry.Error!.Length <= DownloadHistory.MaxErrorLength);
        Assert.Equal("BV1t1YxzWEkz", entry.Url);
        // 不是链接时用解析出的AID对应的BV号拼出B站页面
        Assert.Equal($"https://www.bilibili.com/video/{BBDownT.Core.Util.BilibiliBvConverter.Encode(170001)}/", entry.PageUrl);
        Assert.Equal([new DownloadHistoryPage(1, "P1")], entry.Pages);
    }

    [Fact]
    public async Task VideoWhoseFilesAlreadyExisted_IsMarkedSkippedWithoutTheNewlySelectedStream()
    {
        var existing = WriteFile("已有的视频.mp4", 9);
        var server = CreateServer((option, task) => Program.ExecuteWorkAsync(option, task, prepare: _ =>
            Task.FromResult(new PreparedVideoDownload("170001", Video("已有的视频", "UP", (1, "P1")), related =>
            {
                // 与 Program.DownloadPageAsync 相同：先记下选中的流，发现文件已存在后跳过
                related!.SetStream("170001/1001/1", "P1 · WEB · 4K 超清 3840x2160 HEVC · 192K M4A", ["4K 超清", "HEVC", "192K"]);
                related.AddSavePath(existing);
                related.AddPage(1, "P1", alreadyExisted: true);
                return Task.CompletedTask;
            }, "WEB"))));

        var task = await Run(server, new ServeRequestOptions { Url = "BV1t1YxzWEkz" });

        Assert.True(task.IsSuccessful);
        var entry = Assert.Single(server.History.Snapshot());
        Assert.True(entry.Success);
        Assert.True(entry.Skipped);
        Assert.Empty(entry.Streams);
        Assert.Empty(entry.StreamTags);
        Assert.Equal([new DownloadHistoryFile("已有的视频.mp4", 9, true)], entry.Files);
    }

    [Fact]
    public async Task VideoWithSomePagesDownloaded_IsNotSkipped()
    {
        var server = CreateServer((option, task) => Program.ExecuteWorkAsync(option, task, prepare: _ =>
            Task.FromResult(new PreparedVideoDownload("170001", Video("多P", "UP", (1, "P1"), (2, "P2")), related =>
            {
                related!.AddPage(1, "P1", alreadyExisted: true);
                related.SetStream("170001/1002/2", "P2 · WEB · 1080P 高清 1920x1080 AVC · 192K M4A", ["1080P 高清", "AVC", "192K"]);
                related.AddPage(2, "P2");
                return Task.CompletedTask;
            }, "WEB"))));

        await Run(server, new ServeRequestOptions { Url = "BV1t1YxzWEkz", SelectPage = "ALL" });

        var entry = Assert.Single(server.History.Snapshot());
        Assert.False(entry.Skipped);
        Assert.Equal(["1080P 高清", "AVC", "192K"], entry.StreamTags);
    }

    [Fact]
    public async Task LinkCredentials_AreDroppedFromTheEntryAndTheRedownloadRequest()
    {
        var server = CreateServer((_, _) => throw new InvalidOperationException("失败"));

        await Run(server, new ServeRequestOptions { Url = "https://www.bilibili.com/video/BV1t1YxzWEkz/?access_key=key-secret&p=2&Access_Token=tok-secret" });

        var entry = Assert.Single(server.History.Snapshot());
        Assert.Equal("https://www.bilibili.com/video/BV1t1YxzWEkz/?p=2", entry.Url);
        Assert.Equal(entry.Url, entry.PageUrl);
        Assert.Equal(entry.Url, entry.Request.Url);
        Assert.DoesNotContain("secret", File.ReadAllText(historyPath));
    }

    [Fact]
    public async Task SystemErrors_AreDescribedInChineseWithPathsRelativeToTheDownloadRoot()
    {
        var denied = Path.Combine(root, "117359780042269");
        var server = CreateServer((option, task) => Program.ExecuteWorkAsync(option, task, prepare: _ =>
            Task.FromResult(new PreparedVideoDownload("170001", Video("没有权限", "UP", (1, "P1")),
                _ => throw new UnauthorizedAccessException($"Access to the path '{denied}' is denied.")))));

        await Run(server, new ServeRequestOptions { Url = "BV1t1YxzWEkz" });

        Assert.Equal("没有写入权限：117359780042269", Assert.Single(server.History.Snapshot()).Error);
    }

    [Fact]
    public async Task SpaceDownloadAllThatFailsBeforeAnyVideo_KeepsDownloadAllForRedownload()
    {
        var server = CreateServer((option, task) => Program.ExecuteWorkAsync(option, task, prepare: _ =>
            Task.FromResult(new PreparedVideoDownload("mid:42", new SpaceVideoInfo
            {
                Title = "UP的投稿", Desc = "", Pic = "", PubTime = 0, PagesInfo = [], UrlListFilePath = Path.Combine(root, "没有生成.txt")
            }, _ => throw new InvalidOperationException("不应下载清单本身")))));

        var task = await Run(server, new ServeRequestOptions { Url = SpaceUrl, DownloadAll = true, DelayPerVideo = 4 });

        Assert.False(task.IsSuccessful);
        var entry = Assert.Single(server.History.Snapshot());
        Assert.Equal("space", entry.Kind);
        Assert.Equal(new DownloadHistoryRequest { Url = SpaceUrl, DownloadAll = true, DelayPerVideo = 4 }, entry.Request);
    }

    [Theory]
    [InlineData("Arg_KeyNotFound", "视频不存在或无法获取信息（可能已删除、需要登录或有地区限制）")]
    [InlineData("Arg_KeyNotFoundWithKey, data", "视频不存在或无法获取信息（可能已删除、需要登录或有地区限制）")]
    [InlineData("The given key 'data' was not present in the dictionary.", "视频不存在或无法获取信息（可能已删除、需要登录或有地区限制）")]
    [InlineData("UnauthorizedAccess_IODenied_Path, 117359780042269", "没有写入权限：117359780042269")]
    [InlineData("UnauthorizedAccess_IODenied_NoPathName", "没有写入权限")]
    [InlineData("Access to the path '/srv/x.mp4' is denied.", "没有写入权限：/srv/x.mp4")]
    [InlineData("IO_FileNotFound_FileName, a/b.mp4", "找不到文件：a/b.mp4")]
    [InlineData("Could not find file 'a.m4s'.", "找不到文件：a.m4s")]
    [InlineData("IO_PathNotFound_Path, 视频/P1", "找不到目录：视频/P1")]
    [InlineData("Could not find a part of the path '/a/b'.", "找不到目录：/a/b")]
    [InlineData("No space left on device : '/a/b.mp4'", "磁盘空间不足")]
    [InlineData("net_http_request_timedout, 100", "网络请求超时")]
    [InlineData("The request was canceled due to the configured HttpClient.Timeout of 100 seconds elapsing.", "网络请求超时")]
    [InlineData("net_http_message_not_success_statuscode_reason, 404, Not Found", "网络请求失败（HTTP 404）")]
    [InlineData("Response status code does not indicate success: 403 (Forbidden).", "网络请求失败（HTTP 403）")]
    [InlineData("P1 下载失败", "P1 下载失败")]
    [InlineData("请求失败：Access to the path '/a' is denied.", "请求失败：Access to the path '/a' is denied.")]
    public void ShortError_DescribesCommonSystemErrors(string error, string expected)
    {
        Assert.Equal(expected, DownloadHistory.ShortError(error));
    }

    [Fact]
    public void RelativizeDownloadPaths_OnlyRewritesPathsUnderTheDownloadRoot()
    {
        var server = CreateServer((_, _) => Task.CompletedTask);
        var real = BBDownTApiServer.ResolveRealPath(root);

        Assert.Equal("UnauthorizedAccess_IODenied_Path, 117359780042269",
            server.RelativizeDownloadPaths($"UnauthorizedAccess_IODenied_Path, {Path.Combine(root, "117359780042269")}"));
        // 任务里的路径可能是解析过符号链接的真实路径(macOS 的临时目录在 /var -> /private/var 下)
        Assert.Equal("Could not find file '视频/a.mp4'.", server.RelativizeDownloadPaths($"Could not find file '{Path.Combine(real, "视频", "a.mp4")}'."));
        Assert.Equal("Access to the path '下载目录' is denied.", server.RelativizeDownloadPaths($"Access to the path '{root}' is denied."));
        // 其他目录、只是前缀相同的目录不改
        Assert.Equal($"'{root}2/a.mp4'", server.RelativizeDownloadPaths($"'{root}2/a.mp4'"));
        Assert.Equal($"/other{root}/a.mp4", server.RelativizeDownloadPaths($"/other{root}/a.mp4"));
    }

    [Fact]
    public async Task FailureBeforeParsing_IsRecordedForTheWholeTask()
    {
        var server = CreateServer((_, _) => throw new HttpRequestException("网络错误 access_token=abc"));

        var task = await Run(server, new ServeRequestOptions { Url = VideoUrl, AudioOnly = true });

        var entry = Assert.Single(server.History.Snapshot());
        Assert.False(entry.Success);
        Assert.Null(entry.Title);
        Assert.Equal("网络错误 access_token=<redacted>", entry.Error);
        Assert.Equal("https://www.bilibili.com/video/BV1t1YxzWEkz/?p=2", entry.Url);
        Assert.Equal("BV1t1YxzWEkz", entry.Bvid);
        Assert.Equal(task.TaskId, entry.TaskId);
        Assert.Equal(new DownloadHistoryRequest { Url = entry.Url, AudioOnly = true }, entry.Request);
    }

    [Fact]
    public async Task OnlyShowInfoTasks_AreNotRecorded()
    {
        var calls = 0;
        var server = CreateServer((option, task) => Program.ExecuteWorkAsync(option, task, prepare: _ =>
            Task.FromResult(new PreparedVideoDownload("170001", Video("只看信息", "UP", (1, "P1")), _ =>
            {
                calls++;
                return Task.CompletedTask;
            }))));

        await Run(server, new ServeRequestOptions { Url = "BV1t1YxzWEkz", OnlyShowInfo = true });
        await Run(server, new ServeRequestOptions { Url = "https://www.bilibili.com/video/av9", OnlyShowInfo = true });

        Assert.Equal(2, calls);
        Assert.Equal(0, server.History.Count);
        Assert.False(File.Exists(historyPath));
    }

    [Fact]
    public async Task SpaceBatch_RecordsOneEntryPerVideo()
    {
        var list = WriteFile("UP的投稿.txt");
        File.WriteAllLines(list, ["https://www.bilibili.com/video/av1", "https://www.bilibili.com/video/av2", "https://www.bilibili.com/video/av3"]);
        var first = WriteFile("第一个/第一个.mp4", 4);
        var server = CreateServer((option, task) => Program.ExecuteWorkAsync(option, task, prepare: input => input.Url switch
        {
            SpaceUrl => Task.FromResult(new PreparedVideoDownload("mid:42", new SpaceVideoInfo
            {
                Title = "UP的投稿", Desc = "", Pic = "", PubTime = 0, PagesInfo = [], UrlListFilePath = list
            }, _ => throw new InvalidOperationException("不应下载清单本身"))),
            "https://www.bilibili.com/video/av1" => Task.FromResult(new PreparedVideoDownload("1", Video("第一个", "UP", (1, "P1")), related =>
            {
                related!.SetStream("1/1001/1", "P1 · WEB · 1080P 高清 1920x1080 AVC · 192K M4A", ["1080P 高清", "AVC", "192K"]);
                related.AddPage(1, "P1");
                related.AddSavePath(first);
                return Task.CompletedTask;
            }, "WEB")),
            "https://www.bilibili.com/video/av2" => throw new IOException("第二个解析失败"),
            _ => Task.FromResult(new PreparedVideoDownload("3", Video("第三个", "UP", (1, "P1")), _ => throw new InvalidOperationException("P1 下载失败"), "WEB"))
        }));

        var task = await Run(server, new ServeRequestOptions { Url = SpaceUrl, DownloadAll = true, DelayPerVideo = 0, DfnPriority = "1080P 高清" });

        Assert.False(task.IsSuccessful); // 有失败的投稿时整个任务失败
        var entries = server.History.Snapshot();
        Assert.Equal(["https://www.bilibili.com/video/av3", "https://www.bilibili.com/video/av2", "https://www.bilibili.com/video/av1"], entries.Select(e => e.Url));
        var (third, second, firstEntry) = (entries[0], entries[1], entries[2]);

        Assert.True(firstEntry.Success);
        Assert.Equal("第一个", firstEntry.Title);
        Assert.Equal("1", firstEntry.Aid);
        Assert.Equal(BBDownT.Core.Util.BilibiliBvConverter.Encode(1), firstEntry.Bvid);
        // 投稿清单TXT属于整个任务，不算在某个视频里
        Assert.Equal([new DownloadHistoryFile("第一个/第一个.mp4", 4, true)], firstEntry.Files);
        Assert.Equal(["1080P 高清", "AVC", "192K"], firstEntry.StreamTags);
        Assert.Equal(new DownloadHistoryRequest { Url = "https://www.bilibili.com/video/av1", DfnPriority = "1080P 高清" }, firstEntry.Request);

        Assert.False(second.Success);
        Assert.Null(second.Title);
        Assert.Equal("第二个解析失败", second.Error);
        Assert.Equal("2", second.Aid);
        Assert.Empty(second.Files);

        Assert.False(third.Success);
        Assert.Equal("第三个", third.Title);
        Assert.Equal("P1 下载失败", third.Error);
        Assert.All(entries, e => Assert.Equal(task.TaskId, e.TaskId));
    }

    [Fact]
    public async Task SpaceExportOnly_RecordsTheTaskWithItsListFile()
    {
        var list = WriteFile("UP的投稿.txt");
        var server = CreateServer((option, task) => Program.ExecuteWorkAsync(option, task, prepare: _ =>
            Task.FromResult(new PreparedVideoDownload("mid:42", new SpaceVideoInfo
            {
                Title = "UP的投稿", Desc = "", Pic = "", PubTime = 0, PagesInfo = [], UrlListFilePath = list
            }, _ => throw new InvalidOperationException("只导出")))));

        await Run(server, new ServeRequestOptions { Url = SpaceUrl });

        var entry = Assert.Single(server.History.Snapshot());
        Assert.True(entry.Success);
        Assert.Equal("space", entry.Kind);
        Assert.Equal("UP的投稿", entry.Title);
        Assert.Equal(SpaceUrl, entry.PageUrl);
        Assert.Equal(["UP的投稿.txt"], entry.Files.Select(f => f.Path));
    }

    [Fact]
    public void CliWorkWithoutATask_DoesNotNeedHistory()
    {
        var task = new DownloadTask("", VideoUrl, 1);
        // 没有设置回调时开始/结束视频记录不会出错
        var video = task.BeginVideo(VideoUrl, DownloadHistoryRequest.From(new MyOption { Url = VideoUrl }));
        task.AddSavePath(Path.Combine(root, "a.mp4"));
        task.FinishVideo(video, true, null);
        task.FinishVideo(video, false, "重复结束会被忽略");

        Assert.True(task.HasVideoRecords);
        Assert.Equal([Path.Combine(root, "a.mp4")], task.CreateSnapshot().SavePaths);
    }

    [Fact]
    public void RelativePaths_FollowASymlinkedDownloadRoot()
    {
        if (OperatingSystem.IsWindows()) return; // 创建符号链接需要额外权限
        var real = Path.Combine(temp, "real");
        Directory.CreateDirectory(Path.Combine(real, "sub"));
        var link = Path.Combine(temp, "link");
        Directory.CreateSymbolicLink(link, real);
        File.WriteAllText(Path.Combine(real, "sub", "a.mp4"), "x");
        var server = new BBDownTApiServer(new BBDownTServerOptions { DownloadRoot = link, HistoryPath = historyPath });

        // 任务执行时工作目录取自系统，是解析过符号链接的真实路径(macOS 的临时目录本身也在符号链接 /var 下)
        Assert.Equal("sub/a.mp4", server.ToDownloadRelativePath(Path.Combine(BBDownTApiServer.ResolveRealPath(real), "sub", "a.mp4")));
        Assert.Equal("sub/a.mp4", server.ToDownloadRelativePath(Path.Combine(real, "sub", "a.mp4")));
        Assert.Equal("sub/a.mp4", server.ToDownloadRelativePath(Path.Combine(link, "sub", "a.mp4")));
        Assert.Null(server.ToDownloadRelativePath(Path.Combine(temp, "outside.mp4")));
        Assert.Null(server.ToDownloadRelativePath(link));
    }

    [Theory]
    [InlineData("170001", "https://www.bilibili.com/video/av170001", "170001", true, null)]
    [InlineData("ep:123", "https://www.bilibili.com/bangumi/play/ep123", null, false, "123")]
    [InlineData("cheese:77", "cheese/ep77", null, false, "77")]
    [InlineData(null, "https://www.bilibili.com/video/BV1t1YxzWEkz/", null, true, null)]
    [InlineData(null, "av42", "42", true, null)]
    [InlineData(null, "ep9", null, false, "9")]
    [InlineData("mid:42", "https://space.bilibili.com/42", null, false, null)]
    public void Identify_FindsAidBvidOrEp(string? inputId, string url, string? aid, bool hasBvid, string? ep)
    {
        var result = DownloadHistory.Identify(inputId, url);

        Assert.Equal(aid, result.Aid);
        Assert.Equal(hasBvid, result.Bvid is not null);
        Assert.Equal(ep, result.Ep);
    }
}
