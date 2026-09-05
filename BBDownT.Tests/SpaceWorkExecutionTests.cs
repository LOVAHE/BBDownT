using BBDownT.Core.Entity;

namespace BBDownT.Tests;

public class SpaceWorkExecutionTests
{
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
    public async Task FailedExport_NeverUsesAnExistingOldTxt()
    {
        var path = Path.GetTempFileName();
        var prepares = 0;
        try
        {
            await File.WriteAllTextAsync(path, VideoUrl);
            await Assert.ThrowsAsync<IOException>(() => Program.ExecuteWorkAsync(
                new MyOption { Url = SpaceUrl, DownloadAll = true }, prepare: _ =>
                {
                    prepares++;
                    throw new IOException("new export failed");
                }));
            Assert.Equal(1, prepares);
            Assert.Equal(VideoUrl, await File.ReadAllTextAsync(path));
        }
        finally { File.Delete(path); }
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

    private static SpaceVideoInfo SpaceInfo(string path) => new()
    {
        Title = "UP", Desc = "投稿清单", Pic = "", PubTime = 0, PagesInfo = [], UrlListFilePath = path
    };

    private static VInfo VideoInfo() => new()
    {
        Title = "Video", Desc = "", Pic = "", PubTime = 0, PagesInfo = []
    };
}
