using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;

namespace BBDownT.Tests;

public class CompletedClipReuseTests
{
    [Fact]
    public async Task CompletedClipWithMatchingEntity_IsValidatedWithoutRedownload()
    {
        using var files = new MediaTestDirectory();
        var clip = files.Write("00000_track.vclip", "ABCD");
        var sidecar = files.FilePath("00000_track.vclip.resume");
        await new DownloadResumeValidator("\"v1\"", null).SaveAsync(sidecar);
        var ranges = new List<string>();
        var progress = new List<long>();
        using var client = new HttpClient(new Handler(request =>
        {
            ranges.Add(request.Headers.Range?.ToString() ?? "-");
            // 单字节探针: 实体校验器一致即说明这个分片仍是同一份资源
            return Partial("A"u8.ToArray(), 0, 0, 8, "\"v1\"");
        }));

        await BBDownTDownloadUtil.RangeDownloadToTmpAsync(
            0, "https://cdn.test/track.m4s", clip, 0, 3, (_, downloaded, _) => progress.Add(downloaded),
            httpClient: client);

        Assert.Equal(new[] { "bytes=0-0" }, ranges);
        Assert.Equal("ABCD", File.ReadAllText(clip));
        Assert.True(File.Exists(sidecar));
        Assert.Equal(new[] { 4L }, progress);
    }

    [Fact]
    public async Task CompletedClipWithChangedEntity_IsRedownloadedFromScratch()
    {
        using var files = new MediaTestDirectory();
        var clip = files.Write("00000_track.vclip", "ABCD");
        var sidecar = files.FilePath("00000_track.vclip.resume");
        await new DownloadResumeValidator("\"v1\"", null).SaveAsync(sidecar);
        var ranges = new List<string>();
        using var client = new HttpClient(new Handler(request =>
        {
            var range = request.Headers.Range?.Ranges.FirstOrDefault();
            ranges.Add(range is null ? "-" : $"bytes={range.From}-{range.To}");
            return range?.To == 0
                ? Partial("A"u8.ToArray(), 0, 0, 8, "\"v2\"")
                : Partial("NEW!"u8.ToArray(), 0, 3, 4, "\"v2\"");
        }));

        await BBDownTDownloadUtil.RangeDownloadToTmpAsync(
            0, "https://cdn.test/track.m4s", clip, 0, 3, (_, _, _) => { }, httpClient: client);

        Assert.Equal(new[] { "bytes=0-0", "bytes=0-3" }, ranges);
        Assert.Equal("NEW!", File.ReadAllText(clip));
    }

    [Fact]
    public async Task CompletedClipWithTransientProbeError_IsPreservedAndRethrown()
    {
        using var files = new MediaTestDirectory();
        var clip = files.Write("00000_track.vclip", "ABCD");
        var sidecar = files.FilePath("00000_track.vclip.resume");
        await new DownloadResumeValidator("\"v1\"", null).SaveAsync(sidecar);
        var ranges = new List<string>();
        using var client = new HttpClient(new Handler(request =>
        {
            ranges.Add(request.Headers.Range?.ToString() ?? "-");
            // 链路故障(DNS 解析失败)不代表远端实体已变, 不能据此丢弃已下完的分片
            throw new HttpRequestException("不知道这样的主机。 (upos.test:443)", new SocketException(11001));
        }));

        await Assert.ThrowsAsync<HttpRequestException>(() => BBDownTDownloadUtil.RangeDownloadToTmpAsync(
            0, "https://cdn.test/track.m4s", clip, 0, 3, (_, _, _) => { }, httpClient: client));

        Assert.Equal("ABCD", File.ReadAllText(clip));
        Assert.True(File.Exists(sidecar));
        // 只发了探活请求: 网络恢复之前不会把整片清零重下
        Assert.Equal(new[] { "bytes=0-0" }, ranges);
    }

    [Fact]
    public async Task CompletedClipWithNonTransientProbeError_IsRedownloadedFromScratch()
    {
        using var files = new MediaTestDirectory();
        var clip = files.Write("00000_track.vclip", "ABCD");
        var sidecar = files.FilePath("00000_track.vclip.resume");
        await new DownloadResumeValidator("\"v1\"", null).SaveAsync(sidecar);
        var ranges = new List<string>();
        using var client = new HttpClient(new Handler(request =>
        {
            var range = request.Headers.Range?.Ranges.FirstOrDefault();
            ranges.Add(range is null ? "-" : $"bytes={range.From}-{range.To}");
            if (range?.To == 0) throw new InvalidDataException("探活响应无法解析");
            return Partial("NEW!"u8.ToArray(), 0, 3, 4, "\"v1\"");
        }));

        await BBDownTDownloadUtil.RangeDownloadToTmpAsync(
            0, "https://cdn.test/track.m4s", clip, 0, 3, (_, _, _) => { }, httpClient: client);

        // 探活拿到确定答复(非链路故障)时, 仍按"实体可能已变"截断重下
        Assert.Equal(new[] { "bytes=0-0", "bytes=0-3" }, ranges);
        Assert.Equal("NEW!", File.ReadAllText(clip));
    }

    [Fact]
    public async Task CompletedClipWithoutValidator_IsRedownloadedFromScratch()
    {
        using var files = new MediaTestDirectory();
        var clip = files.Write("00000_track.vclip", "OLD!");
        var ranges = new List<string>();
        using var client = new HttpClient(new Handler(request =>
        {
            ranges.Add(request.Headers.Range?.ToString() ?? "-");
            return Partial("NEW!"u8.ToArray(), 0, 3, 4);
        }));

        await BBDownTDownloadUtil.RangeDownloadToTmpAsync(
            0, "https://cdn.test/track.m4s", clip, 0, 3, (_, _, _) => { }, httpClient: client);

        Assert.Equal(new[] { "bytes=0-3" }, ranges);
        Assert.Equal("NEW!", File.ReadAllText(clip));
    }

    [Fact]
    public async Task PartialClipResumesAndKeepsValidatorUntilMerge()
    {
        using var files = new MediaTestDirectory();
        var clip = files.Write("00000_track.vclip", "AB");
        var sidecar = files.FilePath("00000_track.vclip.resume");
        await new DownloadResumeValidator("\"v1\"", null).SaveAsync(sidecar);
        var ranges = new List<string>();
        using var client = new HttpClient(new Handler(request =>
        {
            ranges.Add(request.Headers.Range?.ToString() ?? "-");
            return Partial("CD"u8.ToArray(), 2, 3, 4, "\"v1\"");
        }));

        await BBDownTDownloadUtil.RangeDownloadToTmpAsync(
            0, "https://cdn.test/track.m4s", clip, 0, 3, (_, _, _) => { }, httpClient: client);

        Assert.Equal(new[] { "bytes=2-3" }, ranges);
        Assert.Equal("ABCD", File.ReadAllText(clip));
        Assert.True(File.Exists(sidecar));
    }

    [Fact]
    public async Task SingleThreadCompletionRemovesValidator()
    {
        using var files = new MediaTestDirectory();
        var temporary = files.FilePath("track.tmp");
        var sidecar = files.FilePath("track.tmp.resume");
        using var client = new HttpClient(new Handler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent("FULL"u8.ToArray())
        }));
        await new DownloadResumeValidator("\"v1\"", null).SaveAsync(sidecar);

        await BBDownTDownloadUtil.RangeDownloadToTmpAsync(
            0, "https://cdn.test/track.m4s", temporary, 0, null, (_, _, _) => { }, httpClient: client);

        Assert.Equal("FULL", File.ReadAllText(temporary));
        Assert.False(File.Exists(sidecar));
    }

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
