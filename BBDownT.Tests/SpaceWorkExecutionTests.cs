using BBDownT.Core.Entity;

namespace BBDownT.Tests;

public class SpaceWorkExecutionTests : IDisposable
{
    private readonly WorkStateScope state = new();

    public void Dispose() => state.Dispose();

    private const string SpaceUrl = "https://space.bilibili.com/42";
    private const string VideoUrl = "https://www.bilibili.com/video/av1";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SharedCliApiExecution_ExportsAndOnlyDownloadsWhenRequested(bool downloadAll)
    {
        var path = Path.GetTempFileName();
        var events = new List<string>();
        var task = new DownloadTask("", SpaceUrl, 1);
        var option = new MyOption { Url = SpaceUrl, DownloadAll = downloadAll, DelayPerVideo = 0 };
        try
        {
            await Program.ExecuteWorkAsync(option, task, prepare: async input =>
            {
                events.Add("prepare:" + input.Url);
                if (input.Url == SpaceUrl)
                {
                    await File.WriteAllTextAsync(path, VideoUrl);
                    return new PreparedVideoDownload("mid:42", SpaceInfo(path),
                        _ => throw new InvalidOperationException("Export must not run the media page pipeline"));
                }
                Assert.False(input.DownloadAll);
                Assert.Equal(VideoUrl, input.Url);
                return new PreparedVideoDownload("1", VideoInfo(), related =>
                {
                    Assert.Same(task, related);
                    Assert.True(File.Exists(path));
                    events.Add("download:" + input.Url);
                    related!.AddSavePath("video.mp4");
                    return Task.CompletedTask;
                });
            });

            var expected = downloadAll
                ? new[] { "prepare:" + SpaceUrl, "prepare:" + VideoUrl, "download:" + VideoUrl }
                : new[] { "prepare:" + SpaceUrl };
            Assert.Equal(expected, events);
            Assert.Equal("mid:42", task.Aid);
            Assert.Equal("UP", task.Title);
            Assert.Equal(downloadAll ? new[] { path, "video.mp4" } : new[] { path }, task.CreateSnapshot().SavePaths);
            Assert.Equal(SpaceUrl, option.Url);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task FailedExport_DoesNotDownloadAnything()
    {
        var prepares = 0;
        await Assert.ThrowsAsync<IOException>(() => Program.ExecuteWorkAsync(
            new MyOption { Url = SpaceUrl, DownloadAll = true }, prepare: _ =>
            {
                prepares++;
                throw new IOException("new export failed");
            }));
        Assert.Equal(1, prepares);
    }

    [Fact]
    public async Task SingleVideo_StillRunsItsPreparedPagePipelineOnce()
    {
        var task = new DownloadTask("1", VideoUrl, 1);
        var option = new MyOption { Url = VideoUrl };
        var downloads = 0;

        await Program.ExecuteWorkAsync(option, task, prepare: input =>
        {
            Assert.Same(option, input);
            return Task.FromResult(new PreparedVideoDownload("1", VideoInfo(), related =>
            {
                Assert.Same(task, related);
                downloads++;
                return Task.CompletedTask;
            }));
        });

        Assert.Equal(1, downloads);
        Assert.Equal("Video", task.Title);
    }

    [Fact]
    public async Task TaskAid_IsTheOneThePreparationEndedWith()
    {
        var task = new DownloadTask("ep:1", "ep1", 1);

        await Program.ExecuteWorkAsync(new MyOption { Url = "ep1" }, task, prepare: _ =>
            Task.FromResult(new PreparedVideoDownload("cheese:1", VideoInfo(), _ => Task.CompletedTask)));

        Assert.Equal("cheese:1", task.Aid);
    }

    [Fact]
    public async Task ShortLinkToASpace_IsResolvedBeforeTheSpaceChecks()
    {
        const string shortLink = "https://b23.tv/space";
        const string resolved = SpaceUrl + "?share_source=copy_link";
        var path = Path.GetTempFileName();
        var option = new MyOption { Url = shortLink, DownloadAll = true, OnlyShowInfo = true };
        var resolutions = new List<string>();
        try
        {
            await Program.ExecuteWorkAsync(option, prepare: async input =>
            {
                Assert.Equal(resolved, input.Url);
                Assert.True(SpaceBatchDownload.IsExportOnly(input));
                Assert.False(Program.NeedsMuxer(input));
                await File.WriteAllTextAsync(path, VideoUrl);
                return new PreparedVideoDownload("mid:42", SpaceInfo(path),
                    _ => throw new InvalidOperationException("Export must not run the media page pipeline"));
            }, resolveShortLink: url =>
            {
                resolutions.Add(url);
                return Task.FromResult(resolved);
            });

            Assert.Equal(new[] { shortLink }, resolutions);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task DownloadAllOnAShortLinkToAVideo_IsRejectedAfterResolution()
    {
        var error = await Assert.ThrowsAsync<ArgumentException>(() => Program.ExecuteWorkAsync(
            new MyOption { Url = "https://b23.tv/video", DownloadAll = true },
            prepare: _ => throw new InvalidOperationException("Invalid options must not be prepared"),
            resolveShortLink: _ => Task.FromResult(VideoUrl)));

        Assert.Contains("--download-all", error.Message);
    }

    [Fact]
    public async Task ShortLinkToOnePart_SelectsThatPart()
    {
        List<string>? selected = null;

        await Program.ExecuteWorkAsync(new MyOption { Url = "https://b23.tv/part" }, prepare: input =>
        {
            var info = VideoInfo();
            info.PagesInfo = Enumerable.Range(1, 40)
                .Select(index => new Entity.Page(index, "1", index.ToString(), "", "Part", 1, 0)).ToList();
            selected = Program.GetSelectedPages(input, info);
            return Task.FromResult(new PreparedVideoDownload("1", info, _ => Task.CompletedTask));
        }, resolveShortLink: _ => Task.FromResult("https://www.bilibili.com/video/BV1xx411c7mD?p=37&share_source=copy"));

        Assert.Equal(new[] { "37" }, selected);
    }

    private static SpaceVideoInfo SpaceInfo(string path) => new()
    {
        Title = "UP", Desc = "投稿清单", Pic = "", PubTime = 0, PagesInfo = [], UrlListFilePath = path
    };

    private static VInfo VideoInfo() => new()
    {
        Title = "Video", Desc = "", Pic = "", PubTime = 0, PagesInfo = []
    };
}
