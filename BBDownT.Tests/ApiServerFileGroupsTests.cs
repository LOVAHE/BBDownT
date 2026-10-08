using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace BBDownT.Tests;

/// <summary>
/// GET/DELETE /files/groups：按视频分组列出、按av号查标题(不联网，注入查询函数)、整组删除只删这一组且不越出下载根目录
/// </summary>
public class ApiServerFileGroupsTests : IDisposable
{
    private readonly string temp = Path.Combine(Path.GetTempPath(), "bbdownt-groups-api-" + Guid.NewGuid().ToString("N"));
    private readonly string root;
    private readonly string historyPath;

    public ApiServerFileGroupsTests()
    {
        root = Path.Combine(temp, "downloads");
        historyPath = Path.Combine(temp, "data", DownloadHistory.FileName);
        Directory.CreateDirectory(root);
    }

    public void Dispose()
    {
        foreach (var link in Directory.EnumerateFileSystemEntries(temp, "*", SearchOption.AllDirectories).Where(p => new FileInfo(p).LinkTarget is not null).ToList())
        {
            if (Directory.Exists(link)) Directory.Delete(link);
            else File.Delete(link);
        }
        Directory.Delete(temp, true);
    }

    private BBDownTApiServer CreateServer(Func<string, CancellationToken, Task<VideoTitleInfo?>>? fetch = null) =>
        new(new BBDownTServerOptions { DownloadRoot = root, HistoryPath = historyPath })
        {
            Titles = new VideoTitleResolver(fetch ?? ((_, _) => Task.FromResult<VideoTitleInfo?>(null))),
            TitleLookupWait = TimeSpan.FromSeconds(5),
        };

    private string Write(string relative, string content = "x", DateTime? time = null)
    {
        var path = Path.Combine(root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        if (time is not null) File.SetLastWriteTimeUtc(path, time.Value);
        return path;
    }

    /// <summary>
    /// 旧版本留下的中断下载：分片、续传状态、封面，没有说明文件
    /// </summary>
    private void LegacyWorkFolder(string aid = "115050127886063")
    {
        Write($"{aid}/00000_{aid}.P1.31783979182.vclip", "complete");
        Write($"{aid}/00001_{aid}.P1.31783979182.vclip", "part");
        Write($"{aid}/00001_{aid}.P1.31783979182.vclip.resume", "x");
        Write($"{aid}/{aid}.jpg", "jpg");
    }

    private async Task<(BBDownTApiServer Server, string Origin)> StartAsync(BBDownTApiServer server, string? token = null)
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        server.SetUpServer(new BBDownTServerOptions { DownloadRoot = root, HistoryPath = historyPath });
        await server.StartWithoutQueueAsync($"http://127.0.0.1:{port}", token);
        return (server, $"http://127.0.0.1:{port}");
    }

    private static HttpClient CreateClient() =>
        new(new HttpClientHandler { UseCookies = false, UseProxy = false }) { Timeout = TimeSpan.FromSeconds(10) };

    [Fact]
    public async Task LegacyFolder_TitleIsLookedUpOnceAndSavedIntoTheFolder()
    {
        LegacyWorkFolder();
        var calls = 0;
        var server = CreateServer((aid, _) =>
        {
            Interlocked.Increment(ref calls);
            return Task.FromResult<VideoTitleInfo?>(new VideoTitleInfo("4K 城市夜景", "风景UP", "https://i0.hdslb.com/c.jpg", "BV1t1YxzWEkz"));
        });

        var list = await server.ListFileGroupsAsync();

        var group = Assert.Single(list.Groups);
        Assert.Equal("4K 城市夜景", group.Title);
        Assert.Equal("风景UP", group.Owner);
        Assert.False(group.TitlePending);
        Assert.Equal("115050127886063/115050127886063.jpg", group.CoverFile);
        Assert.Null(group.Request);
        Assert.Equal(1, calls);
        // 查到的标题补写进文件夹的说明文件(没有继续下载用的请求)
        var metadataPath = Path.Combine(root, "115050127886063", DownloadWorkFolder.MetadataFileName);
        var sw = Stopwatch.StartNew();
        while (!File.Exists(metadataPath) && sw.Elapsed < TimeSpan.FromSeconds(5)) await Task.Delay(20);
        var saved = DownloadWorkFolder.Read(Path.Combine(root, "115050127886063"))!;
        Assert.Equal(("4K 城市夜景", DownloadWorkFolder.SourceLookup, null), (saved.Title, saved.Source, saved.Request));

        // 之后(包括重启后)直接读说明文件，不再查询
        await server.ListFileGroupsAsync();
        var restarted = CreateServer((_, _) => throw new InvalidOperationException("不应再查询"));
        Assert.Equal("4K 城市夜景", Assert.Single((await restarted.ListFileGroupsAsync()).Groups).Title);
        Assert.Equal(1, calls);
        // 说明文件不出现在文件列表里
        Assert.DoesNotContain(list.Groups.SelectMany(g => g.Files), f => f.Path.Contains(DownloadWorkFolder.MetadataFileName));
    }

    [Fact]
    public async Task SlowTitleLookup_DoesNotBlockTheListAndFillsInLater()
    {
        LegacyWorkFolder();
        var release = new TaskCompletionSource<VideoTitleInfo?>();
        var server = CreateServer((_, _) => release.Task);
        server.TitleLookupWait = TimeSpan.FromMilliseconds(100);

        var sw = Stopwatch.StartNew();
        var first = Assert.Single((await server.ListFileGroupsAsync()).Groups);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(3), sw.Elapsed.ToString());
        Assert.Equal("未完成的下载（av115050127886063）", first.Title);
        Assert.True(first.TitlePending);

        // 仍在查询时再次列出：不重复查询，也不再等待
        sw.Restart();
        Assert.True(Assert.Single((await server.ListFileGroupsAsync()).Groups).TitlePending);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(1), sw.Elapsed.ToString());

        release.SetResult(new VideoTitleInfo("终于查到", null, null, null));
        sw.Restart();
        while (server.Titles.IsPending("115050127886063") && sw.Elapsed < TimeSpan.FromSeconds(5)) await Task.Delay(20);
        var later = Assert.Single((await server.ListFileGroupsAsync()).Groups);
        Assert.Equal("终于查到", later.Title);
        Assert.False(later.TitlePending);
    }

    [Fact]
    public async Task FailedTitleLookup_ShowsTheFallbackAndIsNotRetriedImmediately()
    {
        LegacyWorkFolder();
        var calls = 0;
        var server = CreateServer((_, _) =>
        {
            Interlocked.Increment(ref calls);
            throw new HttpRequestException("network down");
        });

        var group = Assert.Single((await server.ListFileGroupsAsync()).Groups);
        Assert.Equal("未完成的下载（av115050127886063）", group.Title);
        Assert.False(group.TitlePending);
        await server.ListFileGroupsAsync();

        Assert.Equal(1, calls);
        Assert.False(File.Exists(Path.Combine(root, "115050127886063", DownloadWorkFolder.MetadataFileName)));
    }

    [Theory]
    [InlineData("""{"code":0,"data":{"title":" 标题 ","pic":"http://i0.hdslb.com/a.jpg","bvid":"BV1t1YxzWEkz","owner":{"name":"UP"}}}""", "标题", "UP", "BV1t1YxzWEkz", false)]
    [InlineData("""{"code":-404,"message":"啥都木有"}""", "", null, null, true)]
    [InlineData("""{"code":62002,"message":"稿件不可见"}""", "", null, null, true)]
    [InlineData("""{"code":-412,"data":null}""", null, null, null, false)]
    [InlineData("""{"code":0,"data":{"title":""}}""", null, null, null, false)]
    [InlineData("not json", null, null, null, false)]
    public void ViewApiResponse(string json, string? title, string? owner, string? bvid, bool gone)
    {
        var info = VideoTitleResolver.ParseViewResponse(json);
        Assert.Equal(title, info?.Title);
        Assert.Equal(owner, info?.Owner);
        Assert.Equal(bvid, info?.Bvid);
        Assert.Equal(gone, info?.Gone == true);
    }

    [Fact]
    public async Task VideoThatNoLongerExists_IsMarkedUnavailableAndNothingIsWrittenIntoTheFolder()
    {
        LegacyWorkFolder("999999999999999");
        var calls = 0;
        var server = CreateServer((_, _) =>
        {
            Interlocked.Increment(ref calls);
            return Task.FromResult(VideoTitleResolver.ParseViewResponse("""{"code":-404,"message":"啥都木有"}"""));
        });

        var group = Assert.Single((await server.ListFileGroupsAsync()).Groups);
        await server.ListFileGroupsAsync();

        Assert.True(group.Unavailable);
        Assert.Null(group.Url);
        Assert.Equal(1, calls);
        await Task.Delay(100);
        Assert.False(File.Exists(Path.Combine(root, "999999999999999", DownloadWorkFolder.MetadataFileName)));
    }

    [Fact]
    public async Task NumericFolderThatIsNotAWorkFolder_IsNotLookedUpOrWrittenInto()
    {
        Write("2024/00000_115050127886063.P1.31783979182.vclip", "clip");
        Write("2024/年度总结.mp4", "video");
        var server = CreateServer((_, _) => throw new InvalidOperationException("不应查询"));

        var groups = (await server.ListFileGroupsAsync()).Groups;

        Assert.DoesNotContain(groups, g => g.Status == FileGroupStatus.Incomplete);
        Assert.False(File.Exists(Path.Combine(root, "2024", DownloadWorkFolder.MetadataFileName)));
    }

    [Fact]
    public async Task GroupsEndpoint_ReturnsTheGroupedJsonContract()
    {
        Write("视频.mp4", "video", DateTime.UtcNow.AddHours(-1));
        Write("视频.zh-CN.srt", "srt", DateTime.UtcNow.AddHours(-1));
        LegacyWorkFolder();
        var (server, origin) = await StartAsync(CreateServer((_, _) => Task.FromResult<VideoTitleInfo?>(new VideoTitleInfo("标题", null, null, "BV1t1YxzWEkz"))));
        try
        {
            using var client = CreateClient();
            using var response = await client.GetAsync(origin + "/files/groups");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var json = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
            Assert.Equal(2, json["Total"]!.GetValue<int>());
            Assert.Equal(6, json["TotalFiles"]!.GetValue<int>());
            Assert.False(json["Truncated"]!.GetValue<bool>());
            var work = json["Groups"]![0]!;
            Assert.Equal("w:115050127886063", work["Id"]!.GetValue<string>());
            Assert.Equal("incomplete", work["Status"]!.GetValue<string>());
            Assert.Equal(2, work["ClipCount"]!.GetValue<int>());
            Assert.Equal("video", work["Clips"]![0]!["Track"]!.GetValue<string>());
            var names = work.AsObject().Select(p => p.Key).ToList();
            Assert.Equal(["Id", "Status", "Source", "Title", "TitlePending", "Unavailable", "Pic", "CoverFile", "Owner", "Aid", "Bvid", "PageUrl", "Url",
                "FinishedAt", "ModifiedTime", "TotalBytes", "FileCount", "MainFile", "Folder", "Files", "ClipCount", "CompleteClipCount",
                "Clips", "Active", "Request"], names);
            var video = json["Groups"]![1]!;
            Assert.Equal("s:视频", video["Id"]!.GetValue<string>());
            Assert.Equal("视频.mp4", video["MainFile"]!.GetValue<string>());
            Assert.Equal(2, video["Files"]!.AsArray().Count);
            Assert.Equal(["Path", "Size", "ModifiedTime", "Title"], video["Files"]![0]!.AsObject().Select(p => p.Key));

            // 原来的 /files/ 不变：仍是平铺的文件列表(不含说明文件)
            using var flat = await client.GetAsync(origin + "/files/");
            var files = JsonNode.Parse(await flat.Content.ReadAsStringAsync())!.AsArray();
            Assert.Equal(6, files.Count);
        }
        finally { await server.StopAsync(); }
    }

    [Fact]
    public async Task GroupsEndpoint_HasTheSameTokenAndOriginRulesAsFiles()
    {
        LegacyWorkFolder();
        const string token = "test-token-0123456789";
        var (server, origin) = await StartAsync(CreateServer(), token);
        try
        {
            using var client = CreateClient();
            using (var anonymous = await client.GetAsync(origin + "/files/groups"))
                Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
            using (var delete = await client.DeleteAsync(origin + "/files/groups?id=w:115050127886063"))
                Assert.Equal(HttpStatusCode.Unauthorized, delete.StatusCode);
            Assert.True(Directory.Exists(Path.Combine(root, "115050127886063")));
            var authorized = new HttpRequestMessage(HttpMethod.Get, origin + "/files/groups");
            authorized.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token);
            using (var ok = await client.SendAsync(authorized))
                Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        }
        finally { await server.StopAsync(); }

        var (open, openOrigin) = await StartAsync(CreateServer());
        try
        {
            using var client = CreateClient();
            var cross = new HttpRequestMessage(HttpMethod.Delete, openOrigin + "/files/groups?id=w:115050127886063");
            cross.Headers.TryAddWithoutValidation("Origin", "http://evil.example");
            using (var response = await client.SendAsync(cross))
                Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            Assert.True(Directory.Exists(Path.Combine(root, "115050127886063")));
        }
        finally { await open.StopAsync(); }
    }

    [Fact]
    public async Task DeleteIncompleteGroup_RemovesTheWholeTempFolderOnly()
    {
        LegacyWorkFolder();
        DownloadWorkFolder.TryCreate(Path.Combine(root, "115050127886063"), new DownloadWorkMetadata { Title = "x" });
        Write("115050127886063/.DS_Store", "finder");
        var other = Write("其他.mp4");
        var (server, origin) = await StartAsync(CreateServer());
        try
        {
            using var client = CreateClient();
            using var response = await client.DeleteAsync(origin + "/files/groups?id=" + Uri.EscapeDataString("w:115050127886063"));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var result = JsonSerializer.Deserialize(await response.Content.ReadAsStringAsync(), AppJsonSerializerContext.Default.FileGroupDeleteResult)!;
            // 2个分片、1个续传状态、封面和说明文件
            Assert.Equal(new FileGroupDeleteResult(5, 0), result);
            Assert.False(Directory.Exists(Path.Combine(root, "115050127886063")));
            Assert.True(File.Exists(other));
            Assert.True(Directory.Exists(root));

            using var again = await client.DeleteAsync(origin + "/files/groups?id=" + Uri.EscapeDataString("w:115050127886063"));
            Assert.Equal(HttpStatusCode.NotFound, again.StatusCode);
        }
        finally { await server.StopAsync(); }
    }

    [Fact]
    public void DeleteCompleteGroup_RemovesOnlyItsFilesAndTheEmptyFolder()
    {
        Write("合集/[P01]开场.mp4");
        Write("合集/[P02]正片.mp4");
        Write("合集/[P02]正片.ass");
        var keep = Write("合集外/别的.mp4");
        var server = CreateServer();
        var group = server.BuildFileGroups(null).Groups.Single(g => g.Id == "d:合集");

        var result = server.DeleteFileGroup(group);

        Assert.Equal(new FileGroupDeleteResult(3, 0), result);
        Assert.False(Directory.Exists(Path.Combine(root, "合集")));
        Assert.True(File.Exists(keep));
    }

    [Theory]
    [InlineData("")]
    [InlineData("w:..")]
    [InlineData("w:../outside")]
    [InlineData("s:../outside/secret")]
    [InlineData("d:/etc")]
    public async Task DeleteRefusesAnythingThatIsNotAListedGroup(string id)
    {
        var outside = Path.Combine(temp, "outside");
        Directory.CreateDirectory(outside);
        var secret = Path.Combine(outside, "secret.mp4");
        File.WriteAllText(secret, "secret");
        Write("视频.mp4");
        var (server, origin) = await StartAsync(CreateServer());
        try
        {
            using var client = CreateClient();
            using var response = await client.DeleteAsync(origin + "/files/groups?id=" + Uri.EscapeDataString(id));
            Assert.Equal(id.Length == 0 ? HttpStatusCode.BadRequest : HttpStatusCode.NotFound, response.StatusCode);
            Assert.True(File.Exists(secret));
            Assert.True(File.Exists(Path.Combine(root, "视频.mp4")));
        }
        finally { await server.StopAsync(); }
    }

    [Fact]
    public void DeleteNeverFollowsALinkOutOfTheDownloadRoot()
    {
        if (OperatingSystem.IsWindows()) return;
        var outside = Path.Combine(temp, "outside");
        Directory.CreateDirectory(outside);
        var secret = Path.Combine(outside, "secret.mp4");
        File.WriteAllText(secret, "secret");
        Directory.CreateSymbolicLink(Path.Combine(root, "link"), outside);
        var server = CreateServer();
        // 即使列出了经由链接看到的文件，删除时也会因真实路径不在下载根目录内而拒绝
        var forged = new FileGroup { Id = "s:link/secret", MemberPaths = ["link/secret.mp4"] };

        var result = server.DeleteFileGroup(forged);

        Assert.Equal(new FileGroupDeleteResult(0, 1), result);
        Assert.True(File.Exists(secret));
        foreach (var group in server.BuildFileGroups(null).Groups) server.DeleteFileGroup(group);
        Assert.True(File.Exists(secret));
    }

    [Fact]
    public void DeleteSkipsProtectedFilesAndPathsOutsideTheRoot()
    {
        var outside = Path.Combine(temp, "outside.mp4");
        File.WriteAllText(outside, "secret");
        var metadata = Write("42/" + DownloadWorkFolder.MetadataFileName, "{}");
        var server = CreateServer();
        var forged = new FileGroup { Id = "s:x", MemberPaths = ["../outside.mp4", "42/" + DownloadWorkFolder.MetadataFileName, "/etc/hosts"] };

        var result = server.DeleteFileGroup(forged);

        Assert.Equal(new FileGroupDeleteResult(0, 3), result);
        Assert.True(File.Exists(outside));
        Assert.True(File.Exists(metadata));
    }

    [Fact]
    public async Task ActiveDownloadCannotBeDeleted()
    {
        Write("115050127886063/00000_115050127886063.P1.31783979182.vclip", "clip");
        var (server, origin) = await StartAsync(CreateServer());
        try
        {
            var task = new DownloadTask("115050127886063", "BV1t1YxzWEkz", 0);
            Assert.True(server.Tasks.TryAddPending(task, 10));
            server.Tasks.Start(task);
            Assert.True(server.BuildFileGroups(null).Groups.Single().Active);

            using var client = CreateClient();
            using var response = await client.DeleteAsync(origin + "/files/groups?id=" + Uri.EscapeDataString("w:115050127886063"));
            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
            Assert.True(Directory.Exists(Path.Combine(root, "115050127886063")));
        }
        finally { await server.StopAsync(); }
    }

    [Fact]
    public async Task OnlyTheFolderTheEngineIsUsingIsActive_NotOtherRecentlyChangedFolders()
    {
        Write("111/00000_111.P1.1.vclip", "clip");
        Write("111/111.jpg", "jpg");
        Write("222/00000_222.P1.2.vclip", "clip");
        var (server, origin) = await StartAsync(CreateServer());
        try
        {
            // 有任务在运行(av号与两个文件夹都不同)，两个文件夹都刚改动过：都不算正在下载
            var task = new DownloadTask("333", "BV1xx", 0);
            Assert.True(server.Tasks.TryAddPending(task, 10));
            server.Tasks.Start(task);
            Assert.All(server.BuildFileGroups(null).Groups, g => Assert.False(g.Active));

            using var client = CreateClient();
            using (DownloadWorkFolder.MarkActive([Path.Combine(root, "111")]))
            {
                var groups = server.BuildFileGroups(null).Groups;
                Assert.True(groups.Single(g => g.Id == "w:111").Active);
                Assert.False(groups.Single(g => g.Id == "w:222").Active);
                // 正在下载的文件夹里的单个文件也不能删除
                using var single = await client.DeleteAsync(origin + "/files/?path=" + Uri.EscapeDataString("111/111.jpg"));
                Assert.Equal(HttpStatusCode.Conflict, single.StatusCode);
                Assert.True(File.Exists(Path.Combine(root, "111", "111.jpg")));
                using var whole = await client.DeleteAsync(origin + "/files/groups?id=" + Uri.EscapeDataString("w:111"));
                Assert.Equal(HttpStatusCode.Conflict, whole.StatusCode);
                using var other = await client.DeleteAsync(origin + "/files/groups?id=" + Uri.EscapeDataString("w:222"));
                Assert.Equal(HttpStatusCode.OK, other.StatusCode);
            }
            Assert.False(server.BuildFileGroups(null).Groups.Single(g => g.Id == "w:111").Active);
            using var after = await client.DeleteAsync(origin + "/files/?path=" + Uri.EscapeDataString("111/111.jpg"));
            Assert.Equal(HttpStatusCode.OK, after.StatusCode);
        }
        finally { await server.StopAsync(); }
    }

    [Fact]
    public void HiddenStagedFileIsCountedAndDeletedWithTheIncompleteDownload()
    {
        var folder = Path.Combine(root, "10");
        Write("10/00000_10.P1.20.vclip", "clip");
        DownloadWorkFolder.TryCreate(folder, new DownloadWorkMetadata { Title = "合并时中断", Aid = "10" });
        // 合并分片时崩溃留下的隐藏暂存文件
        var staged = Write("10/.10.P1.20.0123456789abcdef0123456789abcdef.partial.mp4", new string('x', 1000));
        var server = CreateServer();

        var group = server.BuildFileGroups(null).Groups.Single(g => g.Id == "w:10");

        Assert.Equal(3, group.FileCount);
        Assert.Equal(4 + 1000 + new FileInfo(DownloadWorkFolder.MetadataPath(folder)).Length, group.TotalBytes);
        Assert.Contains(group.Files, f => f.Path == "10/" + Path.GetFileName(staged));
        Assert.Equal(new FileGroupDeleteResult(3, 0), server.DeleteFileGroup(group));
        Assert.False(Directory.Exists(folder));
    }

    [Fact]
    public async Task DeleteWithAStaleFileCountIsRefused()
    {
        Write("视频.mp4", "video");
        var (server, origin) = await StartAsync(CreateServer());
        try
        {
            var group = server.BuildFileGroups(null).Groups.Single();
            // 列表加载之后又多了同名的字幕：确认框里没有它
            Write("视频.zh-CN.srt", "srt");
            using var client = CreateClient();
            var url = $"{origin}/files/groups?id={Uri.EscapeDataString(group.Id)}&count={group.FileCount}&bytes={group.TotalBytes}&mtime={group.ModifiedTime}";
            using (var stale = await client.DeleteAsync(url))
            {
                Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
            }
            Assert.True(File.Exists(Path.Combine(root, "视频.zh-CN.srt")));

            var fresh = server.BuildFileGroups(null).Groups.Single();
            using var ok = await client.DeleteAsync($"{origin}/files/groups?id={Uri.EscapeDataString(fresh.Id)}&count={fresh.FileCount}&bytes={fresh.TotalBytes}&mtime={fresh.ModifiedTime}");
            Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
            Assert.Empty(Directory.EnumerateFiles(root));
        }
        finally { await server.StopAsync(); }
    }

    [Fact]
    public void DataDirectoryInsideTheDownloadRootIsNeitherListedNorDeleted()
    {
        var parent = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(Program.APP_DIR))!;
        var aroundDataDir = new BBDownTApiServer(new BBDownTServerOptions { DownloadRoot = parent, HistoryPath = historyPath });
        Assert.True(aroundDataDir.IsInsideDataDirectory(Path.Combine(Program.APP_DIR, "anything.mp4")));
        Assert.Null(aroundDataDir.ResolveDownloadDirectory(Path.GetFileName(Path.TrimEndingDirectorySeparator(Program.APP_DIR))));
        // 数据目录就是下载根目录时只保护其中的登录、配置等文件
        var sameAsDataDir = new BBDownTApiServer(new BBDownTServerOptions { DownloadRoot = Program.APP_DIR, HistoryPath = historyPath });
        Assert.False(sameAsDataDir.IsInsideDataDirectory(Path.Combine(Program.APP_DIR, "video.mp4")));
        Assert.False(CreateServer().IsInsideDataDirectory(Path.Combine(root, "video.mp4")));
    }

    [Fact]
    public void HistoryOwnedGroupsUseTheRecordedTitle()
    {
        Write("城市夜景.mp4");
        Write("城市夜景.jpg");
        var server = CreateServer();
        server.History.Add(DownloadHistoryStoreTests.Entry("4K 城市夜景", "风景UP", "BV1AAAAAAAAA", 100, "城市夜景.mp4"));

        var group = Assert.Single(server.BuildFileGroups(null).Groups);

        Assert.Equal("h:BV1AAAAAAAAA", group.Id);
        Assert.Equal("4K 城市夜景", group.Title);
        Assert.Equal(2, group.FileCount);
        Assert.Equal(100, group.FinishedAt);
    }
}
