using System.Text.Json;
using System.Text.Json.Nodes;

namespace BBDownT.Tests;

public class DownloadHistoryStoreTests : IDisposable
{
    private readonly string dir = Path.Combine(Path.GetTempPath(), "bbdownt-history-" + Guid.NewGuid().ToString("N"));
    private string FilePath => Path.Combine(dir, DownloadHistory.FileName);

    public DownloadHistoryStoreTests()
    {
        Directory.CreateDirectory(dir);
    }

    public void Dispose()
    {
        if (!OperatingSystem.IsWindows() && Directory.Exists(dir)) File.SetUnixFileMode(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        Directory.Delete(dir, true);
    }

    internal static DownloadHistoryEntry Entry(string title, string? owner = null, string bvid = "BV1t1YxzWEkz", long finishedAt = 100, params string[] files) => new()
    {
        Id = Guid.NewGuid().ToString("N"),
        TaskId = "task",
        FinishedAt = finishedAt,
        Url = $"https://www.bilibili.com/video/{bvid}/",
        PageUrl = $"https://www.bilibili.com/video/{bvid}/",
        Kind = "video",
        Bvid = bvid,
        Title = title,
        Owner = owner,
        Success = true,
        Files = files.Select(file => new DownloadHistoryFile(file, 1, true)).ToList(),
        Request = new DownloadHistoryRequest { Url = $"https://www.bilibili.com/video/{bvid}/" }
    };

    [Fact]
    public void MissingFile_StartsEmptyAndIsOnlyCreatedOnTheFirstWrite()
    {
        var store = new DownloadHistoryStore(FilePath);

        Assert.Equal(0, store.Count);
        Assert.False(File.Exists(FilePath));

        store.Add(Entry("第一个"));
        Assert.True(File.Exists(FilePath));
    }

    [Fact]
    public void Add_PersistsAcrossRestarts_NewestFirst()
    {
        var store = new DownloadHistoryStore(FilePath);
        store.Add(Entry("旧的", finishedAt: 1));
        store.Add(Entry("新的", finishedAt: 2));

        var reloaded = new DownloadHistoryStore(FilePath);

        Assert.Equal(["新的", "旧的"], reloaded.Snapshot().Select(entry => entry.Title));
        Assert.Equal(["新的", "旧的"], reloaded.Query(null, 0, 10).Items.Select(entry => entry.Title));
    }

    [Fact]
    public void Add_KeepsOnlyTheNewestEntriesUpToTheCap()
    {
        var store = new DownloadHistoryStore(FilePath, capacity: 3);
        for (var i = 1; i <= 5; i++) store.Add(Entry("视频" + i));

        Assert.Equal(["视频5", "视频4", "视频3"], store.Snapshot().Select(entry => entry.Title));
        Assert.Equal(["视频5", "视频4", "视频3"], new DownloadHistoryStore(FilePath, capacity: 3).Snapshot().Select(entry => entry.Title));
        // 加载时也按上限截断(比如上限调小以后)
        Assert.Equal(["视频5", "视频4"], new DownloadHistoryStore(FilePath, capacity: 2).Snapshot().Select(entry => entry.Title));
    }

    [Fact]
    public void Save_WritesAWholeDocumentViaATempFileAndLeavesNoTempFilesBehind()
    {
        var store = new DownloadHistoryStore(FilePath);
        store.Add(Entry("A"));
        store.Add(Entry("B"));

        Assert.Equal([DownloadHistory.FileName], Directory.GetFiles(dir).Select(Path.GetFileName));
        var json = JsonNode.Parse(File.ReadAllText(FilePath))!;
        Assert.Equal(DownloadHistoryStore.CurrentVersion, json["Version"]!.GetValue<int>());
        Assert.Equal(2, json["Entries"]!.AsArray().Count);
    }

    [Fact]
    public void FailedWrite_KeepsThePreviousFileIntactAndTheEntryInMemory()
    {
        if (OperatingSystem.IsWindows()) return; // 用目录只读模拟写入失败
        var store = new DownloadHistoryStore(FilePath);
        store.Add(Entry("已保存"));
        var before = File.ReadAllText(FilePath);

        File.SetUnixFileMode(dir, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try
        {
            store.Add(Entry("未能保存"));
        }
        finally
        {
            File.SetUnixFileMode(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        Assert.Equal(before, File.ReadAllText(FilePath));
        Assert.Equal(["未能保存", "已保存"], store.Snapshot().Select(entry => entry.Title));
        Assert.Equal([DownloadHistory.FileName], Directory.GetFiles(dir).Select(Path.GetFileName));
        // 下次修改时一并写入
        store.Add(Entry("之后"));
        Assert.Equal(3, new DownloadHistoryStore(FilePath).Count);
    }

    [Fact]
    public void SavedFile_IsOnlyReadableByTheOwner()
    {
        if (OperatingSystem.IsWindows()) return;
        File.WriteAllText(FilePath, """{"Version":1,"Entries":[]}""");
        File.SetUnixFileMode(FilePath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);

        new DownloadHistoryStore(FilePath).Add(Entry("观看记录"));

        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(FilePath));
    }

    [Fact]
    public void StaleTempFiles_AreRemovedOnLoadButFreshOnesAndOtherFilesAreKept()
    {
        string Temp(string middle, TimeSpan age)
        {
            var path = Path.Combine(dir, $"{DownloadHistory.FileName}.{middle}.tmp");
            File.WriteAllText(path, "{");
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow - age);
            return path;
        }
        var stale = Temp(Guid.NewGuid().ToString("N"), TimeSpan.FromHours(1));
        var fresh = Temp(Guid.NewGuid().ToString("N"), TimeSpan.Zero);
        var other = Temp("not-a-guid", TimeSpan.FromHours(1));

        Assert.Equal(0, new DownloadHistoryStore(FilePath).Count);

        Assert.False(File.Exists(stale));
        Assert.True(File.Exists(fresh)); // 可能是别的进程正在写
        Assert.True(File.Exists(other));
    }

    [Fact]
    public void ChangesMadeByAnotherProcess_AreReadBeforeTheNextReadOrWrite()
    {
        var mine = new DownloadHistoryStore(FilePath);
        mine.Add(Entry("我的第一条"));
        var other = new DownloadHistoryStore(FilePath);
        other.Add(Entry("另一个进程"));

        Assert.Equal(["另一个进程", "我的第一条"], mine.Snapshot().Select(entry => entry.Title));
        mine.Add(Entry("我的第二条"));
        Assert.Equal(["我的第二条", "另一个进程", "我的第一条"], new DownloadHistoryStore(FilePath).Snapshot().Select(entry => entry.Title));

        Assert.True(other.Remove(mine.Snapshot().Single(entry => entry.Title == "我的第一条").Id));
        Assert.Equal(["我的第二条", "另一个进程"], mine.Query(null, 0, 10).Items.Select(entry => entry.Title));
        Assert.Equal(2, mine.Count);
    }

    [Fact]
    public void UnsavedEntries_AreMergedWhenAnotherProcessChangedTheFile()
    {
        if (OperatingSystem.IsWindows()) return; // 用目录只读模拟写入失败
        var mine = new DownloadHistoryStore(FilePath);
        mine.Add(Entry("已保存"));
        File.SetUnixFileMode(dir, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try
        {
            mine.Add(Entry("未能保存"));
        }
        finally
        {
            File.SetUnixFileMode(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        new DownloadHistoryStore(FilePath).Add(Entry("另一个进程"));

        Assert.Equal(["未能保存", "另一个进程", "已保存"], mine.Snapshot().Select(entry => entry.Title));
        mine.Add(Entry("之后"));
        Assert.Equal(["之后", "未能保存", "另一个进程", "已保存"], new DownloadHistoryStore(FilePath).Snapshot().Select(entry => entry.Title));
    }

    [Fact]
    public void CorruptFile_IsBackedUpAndHistoryStartsEmpty()
    {
        File.WriteAllText(FilePath, "{\"Version\":1,\"Entries\":[{\"Id\":");

        var store = new DownloadHistoryStore(FilePath);
        Assert.Equal(0, store.Count);

        var backup = Assert.Single(Directory.GetFiles(dir, DownloadHistory.FileName + ".corrupt-*"));
        Assert.Equal("{\"Version\":1,\"Entries\":[{\"Id\":", File.ReadAllText(backup));
        Assert.False(File.Exists(FilePath));

        store.Add(Entry("重新开始"));
        Assert.Equal(["重新开始"], new DownloadHistoryStore(FilePath).Snapshot().Select(entry => entry.Title));
        Assert.True(File.Exists(backup));
    }

    [Theory]
    [InlineData("")]
    [InlineData("null")]
    [InlineData("[1,2,3]")]
    [InlineData("{\"Version\":1,\"Entries\":[{\"Id\":\"x\",\"FinishedAt\":\"不是数字\"}]}")]
    public void UnreadableContent_NeverThrows(string content)
    {
        File.WriteAllText(FilePath, content);

        var store = new DownloadHistoryStore(FilePath);

        Assert.Equal(0, store.Count);
        // 空文件不算损坏，不留备份
        Assert.Equal(content.Length == 0 ? 0 : 1, Directory.GetFiles(dir, DownloadHistory.FileName + ".corrupt-*").Length);
    }

    [Fact]
    public void EntriesWithMissingFields_AreNormalizedAndEntriesWithoutIdAreDropped()
    {
        File.WriteAllText(FilePath, """
            {"Version":1,"Entries":[{"Id":"a","Title":"只有标题"},{"Title":"没有Id"},null]}
            """);

        var entry = Assert.Single(new DownloadHistoryStore(FilePath).Snapshot());

        Assert.Equal("只有标题", entry.Title);
        Assert.Empty(entry.Files);
        Assert.Empty(entry.Pages);
        Assert.Empty(entry.Streams);
        Assert.Empty(entry.StreamTags);
        Assert.NotNull(entry.Request);
        Assert.Equal("", entry.Url);
    }

    [Fact]
    public async Task ConcurrentAppends_AreAllKept()
    {
        var store = new DownloadHistoryStore(FilePath);

        await Task.WhenAll(Enumerable.Range(0, 8).Select(worker => Task.Run(() =>
        {
            for (var i = 0; i < 25; i++) store.Add(Entry($"w{worker}-{i}"));
        })));

        Assert.Equal(200, store.Count);
        var reloaded = new DownloadHistoryStore(FilePath).Snapshot();
        Assert.Equal(200, reloaded.Count);
        Assert.Equal(200, reloaded.Select(entry => entry.Id).Distinct().Count());
        Assert.Equal([DownloadHistory.FileName], Directory.GetFiles(dir).Select(Path.GetFileName));
    }

    [Fact]
    public void RemoveAndClear_OnlyTouchRecords()
    {
        var store = new DownloadHistoryStore(FilePath);
        var keep = Entry("保留");
        var drop = Entry("删除");
        store.AddRange([keep, drop]);

        Assert.True(store.Remove(drop.Id));
        Assert.False(store.Remove(drop.Id));
        Assert.False(store.Remove("unknown"));
        Assert.Equal([keep.Id], new DownloadHistoryStore(FilePath).Snapshot().Select(entry => entry.Id));

        Assert.Equal(1, store.Clear());
        Assert.Equal(0, new DownloadHistoryStore(FilePath).Count);
    }

    [Fact]
    public void Query_FiltersByTitleOwnerOrBvidAndPagesNewestFirst()
    {
        var store = new DownloadHistoryStore(FilePath);
        store.Add(Entry("【4K】城市夜景", "风景UP", "BV1AAAAAAAAA"));
        store.Add(Entry("猫咪合集", "萌宠UP", "BV1BBBBBBBBB"));
        store.Add(Entry("城市延时摄影", "萌宠UP", "BV1CCCCCCCCC"));

        Assert.Equal(["城市延时摄影", "【4K】城市夜景"], store.Query("城市", 0, 10).Items.Select(entry => entry.Title));
        Assert.Equal(["城市延时摄影", "猫咪合集"], store.Query("萌宠", 0, 10).Items.Select(entry => entry.Title));
        Assert.Equal(["猫咪合集"], store.Query("bv1bbbbbbbbb", 0, 10).Items.Select(entry => entry.Title));
        Assert.Equal(["【4K】城市夜景"], store.Query("4k 风景", 0, 10).Items.Select(entry => entry.Title));
        Assert.Empty(store.Query("不存在", 0, 10).Items);

        var page = store.Query("", 1, 1);
        Assert.Equal(3, page.Total);
        Assert.Equal(3, page.Matched);
        Assert.Equal(1, page.Offset);
        Assert.Equal(1, page.Limit);
        Assert.Equal(["猫咪合集"], page.Items.Select(entry => entry.Title));

        var filtered = store.Query("城市", 1, 5);
        Assert.Equal(3, filtered.Total);
        Assert.Equal(2, filtered.Matched);
        Assert.Equal(["【4K】城市夜景"], filtered.Items.Select(entry => entry.Title));
    }

    [Fact]
    public void Document_RoundTripsThroughTheSourceGeneratedContext()
    {
        var entry = Entry("标题", "UP主", files: "sub/视频.mp4") with
        {
            Aid = "170001",
            Ep = null,
            Pic = "https://i0.hdslb.com/bfs/archive/x.jpg",
            Api = "TV",
            Pages = [new DownloadHistoryPage(2, "第二P")],
            Streams = ["P2 · TV · 4K 超清 3840x2160 HEVC · 192K M4A"],
            StreamTags = ["4K 超清", "HEVC", "192K"],
            TotalBytes = 1,
            Success = false,
            Error = "失败原因",
            Request = new DownloadHistoryRequest { Url = "BV1t1YxzWEkz", UseTvApi = true, VideoStream = "120:HEVC", SelectPage = "2" }
        };
        var json = JsonSerializer.Serialize(new DownloadHistoryDocument(1, [entry]), AppJsonSerializerContext.Default.DownloadHistoryDocument);

        var back = Assert.Single(JsonSerializer.Deserialize(json, AppJsonSerializerContext.Default.DownloadHistoryDocument)!.Entries);

        Assert.Equal(JsonSerializer.Serialize(entry, AppJsonSerializerContext.Default.DownloadHistoryEntry),
            JsonSerializer.Serialize(back, AppJsonSerializerContext.Default.DownloadHistoryEntry));
        Assert.Equal(entry.Request, back.Request);
    }
}
