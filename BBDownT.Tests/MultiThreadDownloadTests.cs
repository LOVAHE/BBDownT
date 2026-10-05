using System.Net;
using System.Net.Http.Headers;

namespace BBDownT.Tests;

public class MultiThreadDownloadTests
{
    [Fact]
    public async Task SameLengthOldTrackCannotReplaceTheNewResource()
    {
        using var files = new MediaTestDirectory();
        var destination = files.Write("track.mp4", "OLD!");
        var expectedClip = files.FilePath("00000_track.vclip");
        var requests = new List<string>();
        using var client = new HttpClient(new Handler(request =>
        {
            requests.Add(request.Headers.Range?.ToString() ?? "size probe");
            var response = new HttpResponseMessage(request.Headers.Range is null
                ? HttpStatusCode.OK : HttpStatusCode.PartialContent)
            {
                Content = new ByteArrayContent("NEW!"u8.ToArray())
            };
            if (request.Headers.Range is not null)
                response.Content.Headers.ContentRange = new ContentRangeHeaderValue(0, 3, 4);
            return response;
        }));

        var manifest = await BBDownTDownloadUtil.MultiThreadDownloadFileAsync(
            "https://cdn.test/new-quality.mp4", destination, new(), client);

        Assert.Equal(new[] { "size probe", "bytes=0-3" }, requests);
        Assert.Equal(new[] { expectedClip }, manifest);
        Assert.Equal("OLD!", File.ReadAllText(destination));
        BBDownTDownloadUtil.MergeTrackClips(manifest, destination);
        Assert.Equal("NEW!", File.ReadAllText(destination));
        Assert.False(File.Exists(expectedClip));
    }

    [Fact]
    public async Task FailedReplacementPreservesThePreviouslyCompletedTrack()
    {
        using var files = new MediaTestDirectory();
        var destination = files.Write("track.mp4", "OLD!");
        files.FilePath("00000_track.vclip");
        files.FilePath("00000_track.vclip.resume");
        using var client = new HttpClient(new Handler(request => request.Headers.Range is null
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent("NEW!"u8.ToArray()) }
            : throw new HttpRequestException("new resource unavailable")));

        // 零延迟注入: 否则该用例会为了验证重试而真的睡满退避预算
        await Assert.ThrowsAnyAsync<Exception>(() => BBDownTDownloadUtil.MultiThreadDownloadFileAsync(
            "https://cdn.test/new-quality.mp4", destination, new(), client, _ => Task.CompletedTask));

        Assert.Equal("OLD!", File.ReadAllText(destination));
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken) => Task.FromResult(respond(request));
    }
}
