using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;

namespace BBDownT.Tests;

public class ClipDownloadRetryTests
{
    [Fact]
    public async Task TransientClipFailure_IsRetriedWithBackoffAndCompletes()
    {
        using var files = new MediaTestDirectory();
        var destination = files.FilePath("track.mp4");
        var expectedClip = files.FilePath("00000_track.vclip");
        var delays = new List<int>();
        var rangeAttempts = 0;
        using var client = new HttpClient(new Handler(request =>
        {
            if (request.Headers.Range is null) return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent("NEW!"u8.ToArray())
            };
            rangeAttempts++;
            if (rangeAttempts == 1) throw new HttpRequestException("临时断网", new SocketException(11001));
            return Partial("NEW!"u8.ToArray(), 0, 3, 4);
        }));

        var manifest = await BBDownTDownloadUtil.MultiThreadDownloadFileAsync(
            "https://cdn.test/track.m4s", destination, new(), client, Record(delays));

        Assert.Equal(new[] { expectedClip }, manifest);
        Assert.Equal("NEW!", File.ReadAllText(expectedClip));
        Assert.Equal(2, rangeAttempts);
        Assert.Equal(new[] { 3000 }, delays);
    }

    [Fact]
    public async Task ExhaustedClipFailure_ReportsRootCauseAndNeverReturnsPartialManifest()
    {
        using var files = new MediaTestDirectory();
        var destination = files.FilePath("track.mp4");
        files.FilePath("00000_track.vclip");
        var delays = new List<int>();
        var rangeAttempts = 0;
        using var client = new HttpClient(new Handler(request =>
        {
            if (request.Headers.Range is null)
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent("NEW!"u8.ToArray()) };
            rangeAttempts++;
            throw new HttpRequestException("不知道这样的主机。 (upos.test:443)", new SocketException(11001));
        }));

        var error = await Assert.ThrowsAsync<Exception>(() => BBDownTDownloadUtil.MultiThreadDownloadFileAsync(
            "https://cdn.test/track.m4s", destination, new(), client, Record(delays)));

        Assert.Contains("分片 0", error.Message);
        Assert.Contains("不知道这样的主机", error.Message);
        Assert.IsType<HttpRequestException>(error.InnerException);
        Assert.Equal(BBDownTDownloadUtil.ClipMaxAttempts, rangeAttempts);
        Assert.Equal(new[] { 3000, 6000, 12000, 15000 }, delays);
    }

    [Fact]
    public async Task TransientProbeFailure_KeepsCompletedClipAndSaysSo()
    {
        using var files = new MediaTestDirectory();
        var destination = files.FilePath("track.mp4");
        var expectedClip = files.Write("00000_track.vclip", "ABCD");
        var sidecar = files.FilePath("00000_track.vclip.resume");
        await new DownloadResumeValidator("\"v1\"", null).SaveAsync(sidecar);
        var delays = new List<int>();
        var probeAttempts = 0;
        var refetchRequests = 0;
        using var client = new HttpClient(new Handler(request =>
        {
            var range = request.Headers.Range?.Ranges.FirstOrDefault();
            if (range is null)
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent("NEW!"u8.ToArray()) };
            if (range.To == 0)
            {
                probeAttempts++;
                throw new HttpRequestException("不知道这样的主机。 (upos.test:443)", new SocketException(11001));
            }
            refetchRequests++;
            return Partial("NEW!"u8.ToArray(), 0, 3, 4);
        }));

        var error = await Assert.ThrowsAsync<Exception>(() => BBDownTDownloadUtil.MultiThreadDownloadFileAsync(
            "https://cdn.test/track.m4s", destination, new(), client, Record(delays)));

        Assert.Contains("已下载分片均已保留", error.Message);
        Assert.IsType<HttpRequestException>(error.InnerException);
        Assert.Equal(BBDownTDownloadUtil.ClipMaxAttempts, probeAttempts);
        // 链路故障期间不应把已下完的分片清零重下
        Assert.Equal(0, refetchRequests);
        Assert.Equal("ABCD", File.ReadAllText(expectedClip));
    }

    [Fact]
    public async Task UnsupportedRange_ThrowsActionableHintWithoutBackoff()
    {
        using var files = new MediaTestDirectory();
        var destination = files.FilePath("track.mp4");
        files.FilePath("00000_track.vclip");
        var delays = new List<int>();
        var rangeAttempts = 0;
        // 服务端忽略 Range: 分片请求也返回整份 200, 多线程下载无从推进
        using var client = new HttpClient(new Handler(request =>
        {
            if (request.Headers.Range is not null) rangeAttempts++;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent("NEW!"u8.ToArray()) };
        }));

        var error = await Assert.ThrowsAsync<NotSupportedException>(() => BBDownTDownloadUtil.MultiThreadDownloadFileAsync(
            "https://cdn.test/track.m4s", destination, new(), client, Record(delays)));

        Assert.Contains("--multi-thread false", error.Message);
        Assert.Equal(1, rangeAttempts);
        Assert.Empty(delays);
    }

    private static Func<int, Task> Record(List<int> delays) => milliseconds =>
    {
        delays.Add(milliseconds);
        return Task.CompletedTask;
    };

    private static HttpResponseMessage Partial(byte[] content, long from, long to, long total, string? entityTag = null)
    {
        var response = new HttpResponseMessage(HttpStatusCode.PartialContent)
        {
            Content = new ByteArrayContent(content)
        };
        response.Content.Headers.ContentRange = new ContentRangeHeaderValue(from, to, total);
        if (entityTag is not null) response.Headers.ETag = EntityTagHeaderValue.Parse(entityTag);
        return response;
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken) => Task.FromResult(respond(request));
    }
}
