using BBDownT.Core.Entity;
using BBDownT.Core.Fetcher;

namespace BBDownT.Tests;

public class SpaceVideoFetcherTests
{
    [Fact]
    public async Task Export_WritesAllPagesBeforeReturningSuccessfulResult()
    {
        var calls = new List<string>();
        var responses = new Queue<string>([
            """{"data":{"info":{"uname":"UP/name"}}}""",
            Page(51, 1), Page(51, 2)
        ]);
        string? savedPath = null;
        string? savedContent = null;
        var writeCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var writeStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = SpaceVideoFetcher.FetchAsync("mid:42", url =>
        {
            calls.Add(url);
            return Task.FromResult(responses.Dequeue());
        }, async (path, content) =>
        {
            savedPath = path;
            savedContent = content;
            writeStarted.SetResult();
            await writeCompleted.Task;
        });
        try
        {
            await writeStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(pending.IsCompleted);
            Assert.Equal(3, calls.Count);
            Assert.Equal("https://api.live.bilibili.com/live_user/v1/Master/info?uid=42", calls[0]);
            Assert.Contains("mid=42&order=pubdate&pn=1&ps=50&tid=0&wts=", calls[1]);
            Assert.Contains("&pn=2&", calls[2]);
            Assert.EndsWith("UP.name的投稿视频.txt", savedPath);
            Assert.Equal(string.Join(Environment.NewLine, Url(1), Url(2)), savedContent);
        }
        finally
        {
            writeCompleted.TrySetResult();
        }
        var result = Assert.IsType<SpaceVideoInfo>(await pending);
        Assert.Equal(savedPath, result.UrlListFilePath);
        Assert.Equal("UP.name", result.Title);
        Assert.Empty(result.PagesInfo);
    }

    [Fact]
    public async Task LaterPageFailure_DoesNotWriteAPartialList()
    {
        var responses = new Queue<string>(["""{"data":{"info":{"uname":"UP"}}}""", Page(51, 1)]);
        var writes = 0;

        await Assert.ThrowsAsync<InvalidOperationException>(() => SpaceVideoFetcher.FetchAsync("mid:42",
            _ => responses.Count > 0 ? Task.FromResult(responses.Dequeue()) : throw new InvalidOperationException("fixture failure"),
            (_, _) => { writes++; return Task.CompletedTask; }));

        Assert.Equal(0, writes);
    }

    [Fact]
    public async Task FailedFileWrite_DoesNotReturnSuccessfulExport()
    {
        var responses = new Queue<string>(["""{"data":{"info":{"uname":"UP"}}}""", Page(1, 1)]);

        await Assert.ThrowsAsync<IOException>(() => SpaceVideoFetcher.FetchAsync("mid:42",
            _ => Task.FromResult(responses.Dequeue()), (_, _) => throw new IOException("write failed")));
    }

    [Fact]
    public async Task EmptySpace_ExportsAnEmptyFileSuccessfully()
    {
        var responses = new Queue<string>([
            """{"data":{"info":{"uname":"UP"}}}""",
            """{"data":{"list":{"vlist":[]},"page":{"count":0}}}"""
        ]);
        string? saved = null;

        var info = await SpaceVideoFetcher.FetchAsync("mid:42", _ => Task.FromResult(responses.Dequeue()),
            (_, text) => { saved = text; return Task.CompletedTask; });

        Assert.IsType<SpaceVideoInfo>(info);
        Assert.Equal("", saved);
    }

    private static string Url(int aid) => $"https://www.bilibili.com/video/av{aid}";
    private static string Page(int count, int aid) =>
        System.Text.Json.JsonSerializer.Serialize(new
        {
            data = new { list = new { vlist = new[] { new { aid } } }, page = new { count } }
        });
}
