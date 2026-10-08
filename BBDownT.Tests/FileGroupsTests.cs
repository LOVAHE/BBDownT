namespace BBDownT.Tests;

/// <summary>
/// 「已下载文件」按视频分组的规则(纯函数，不读写磁盘)
/// </summary>
public class FileGroupsTests
{
    private const int Clip = 20 * 1024 * 1024;
    private static readonly Dictionary<string, DownloadWorkMetadata> NoMetadata = new();

    private static ListedFile F(string path, long size = 10, long time = 1000) => new(path, size, time);

    /// <summary>
    /// 下载流程写入分片 .resume 的续传状态：这一段 [from, from+length) 已下载 local 字节
    /// </summary>
    private static DownloadResumeState State(long length, long local, long from = 0) =>
        new("track", "source", from, from + length - 1, from + length, local == length, local, new string('0', 64),
            new DownloadResumeValidator("\"v\"", null));

    private static DownloadHistoryEntry Entry(string title, string bvid, long finishedAt, params string[] files) =>
        DownloadHistoryStoreTests.Entry(title, "UP", bvid, finishedAt, files) with { Pic = "https://i0.hdslb.com/bfs/archive/" + bvid + ".jpg" };

    private static FileGroup Single(List<FileGroup> groups, string id) => Assert.Single(groups, g => g.Id == id);

    [Fact]
    public void HistoryOwnedVideo_TakesSameNamedSubtitleDanmakuAndCoverButNotOtherVideos()
    {
        var files = new[]
        {
            F("城市夜景.mp4", 1000, 2000), F("城市夜景.zh-CN.srt"), F("城市夜景.xml"), F("城市夜景.ass"), F("城市夜景.jpg"),
            F("城市夜景.第二部.mp4", 500), F("别的视频.mp4", 300),
        };
        var history = new[] { Entry("城市夜景 4K", "BV1AAAAAAAAA", 3000, "城市夜景.mp4") };

        var groups = DownloadFileGroups.Build(files, history, NoMetadata);

        var night = Single(groups, "h:BV1AAAAAAAAA");
        Assert.Equal("城市夜景 4K", night.Title);
        Assert.Equal(FileGroupStatus.Complete, night.Status);
        Assert.Equal("history", night.Source);
        Assert.Equal("城市夜景.mp4", night.MainFile);
        Assert.Equal("城市夜景.jpg", night.CoverFile);
        Assert.Equal(3000, night.FinishedAt);
        Assert.Equal("https://i0.hdslb.com/bfs/archive/BV1AAAAAAAAA.jpg", night.Pic);
        Assert.Equal(["城市夜景.mp4", "城市夜景.ass", "城市夜景.jpg", "城市夜景.xml", "城市夜景.zh-CN.srt"], night.Files.Select(f => f.Path));
        Assert.Equal(5, night.FileCount);
        Assert.Equal(1040, night.TotalBytes);
        // 名字以「城市夜景.」开头的另一个视频不是附属文件
        Assert.Equal("城市夜景.第二部", Single(groups, "s:城市夜景.第二部").Title);
        Assert.Equal("别的视频", Single(groups, "s:别的视频").Title);
        Assert.Equal(3, groups.Count);
        // 最新的在前(下载历史的完成时间)
        Assert.Equal("h:BV1AAAAAAAAA", groups[0].Id);
    }

    [Fact]
    public void SameVideoDownloadedTwice_IsOneGroupAndTheNewestRecordWins()
    {
        var files = new[] { F("合集/[P01]开场.mp4"), F("合集/[P02]正片.mp4") };
        var history = new[]
        {
            Entry("新标题", "BV1AAAAAAAAA", 200, "合集/[P02]正片.mp4"),
            Entry("旧标题", "BV1AAAAAAAAA", 100, "合集/[P01]开场.mp4"),
        };

        var groups = DownloadFileGroups.Build(files, history, NoMetadata);

        var group = Assert.Single(groups);
        Assert.Equal("新标题", group.Title);
        Assert.Equal(200, group.FinishedAt);
        Assert.Equal("合集", group.Folder);
        Assert.Equal("合集/[P01]开场.mp4", group.MainFile);
        Assert.Equal(2, group.FileCount);
    }

    [Fact]
    public void MultiPageFolder_OtherFilesInTheFolderJoinTheVideoFromHistory()
    {
        var files = new[]
        {
            F("合集/[P01]开场.mp4"), F("合集/[P01]开场.ass"), F("合集/[P02]正片.mp4"), F("合集/[P02]正片.zh-CN.srt"), F("单独.mp4"),
        };
        // 只有 P2 是这次下载的(有记录)；P1 是之前的版本下载的
        var history = new[] { Entry("多P视频", "BV1AAAAAAAAA", 100, "合集/[P02]正片.mp4") };

        var groups = DownloadFileGroups.Build(files, history, NoMetadata);

        var video = Single(groups, "h:BV1AAAAAAAAA");
        Assert.Equal(4, video.FileCount);
        Assert.Single(groups, g => g.Id == "s:单独");
    }

    [Fact]
    public void FilesWithoutHistory_MultiPageFolderIsOneGroupOtherwiseGroupedByName()
    {
        var files = new[]
        {
            F("合集/[P01]开场.mp4"), F("合集/[P02]正片.mp4"), F("合集/[P02]正片.ass"), F("合集/封面.jpg"),
            F("B站/视频A.mp4"), F("B站/视频A.zh-CN.srt"), F("B站/视频A.jpg"), F("B站/视频B.mp4"), F("B站/清单.txt"),
            F("video.part2.mp4"), F("video.mp4"), F("video.part2.zh.srt"), F("video.zh.srt"),
        };

        var groups = DownloadFileGroups.Build(files, [], NoMetadata);

        // 多P文件夹：分P序号开头的音视频和同名的附属文件一组；文件夹里别的文件(封面.jpg)按文件名各自成组
        var folder = Single(groups, "d:合集");
        Assert.Equal("合集", folder.Title);
        Assert.Equal("folder", folder.Source);
        Assert.Equal(["合集/[P01]开场.mp4", "合集/[P02]正片.mp4", "合集/[P02]正片.ass"], folder.Files.Select(f => f.Path));
        Assert.Equal("合集", folder.Folder);
        Assert.Single(Single(groups, "s:合集/封面").Files);
        // 不是多P文件夹：按文件名分组
        Assert.Equal(["B站/视频A.mp4", "B站/视频A.jpg", "B站/视频A.zh-CN.srt"], Single(groups, "s:B站/视频A").Files.Select(f => f.Path));
        Assert.Single(Single(groups, "s:B站/视频B").Files);
        Assert.Single(Single(groups, "s:B站/清单").Files);
        // 字幕跟文件名最长的视频
        Assert.Equal(["video.part2.mp4", "video.part2.zh.srt"], Single(groups, "s:video.part2").Files.Select(f => f.Path));
        Assert.Equal(["video.mp4", "video.zh.srt"], Single(groups, "s:video").Files.Select(f => f.Path));
    }

    [Fact]
    public void IncompleteDownloadWithMetadata_IsOneGroupWithTitleClipsAndResumeRequest()
    {
        var files = new[]
        {
            F("115050127886063/00000_115050127886063.P1.31783979182.vclip", Clip, 1700),
            F("115050127886063/00000_115050127886063.P1.31783979182.vclip.resume", 83, 1700),
            F("115050127886063/00001_115050127886063.P1.31783979182.vclip", Clip, 1800),
            F("115050127886063/00001_115050127886063.P1.31783979182.vclip.resume", 83, 1800),
            F("115050127886063/00002_115050127886063.P1.31783979182.vclip", 1000, 1900),
            F("115050127886063/00002_115050127886063.P1.31783979182.vclip.resume", 83, 1900),
            F("115050127886063/00000_115050127886063.P1.31783979182.aclip", 4000, 1950),
            F("115050127886063/00000_115050127886063.P1.31783979182.aclip.resume", 83, 1950),
            F("115050127886063/115050127886063.jpg", 5000, 1500),
            F("done.mp4", 10, 1000),
        };
        var request = new DownloadHistoryRequest { Url = "https://www.bilibili.com/video/BV1t1YxzWEkz/", VideoStream = "120:HEVC" };
        var metadata = new Dictionary<string, DownloadWorkMetadata>
        {
            ["115050127886063"] = new() { Title = "4K 城市夜景", Owner = "UP", Pic = "https://i0.hdslb.com/x.jpg", Aid = "115050127886063", Bvid = "BV1t1YxzWEkz", Page = 1, Request = request }
        };
        // 视频的3段都已下载完整，音频第1段(8000字节)只下载了一半
        DownloadResumeState? StateOf(string path) => path.Contains(".vclip")
            ? State(files.Single(file => file.Path + ".resume" == path).Size, files.Single(file => file.Path + ".resume" == path).Size)
            : State(8000, 4000);

        var groups = DownloadFileGroups.Build(files, [], metadata, StateOf);

        var work = Single(groups, "w:115050127886063");
        Assert.Equal(FileGroupStatus.Incomplete, work.Status);
        Assert.Equal("work", work.Source);
        Assert.Equal("4K 城市夜景", work.Title);
        Assert.False(work.TitlePending);
        Assert.Equal("115050127886063", work.Folder);
        Assert.Equal("115050127886063", work.Aid);
        Assert.Equal("BV1t1YxzWEkz", work.Bvid);
        Assert.Equal(request, work.Request);
        Assert.Equal("115050127886063/115050127886063.jpg", work.CoverFile);
        Assert.Equal(["115050127886063/115050127886063.jpg"], work.Files.Select(f => f.Path));
        Assert.Equal(4, work.ClipCount);
        Assert.Equal(3, work.CompleteClipCount);
        // 删除整组时删除的全部文件：4个分片、4个续传状态和封面
        Assert.Equal(9, work.FileCount);
        Assert.Equal(2L * Clip + 1000 + 4000 + 5000 + 4 * 83, work.TotalBytes);
        Assert.Equal(1950, work.ModifiedTime);
        Assert.Null(work.MainFile);
        Assert.Equal([("video", 0, true), ("video", 1, true), ("video", 2, true), ("audio", 0, false)],
            work.Clips.Select(c => (c.Track, c.Index, c.Complete)));
        Assert.All(work.Clips, c => Assert.Equal(1, c.Page));
        // 删除整组时连同续传状态一起删除
        Assert.Equal(9, work.MemberPaths.Count);
        Assert.Single(groups, g => g.Id == "s:done");
    }

    [Fact]
    public void LegacyIncompleteFolderWithoutMetadata_UsesTheLookedUpTitleOrAFallbackLabel()
    {
        var files = new[]
        {
            F("115050127886063/00000_115050127886063.P1.31783979182.vclip", Clip),
            F("115050127886063/00001_115050127886063.P1.31783979182.vclip", 7000),
            F("115050127886063/00001_115050127886063.P1.31783979182.vclip.resume", 83),
        };

        var pending = Assert.Single(DownloadFileGroups.Build(files, [], NoMetadata, titleFor: _ => (null, true)));
        Assert.Equal("未完成的下载（av115050127886063）", pending.Title);
        Assert.True(pending.TitlePending);
        Assert.False(pending.NeedsTitleLookup);
        Assert.Null(pending.Request);
        Assert.Equal("BV1t1YxzWEkz", pending.Bvid);
        Assert.Equal("https://www.bilibili.com/video/BV1t1YxzWEkz/", pending.Url);
        // 没有续传状态可读(旧版本或别的工具)：无法确认分片完整
        Assert.Equal([false, false], pending.Clips.Select(c => c.Complete));

        var failed = Assert.Single(DownloadFileGroups.Build(files, [], NoMetadata, titleFor: _ => (null, false)));
        Assert.Equal("未完成的下载（av115050127886063）", failed.Title);
        Assert.False(failed.TitlePending);
        Assert.True(failed.NeedsTitleLookup);

        var found = Assert.Single(DownloadFileGroups.Build(files, [], NoMetadata,
            titleFor: aid => (new VideoTitleInfo("查到的标题 " + aid, "UP主", "https://i0.hdslb.com/p.jpg", "BV1t1YxzWEkz"), false)));
        Assert.Equal("查到的标题 115050127886063", found.Title);
        Assert.Equal("UP主", found.Owner);
        Assert.Equal("https://i0.hdslb.com/p.jpg", found.Pic);
        Assert.False(found.TitlePending);
        Assert.False(found.NeedsTitleLookup);
    }

    [Fact]
    public void WorkFolderMustBeNamedAfterTheAidInTheFileNames()
    {
        // 文件夹名不是分片里的 av 号：不当作未完成的下载(删除整组不会删到文件夹里别的文件)
        var other = DownloadFileGroups.Build([F("tmp/00000_123.P2.456.aclip", 100), F("tmp/notes.txt")], [], NoMetadata);
        Assert.DoesNotContain(other, g => g.Status == FileGroupStatus.Incomplete);
        Assert.Equal(2, other.Count);

        var group = Assert.Single(DownloadFileGroups.Build([F("123/00000_123.P2.456.aclip", 100)], [], NoMetadata));
        Assert.Equal("w:123", group.Id);
        Assert.Equal("123", group.Aid);
        Assert.Equal("未完成的下载（av123）", group.Title);
        Assert.Equal(2, group.Clips[0].Page);
        // 没有续传状态：无法确认是否下载完整，按未完成显示
        Assert.False(group.Clips[0].Complete);
    }

    [Theory]
    [InlineData("资料", "foo.resume")]
    [InlineData("资料", "00000_123.P1.2.vclip.resume")]
    [InlineData("123", "foo.resume")]
    [InlineData("123", "456.P1.7.mp4.resume")]
    [InlineData("123", "00000_456.P1.7.vclip")]
    // 合并好的轨道旁的续传状态：只下载不混流时它和轨道就是输出
    [InlineData("123", "123.P1.456.mp4.resume")]
    public void ForeignResumeOrClipFiles_DoNotMakeAFolderAnIncompleteDownload(string folder, string name)
    {
        var files = new[] { F($"{folder}/{name}"), F($"{folder}/视频.mp4", 500) };

        var groups = DownloadFileGroups.Build(files, [], NoMetadata);

        Assert.DoesNotContain(groups, g => g.Status == FileGroupStatus.Incomplete);
        Assert.Single(groups, g => g.Id == $"s:{folder}/视频");
    }

    [Theory]
    [InlineData("123.P1.456.mp4.tmp")]
    [InlineData("123.P1.456.mp4.verify.tmp.resume")]
    [InlineData("123.P1.456.tmp.resume")]
    [InlineData("123.tmp.resume")]
    [InlineData("00000_123.P1.456.vclip.resume")]
    [InlineData("00001_123.456.P1.back_ground.aclip")]
    public void EngineWorkFilesNamedAfterTheFolder_MakeItAnIncompleteDownload(string name)
    {
        var group = Assert.Single(DownloadFileGroups.Build([F("123/" + name), F("123/123.jpg")], [], NoMetadata));

        Assert.Equal("w:123", group.Id);
        Assert.Equal(2, group.FileCount);
    }

    [Fact]
    public void HistoryMultiPageFolder_DoesNotSwallowOtherVideosWithoutHistory()
    {
        var files = new[]
        {
            F("合集/[P01]开场.mp4"), F("合集/[P02]正片.mp4"), F("合集/[P01]开场.zh-CN.srt"), F("合集/[P03]花絮.mp4"),
            F("合集/B.mp4", 300), F("合集/B.jpg"), F("合集/说明.txt"),
        };
        var history = new[] { Entry("A", "BV1AAAAAAAAA", 100, "合集/[P01]开场.mp4", "合集/[P02]正片.mp4") };

        var groups = DownloadFileGroups.Build(files, history, NoMetadata);

        // 之前版本下载的 P3 和同名字幕并入 A；没有记录的 B 和说明文件各自成组
        Assert.Equal(["合集/[P01]开场.mp4", "合集/[P02]正片.mp4", "合集/[P03]花絮.mp4", "合集/[P01]开场.zh-CN.srt"],
            Single(groups, "h:BV1AAAAAAAAA").Files.Select(f => f.Path));
        Assert.Equal(["合集/B.mp4", "合集/B.jpg"], Single(groups, "s:合集/B").Files.Select(f => f.Path));
        Assert.Single(groups, g => g.Id == "s:合集/说明");
        Assert.Equal(3, groups.Count);
    }

    [Fact]
    public void HiddenEngineFiles_CountTowardsTheIncompleteDownloadOnly()
    {
        var staged = "10/.10.P1.20.0123456789abcdef0123456789abcdef.partial.mp4";
        var files = new[] { F("10/00000_10.P1.20.vclip", 70, 1000), F("10/00000_10.P1.20.vclip.resume", 83, 1000), F("合集/[P01]a.mp4") };
        var hidden = new[]
        {
            F(staged, 1_400_000, 3000), F("10/" + DownloadWorkFolder.MetadataFileName, 300, 900),
            // 别的文件夹里的暂存文件不归入任何组
            F("合集/.[P02]b.0123456789abcdef0123456789abcdef.partial.mp4", 999),
            // 只有暂存文件、没有说明文件的旧文件夹也是未完成的下载
            F("20/.20.P1.30.0123456789abcdef0123456789abcdef.partial.m4a", 50, 800),
        };

        var groups = DownloadFileGroups.Build(files, [], NoMetadata, hiddenFiles: hidden);

        var work = Single(groups, "w:10");
        Assert.Equal(4, work.FileCount);
        Assert.Equal(70 + 83 + 1_400_000 + 300, work.TotalBytes);
        Assert.Equal(3000, work.ModifiedTime);
        Assert.Equal([staged], work.Files.Select(f => f.Path));
        // 说明文件由删除流程单独删除，不在成员里
        Assert.Equal(["10/.10.P1.20.0123456789abcdef0123456789abcdef.partial.mp4", "10/00000_10.P1.20.vclip", "10/00000_10.P1.20.vclip.resume"],
            work.MemberPaths);
        var stagedOnly = Single(groups, "w:20");
        Assert.Equal(1, stagedOnly.FileCount);
        Assert.Equal("20", stagedOnly.Aid);
        Assert.Equal(1, Single(groups, "s:合集/[P01]a").FileCount);
        Assert.Equal(3, groups.Count);
    }

    [Fact]
    public void VideoThatNoLongerExists_HasNoLinkToResumeFrom()
    {
        var files = new[] { F("999999999999999/00000_999999999999999.P1.1.vclip", Clip) };

        var group = Assert.Single(DownloadFileGroups.Build(files, [], NoMetadata, titleFor: _ => (VideoTitleInfo.Missing, false)));

        Assert.True(group.Unavailable);
        Assert.Null(group.Url);
        Assert.Null(group.Bvid);
        Assert.Null(group.PageUrl);
        Assert.False(group.NeedsTitleLookup);
        Assert.False(group.TitlePending);
        Assert.Equal("未完成的下载（av999999999999999）", group.Title);
    }

    [Fact]
    public void ClipCompleteness_ComesFromTheResumeStateWrittenByTheDownload()
    {
        var files = new[]
        {
            F("10/00000_10.P1.1.vclip", Clip), F("10/00000_10.P1.1.vclip.resume", 300),
            F("10/00001_10.P1.1.vclip", 500), F("10/00001_10.P1.1.vclip.resume", 300),
            F("10/00002_10.P1.1.vclip", 900), F("10/00002_10.P1.1.vclip.resume", 300),
            F("10/00003_10.P1.1.vclip", 700), F("10/00003_10.P1.1.vclip.resume", 300),
            // 没有续传状态
            F("10/00004_10.P1.1.vclip", 300),
        };
        var states = new Dictionary<string, DownloadResumeState?>
        {
            ["10/00000_10.P1.1.vclip.resume"] = State(Clip, Clip),
            // 只下载了一部分
            ["10/00001_10.P1.1.vclip.resume"] = State(Clip, 500, Clip),
            // 记下已完整，但本地文件长度不符(被改动过)
            ["10/00002_10.P1.1.vclip.resume"] = State(1000, 1000, 2L * Clip),
            // 读不出有效的状态(旧格式或损坏)
            ["10/00003_10.P1.1.vclip.resume"] = null,
        };

        var work = Assert.Single(DownloadFileGroups.Build(files, [], NoMetadata, path => states.GetValueOrDefault(path)));

        Assert.Equal([true, false, false, false, false], work.Clips.Select(c => c.Complete));
    }

    [Fact]
    public void FinishedSkipMuxTracksAndTheirResumeState_AreNotAnIncompleteDownload()
    {
        // 只下载不混流：轨道和它们旁边的续传状态(供重新下载时直接沿用)留在工作文件夹里，说明文件已删除
        var files = new[]
        {
            F("10/10.P1.20.mp4", 100), F("10/10.P1.20.mp4.resume", 300), F("10/10.P1.20.m4a", 50), F("10/10.P1.20.m4a.resume", 300),
        };

        var group = Assert.Single(DownloadFileGroups.Build(files, [], NoMetadata));

        Assert.Equal(FileGroupStatus.Complete, group.Status);
        Assert.Equal("s:10/10.P1.20", group.Id);
        Assert.Equal(4, group.FileCount);
        Assert.Equal("10/10.P1.20.mp4", group.MainFile);
    }

    [Fact]
    public void InternationalWorkFolder_IsRecognizedByItsMetadata()
    {
        // 国际站没有 av 号：工作文件夹是 intl_<epid>，靠说明文件识别
        var files = new[] { F("intl_123/00000_intl_123.P1.456.vclip", 70), F("intl_123/00000_intl_123.P1.456.vclip.resume", 300) };
        var metadata = new Dictionary<string, DownloadWorkMetadata>
        {
            ["intl_123"] = new() { Title = "国际站番剧", Request = new() { Url = "https://www.bilibili.tv/en/play/1/123", UseIntlApi = true } }
        };

        var group = Assert.Single(DownloadFileGroups.Build(files, [], metadata));

        Assert.Equal("w:intl_123", group.Id);
        Assert.Equal("国际站番剧", group.Title);
        Assert.Null(group.Aid);
        Assert.Equal("https://www.bilibili.tv/en/play/1/123", group.Url);
        Assert.Equal(1, group.ClipCount);
    }

    [Fact]
    public void FolderWithOnlyTheMetadataFileAndACover_IsStillAnIncompleteDownload()
    {
        var files = new[] { F("42/42.jpg") };
        var metadata = new Dictionary<string, DownloadWorkMetadata> { ["42"] = new() { Title = "刚开始就中断了", Aid = "42" } };

        var group = Assert.Single(DownloadFileGroups.Build(files, [], metadata));

        Assert.Equal(FileGroupStatus.Incomplete, group.Status);
        Assert.Equal(0, group.ClipCount);
        Assert.Equal(1, group.FileCount);
    }

    [Fact]
    public void HistoryOwnedOutputsInsideAWorkFolder_StayWithTheirVideo()
    {
        // 只下载不混流(SkipMux)：轨道本身就是输出，留在 aid 文件夹里；同时下一个分P中断留下了分片
        var files = new[]
        {
            F("10/10.P1.20.mp4", 100), F("10/10.P1.20.m4a", 50),
            F("10/00000_10.P2.30.vclip", 70), F("10/00000_10.P2.30.vclip.resume", 83),
        };
        var history = new[] { Entry("轨道", "BV1AAAAAAAAA", 100, "10/10.P1.20.mp4", "10/10.P1.20.m4a") };

        var groups = DownloadFileGroups.Build(files, history, NoMetadata);

        Assert.Equal(2, Single(groups, "h:BV1AAAAAAAAA").FileCount);
        var work = Single(groups, "w:10");
        Assert.Equal(1, work.ClipCount);
        Assert.Empty(work.Files);
    }

    [Fact]
    public void ActiveFlagComesFromTheCaller()
    {
        var files = new[] { F("10/00000_10.P1.20.vclip", 70, 5000) };

        var group = Assert.Single(DownloadFileGroups.Build(files, [], NoMetadata,
            isActive: (folder, aid, modified) => folder == "10" && aid == "10" && modified == 5000));

        Assert.True(group.Active);
    }

    [Theory]
    [InlineData("video.part2.zh-CN.srt", new[] { "video.part2.zh-CN", "video.part2", "video" })]
    [InlineData("a.mp4", new[] { "a" })]
    [InlineData(".hidden", new[] { ".hidden" })]
    public void CandidateStems_GoFromLongestToShortest(string name, string[] expected)
    {
        Assert.Equal(expected, DownloadFileGroups.CandidateStems(name));
    }
}
