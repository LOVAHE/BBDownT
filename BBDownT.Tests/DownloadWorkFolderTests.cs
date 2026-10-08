using System.Text.Json;
using System.Text.Json.Nodes;
using BBDownT.Core.Entity;
using static BBDownT.Core.Entity.Entity;

namespace BBDownT.Tests;

/// <summary>
/// 临时工作文件夹的说明文件(.bbdownt-task.json)：下载开始时写入，分P完成、不再有未完成的工作时随文件夹删除
/// </summary>
public class DownloadWorkFolderTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "bbdownt-work-" + Guid.NewGuid().ToString("N"));
    private readonly string folder;

    public DownloadWorkFolderTests()
    {
        folder = Path.Combine(root, "115050127886063");
        Directory.CreateDirectory(folder);
    }

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }

    private string MetadataPath => Path.Combine(folder, DownloadWorkFolder.MetadataFileName);

    private static VInfo Info(params Page[] pages) => new()
    {
        Title = "4K 城市夜景",
        Desc = "",
        Pic = "http://i0.hdslb.com/bfs/archive/cover.jpg",
        PubTime = 1,
        PagesInfo = [.. pages],
    };

    private static Page P(int index, string aid = "115050127886063", string title = "正片") =>
        new(index, aid, "31783979182", "", title, 60, "", 1) { ownerName = "风景UP" };

    [Fact]
    public void BeginWritesTitleCoverAndTheResumeRequestWithoutCredentials()
    {
        var option = new MyOption
        {
            Url = "https://www.bilibili.com/video/BV1t1YxzWEkz/?spm_id_from=333&access_key=secret-key",
            VideoStream = "120:HEVC",
            AudioStream = "30280",
            DfnPriority = "4K 超清",
            Cookie = "SESSDATA=secret-cookie",
            AccessToken = "secret-token",
        };

        Program.BeginWorkFolder(folder, option, Info(P(1)), P(1), "http://i0.hdslb.com/bfs/archive/cover.jpg", "WEB", null);

        var text = File.ReadAllText(MetadataPath);
        Assert.DoesNotContain("secret", text);
        var metadata = DownloadWorkFolder.Read(folder)!;
        Assert.Equal("4K 城市夜景", metadata.Title);
        Assert.Equal("风景UP", metadata.Owner);
        Assert.Equal("115050127886063", metadata.Aid);
        Assert.Equal("BV1t1YxzWEkz", metadata.Bvid);
        Assert.Equal("31783979182", metadata.Cid);
        Assert.Equal(1, metadata.Page);
        Assert.Equal("WEB", metadata.Api);
        Assert.Equal(DownloadWorkFolder.SourceEngine, metadata.Source);
        Assert.Equal("https://www.bilibili.com/video/BV1t1YxzWEkz/", metadata.Url);
        Assert.Equal("https://www.bilibili.com/video/BV1t1YxzWEkz/", metadata.Request!.Url);
        Assert.Equal("120:HEVC", metadata.Request.VideoStream);
        Assert.Equal("30280", metadata.Request.AudioStream);
        Assert.Equal("4K 超清", metadata.Request.DfnPriority);
        Assert.True(metadata.StartedAt > 0);
        if (!OperatingSystem.IsWindows())
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(MetadataPath));
    }

    [Fact]
    public void BeginPrefersTheRequestRecordedBeforeParsing()
    {
        var task = new DownloadTask("t", "", "fixture", 0);
        task.BeginVideo("https://b23.tv/x", new DownloadHistoryRequest { Url = "https://www.bilibili.com/video/BV1t1YxzWEkz/", UseTvApi = true });

        Program.BeginWorkFolder(folder, new MyOption { Url = "changed" }, Info(P(1)), P(1), "", "TV", task);

        var request = DownloadWorkFolder.Read(folder)!.Request!;
        Assert.Equal("https://www.bilibili.com/video/BV1t1YxzWEkz/", request.Url);
        Assert.True(request.UseTvApi);
    }

    [Fact]
    public void ListOfDifferentVideosUsesThePageTitleAndCover()
    {
        var first = P(1, "100", "第一个视频");
        first.cover = "http://i0.hdslb.com/1.jpg";
        var second = P(2, "200", "第二个视频");

        Program.BeginWorkFolder(folder, new MyOption { Url = "https://space.bilibili.com/1/favlist" }, Info(first, second), first, "list.jpg", "WEB", null);

        var metadata = DownloadWorkFolder.Read(folder)!;
        Assert.Equal("第一个视频", metadata.Title);
        Assert.Equal("http://i0.hdslb.com/1.jpg", metadata.Pic);
    }

    [Fact]
    public void BeginAgainKeepsTheFirstStartTime()
    {
        DownloadWorkFolder.Begin(folder, new DownloadWorkMetadata { Title = "P1", Page = 1 });
        var first = DownloadWorkFolder.Read(folder)!;
        File.WriteAllText(MetadataPath, File.ReadAllText(MetadataPath).Replace($"\"StartedAt\":{first.StartedAt}", "\"StartedAt\":123"));

        DownloadWorkFolder.Begin(folder, new DownloadWorkMetadata { Title = "P2", Page = 2 });

        var second = DownloadWorkFolder.Read(folder)!;
        Assert.Equal(123, second.StartedAt);
        Assert.Equal(2, second.Page);
        Assert.Empty(Directory.GetFiles(folder, "*.tmp"));
    }

    [Fact]
    public void TryCreateNeverOverwritesExistingMetadata()
    {
        DownloadWorkFolder.Begin(folder, new DownloadWorkMetadata { Title = "下载时写入", Request = new() { Url = "BV1t1YxzWEkz" } });

        Assert.False(DownloadWorkFolder.TryCreate(folder, new DownloadWorkMetadata { Title = "查到的", Source = DownloadWorkFolder.SourceLookup }));

        Assert.Equal("下载时写入", DownloadWorkFolder.Read(folder)!.Title);
        File.Delete(MetadataPath);
        Assert.True(DownloadWorkFolder.TryCreate(folder, new DownloadWorkMetadata { Title = "查到的", Source = DownloadWorkFolder.SourceLookup }));
        Assert.Equal(DownloadWorkFolder.SourceLookup, DownloadWorkFolder.Read(folder)!.Source);
        Assert.False(DownloadWorkFolder.TryCreate(Path.Combine(root, "missing"), new DownloadWorkMetadata { Title = "x" }));
    }

    [Fact]
    public void FinishedPageDeletesTheMetadataAndTheEmptyFolder()
    {
        // 已混流：轨道和它们的续传状态已随输入删除(MediaOutput.DeleteInput)，只剩说明文件
        DownloadWorkFolder.Begin(folder, new DownloadWorkMetadata { Title = "完成" });

        Program.DeleteEmptyDownloadDirectory(folder);

        Assert.False(Directory.Exists(folder));
    }

    [Fact]
    public void InterruptedWorkKeepsTheMetadata()
    {
        DownloadWorkFolder.Begin(folder, new DownloadWorkMetadata { Title = "未完成" });
        File.WriteAllText(Path.Combine(folder, "00000_115050127886063.P2.1.vclip"), "clip");

        Program.DeleteEmptyDownloadDirectory(folder);

        Assert.True(File.Exists(MetadataPath));
    }

    [Fact]
    public void FinishedSkipMuxTrackKeepsItsResumeStateButNotTheMetadata()
    {
        // 只下载不混流：轨道就是输出；旁边的续传状态保留，重新下载时可直接沿用整条轨道
        var track = Path.Combine(folder, "115050127886063.P1.31783979182.mp4");
        File.WriteAllText(track, "video");
        File.WriteAllText(track + ".resume", "state");
        DownloadWorkFolder.Begin(folder, new DownloadWorkMetadata { Title = "只下载不混流" });

        DownloadWorkFolder.CleanUp(folder);

        Assert.False(File.Exists(MetadataPath));
        Assert.True(File.Exists(track));
        Assert.True(File.Exists(track + ".resume"));
    }

    [Fact]
    public void FinishedPageDeletesStagedFilesLeftByACrashedMerge()
    {
        DownloadWorkFolder.Begin(folder, new DownloadWorkMetadata { Title = "合并时崩溃过" });
        var stale = Path.Combine(folder, ".115050127886063.P1.31783979182.0123456789abcdef0123456789abcdef.partial.mp4");
        File.WriteAllText(stale, "half a track");
        File.SetLastWriteTimeUtc(stale, DateTime.UtcNow.AddHours(-1));

        Program.DeleteEmptyDownloadDirectory(folder);

        Assert.False(Directory.Exists(folder));
    }

    [Fact]
    public void RecentStagedFileCountsAsPendingWork()
    {
        DownloadWorkFolder.Begin(folder, new DownloadWorkMetadata { Title = "刚中断" });
        var recent = Path.Combine(folder, ".115050127886063.P1.31783979182.0123456789abcdef0123456789abcdef.partial.mp4");
        File.WriteAllText(recent, "half a track");

        Program.DeleteEmptyDownloadDirectory(folder);

        // 不到两分钟、可能还在写入的暂存文件不删；说明文件留着，「已下载文件」仍显示为未完成的下载
        Assert.True(File.Exists(recent));
        Assert.True(File.Exists(MetadataPath));
    }

    [Fact]
    public void ServerTaskNeverWritesTheMetadataThroughALinkedFolder()
    {
        if (OperatingSystem.IsWindows()) return;
        var outside = Path.Combine(root, "outside");
        Directory.CreateDirectory(outside);
        var restricted = Path.Combine(root, "downloads");
        Directory.CreateDirectory(restricted);
        var linked = Path.Combine(restricted, "42");
        Directory.CreateSymbolicLink(linked, outside);

        Program.BeginWorkFolder(linked, new MyOption { Url = "av42", RestrictedOutputRoot = restricted }, Info(P(1, "42")), P(1, "42"), "", "WEB", null);

        Assert.Empty(Directory.GetFiles(outside));
    }

    [Fact]
    public void ActiveFoldersAreCountedPerRegistration()
    {
        Assert.False(DownloadWorkFolder.IsActive(folder));
        var first = DownloadWorkFolder.MarkActive([folder, folder + Path.DirectorySeparatorChar]);
        var second = DownloadWorkFolder.MarkActive([folder]);
        Assert.True(DownloadWorkFolder.IsActive(folder));

        first.Dispose();
        first.Dispose();
        Assert.True(DownloadWorkFolder.IsActive(folder));
        second.Dispose();
        Assert.False(DownloadWorkFolder.IsActive(folder));
        Assert.False(DownloadWorkFolder.IsActive(Path.Combine(root, "other")));
    }

    [Theory]
    [InlineData("00000_115050127886063.P1.31783979182.vclip", true)]
    [InlineData("00000_115050127886063.P1.31783979182.vclip.resume", true)]
    [InlineData("115050127886063.P1.31783979182.mp4.tmp", true)]
    [InlineData("115050127886063.tmp.resume", true)]
    // 合并好的轨道旁的续传状态不算未完成的工作
    [InlineData("115050127886063.P1.31783979182.mp4.resume", false)]
    [InlineData("115050127886063.P1.31783979182.mp4", false)]
    [InlineData("foo.resume", false)]
    [InlineData("00000_1.P1.2.vclip", false)]
    public void WorkFilesAreNamedAfterTheFolder(string name, bool expected)
    {
        Assert.Equal(expected, DownloadWorkFolder.IsWorkFileOf(name, "115050127886063"));
        Assert.False(DownloadWorkFolder.IsWorkFileOf(name, "资料"));
    }

    [Theory]
    [InlineData(".10.P1.20.0123456789abcdef0123456789abcdef.partial.mp4", true, "10.P1.20.mp4")]
    [InlineData(".[P01]开场.0123456789abcdef0123456789abcdef.partial.mkv", true, "[P01]开场.mkv")]
    [InlineData(".chapters.0123456789abcdef0123456789abcdef.partial", true, "chapters")]
    [InlineData("other.partial.mp4", false, "")]
    [InlineData(".x.0123.partial.mp4", false, "")]
    public void StagedNames(string name, bool staged, string original)
    {
        Assert.Equal(staged, DownloadWorkFolder.TryParseStagedName(name, out var parsed));
        Assert.Equal(original, parsed);
    }

    [Theory]
    [InlineData(".bbdownt-task.json", true)]
    [InlineData(".bbdownt-task.json.0123456789abcdef.tmp", true)]
    [InlineData("bbdownt-task.json", false)]
    [InlineData("video.json", false)]
    public void MetadataFilesAreProtectedEverywhere(string name, bool expected)
    {
        Assert.Equal(expected, DownloadWorkFolder.IsMetadataFileName(name));
        Assert.Equal(expected, BBDownTApiServer.IsProtectedFile(Path.Combine(folder, name)));
    }

    [Fact]
    public void FilesApiNeverListsReadsOrDeletesTheMetadataFile()
    {
        DownloadWorkFolder.Begin(folder, new DownloadWorkMetadata { Title = "x" });
        File.WriteAllText(Path.Combine(folder, "cover.jpg"), "jpg");
        var server = new BBDownTApiServer(new BBDownTServerOptions { DownloadRoot = root, HistoryPath = Path.Combine(root, "data", "history.json") });

        Assert.Equal(["115050127886063/cover.jpg"], server.ListDownloadedFiles().Select(f => f.Path));
        Assert.Null(server.ResolveDownloadPath("115050127886063/" + DownloadWorkFolder.MetadataFileName));
    }

    [Fact]
    public void MetadataJsonContract()
    {
        var json = JsonSerializer.Serialize(new DownloadWorkMetadata
        {
            Title = "t", Aid = "1", Bvid = "BV1", Cid = "2", Page = 1, StartedAt = 5, UpdatedAt = 6,
            Request = new DownloadHistoryRequest { Url = "u", VideoStream = "80:AVC" }
        }, AppJsonSerializerContext.Default.DownloadWorkMetadata);

        var expected = JsonNode.Parse("""
            {"Version":1,"Source":"engine","Title":"t","Owner":null,"Pic":null,"Url":null,"Aid":"1","Bvid":"BV1","Cid":"2",
             "Page":1,"PageTitle":null,"Api":null,"StartedAt":5,"UpdatedAt":6,"Request":{"Url":"u","VideoStream":"80:AVC"}}
            """);
        Assert.True(JsonNode.DeepEquals(expected, JsonNode.Parse(json)), json);
    }

    [Theory]
    [InlineData("00000_115050127886063.P1.31783979182.vclip", true, 0, "115050127886063.P1.31783979182", true, 1)]
    [InlineData("00012_10.P3.20.zh.aclip", true, 12, "10.P3.20.zh", false, 3)]
    [InlineData("00001_10.20.P1.back_ground.aclip", true, 1, "10.20.P1.back_ground", false, null)]
    [InlineData("0000_10.P1.20.vclip", false, 0, "", false, null)]
    [InlineData("00000_10.P1.20.vclip.resume", false, 0, "", false, null)]
    public void ClipNames(string name, bool ok, int index, string trackBase, bool video, int? page)
    {
        Assert.Equal(ok, DownloadWorkFolder.TryParseClip(name, out var clip));
        if (!ok) return;
        Assert.Equal((index, trackBase, video, page), (clip.Index, clip.TrackBase, clip.IsVideo, clip.Page));
    }
}
