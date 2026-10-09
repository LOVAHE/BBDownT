using System.Net;
using System.Net.Http.Headers;

namespace BBDownT.Tests;

public class RangeDownloadStateMachineTests
{
    [Fact]
    public async Task ExistingPartialFile_ResumesFromItsCurrentLength()
    {
        var path = Path.GetTempFileName();
        await File.WriteAllBytesAsync(path, [1, 2]);
        await SavePartialState(path, 4);
        try
        {
            using var client = CreateClient(request =>
            {
                Assert.Equal(2, request.Headers.Range?.Ranges.Single().From);
                Assert.Equal("\"entity-v1\"", request.Headers.IfRange?.EntityTag?.ToString());
                return CreateResponse(HttpStatusCode.PartialContent, [3, 4], 2, 3, 4, entityTag: "\"entity-v1\"");
            });

            await BBDownTDownloadUtil.RangeDownloadToTmpAsync(
                0, "https://example.test/media", path, 0, null, (_, _, _) => { }, httpClient: client);

            Assert.Equal(new byte[] { 1, 2, 3, 4 }, await File.ReadAllBytesAsync(path));
        }
        finally
        {
            File.Delete(path);
            File.Delete(path + ".resume");
        }
    }

    [Fact]
    public async Task FullResponse_TruncatesStalePartialFileBeforeWriting()
    {
        var path = Path.GetTempFileName();
        await File.WriteAllBytesAsync(path, [9, 9, 9, 9]);
        try
        {
            using var client = CreateClient(_ => CreateResponse(HttpStatusCode.OK, [1, 2]));

            await BBDownTDownloadUtil.RangeDownloadToTmpAsync(
                0, "https://example.test/media", path, 0, null, (_, _, _) => { }, httpClient: client);

            Assert.Equal(new byte[] { 1, 2 }, await File.ReadAllBytesAsync(path));
        }
        finally
        {
            File.Delete(path);
            File.Delete(path + ".resume");
        }
    }

    [Fact]
    public async Task ChangedEntity_RestartsWhenIfRangeProducesFullResponse()
    {
        var path = Path.GetTempFileName();
        await File.WriteAllBytesAsync(path, [9, 9]);
        await SavePartialState(path, 4);
        try
        {
            using var client = CreateClient(request =>
            {
                Assert.Equal("\"entity-v1\"", request.Headers.IfRange?.EntityTag?.ToString());
                return CreateResponse(HttpStatusCode.OK, [1, 2, 3], entityTag: "\"entity-v2\"");
            });

            await BBDownTDownloadUtil.RangeDownloadToTmpAsync(
                0, "https://example.test/media", path, 0, null, (_, _, _) => { }, httpClient: client);

            Assert.Equal(new byte[] { 1, 2, 3 }, await File.ReadAllBytesAsync(path));
        }
        finally
        {
            File.Delete(path);
            File.Delete(path + ".resume");
        }
    }

    [Fact]
    public async Task CompleteTemporaryFile_IsAcceptedWhenRangeReturnsMatchingEof()
    {
        var path = Path.GetTempFileName();
        await File.WriteAllBytesAsync(path, [1, 2, 3, 4]);
        await SavePartialState(path, 4);
        try
        {
            using var client = CreateClient(request =>
            {
                Assert.Equal(4, request.Headers.Range?.Ranges.Single().From);
                var response = CreateResponse(HttpStatusCode.RequestedRangeNotSatisfiable, [], entityTag: "\"entity-v1\"");
                response.Content.Headers.ContentRange = new ContentRangeHeaderValue(4);
                return response;
            });

            await BBDownTDownloadUtil.RangeDownloadToTmpAsync(
                0, "https://example.test/media", path, 0, null, (_, _, _) => { }, httpClient: client);

            Assert.Equal(new byte[] { 1, 2, 3, 4 }, await File.ReadAllBytesAsync(path));
            Assert.True((await DownloadResumeState.LoadAsync(path + ".resume"))!.Complete);
        }
        finally
        {
            File.Delete(path);
            File.Delete(path + ".resume");
        }
    }

    [Fact]
    public async Task InvalidRangeAtEof_PreservesTemporaryFileForReparse()
    {
        var path = Path.GetTempFileName();
        await File.WriteAllBytesAsync(path, [1, 2, 3, 4]);
        await SavePartialState(path, 4);
        try
        {
            using var client = CreateClient(_ =>
            {
                var response = CreateResponse(HttpStatusCode.RequestedRangeNotSatisfiable, [], entityTag: "\"entity-v2\"");
                response.Content.Headers.ContentRange = new ContentRangeHeaderValue(4);
                return response;
            });

            await Assert.ThrowsAsync<IOException>(() =>
                BBDownTDownloadUtil.RangeDownloadToTmpAsync(
                    0, "https://example.test/media", path, 0, null, (_, _, _) => { }, httpClient: client));

            Assert.Equal(new byte[] { 1, 2, 3, 4 }, await File.ReadAllBytesAsync(path));
            Assert.True(File.Exists(path + ".resume"));
        }
        finally
        {
            File.Delete(path);
            File.Delete(path + ".resume");
        }
    }

    [Fact]
    public async Task BoundedClip_RejectsServerThatIgnoresRange()
    {
        var path = Path.GetTempFileName();
        try
        {
            using var client = CreateClient(_ => CreateResponse(HttpStatusCode.OK, [1, 2]));

            await Assert.ThrowsAsync<NotSupportedException>(() =>
                BBDownTDownloadUtil.RangeDownloadToTmpAsync(
                    0, "https://example.test/media", path, 10, 11, (_, _, _) => { }, true, client));
        }
        finally
        {
            File.Delete(path);
            File.Delete(path + ".resume");
        }
    }

    [Theory]
    [InlineData(HttpStatusCode.PartialContent, 2, 2L, 3L, 4L, 0L, null, "与请求起点不一致")]
    [InlineData(HttpStatusCode.PartialContent, 2, 0L, 1L, 4L, 0L, null, "未到达资源末尾")]
    [InlineData(HttpStatusCode.PartialContent, 1, 10L, 10L, 20L, 10L, 11L, "未完整覆盖请求范围")]
    [InlineData(HttpStatusCode.NoContent, 0, null, null, null, 0L, null, "不支持的下载响应状态: 204")]
    public async Task InvalidResponses_AreRejectedByTheirOwnCheck(HttpStatusCode status, int bodyLength,
        long? rangeStart, long? rangeEnd, long? totalLength, long from, long? to, string message)
    {
        var path = Path.GetTempFileName();
        try
        {
            using var client = CreateClient(_ => CreateResponse(status, new byte[bodyLength], rangeStart, rangeEnd, totalLength));

            var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
                BBDownTDownloadUtil.RangeDownloadToTmpAsync(0, "https://example.test/media", path, from, to,
                    (_, _, _) => { }, failOnRangeNotSupported: to is not null, httpClient: client));

            Assert.Contains(message, error.Message);
        }
        finally
        {
            File.Delete(path);
            File.Delete(path + ".resume");
        }
    }

    [Fact]
    public async Task ResponseShorterThanDeclaredLength_IsRejected()
    {
        var path = Path.GetTempFileName();
        try
        {
            using var client = CreateClient(_ =>
                CreateResponse(HttpStatusCode.OK, [1, 2], declaredLength: 4));

            await Assert.ThrowsAsync<BBDownT.Core.Util.DownloadInterruptedException>(() =>
                BBDownTDownloadUtil.RangeDownloadToTmpAsync(
                    0, "https://example.test/media", path, 0, null, (_, _, _) => { }, httpClient: client));
        }
        finally
        {
            File.Delete(path);
            File.Delete(path + ".resume");
        }
    }

    private static async Task SavePartialState(string path, long total)
    {
        const string url = "https://example.test/media";
        var bytes = await File.ReadAllBytesAsync(path);
        await new DownloadResumeState(DownloadResumeState.Scope(url, null), DownloadResumeState.SourceHash(url),
            0, null, total, false, bytes.Length,
            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)), new("\"entity-v1\"", null))
            .SaveAsync(path + ".resume");
    }

    private static HttpClient CreateClient(Func<HttpRequestMessage, HttpResponseMessage> responder)
    {
        return new HttpClient(new StubHandler(responder));
    }

    private static HttpResponseMessage CreateResponse(
        HttpStatusCode statusCode,
        byte[] content,
        long? rangeStart = null,
        long? rangeEnd = null,
        long? totalLength = null,
        long? declaredLength = null,
        string? entityTag = null)
    {
        var response = new HttpResponseMessage(statusCode)
        {
            Content = new ByteArrayContent(content)
        };
        if (rangeStart is not null && rangeEnd is not null && totalLength is not null)
        {
            response.Content.Headers.ContentRange = new ContentRangeHeaderValue(
                rangeStart.Value,
                rangeEnd.Value,
                totalLength.Value);
        }
        if (declaredLength is not null)
        {
            response.Content.Headers.ContentLength = declaredLength;
        }
        if (entityTag is not null)
        {
            response.Headers.ETag = EntityTagHeaderValue.Parse(entityTag);
        }
        return response;
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(responder(request));
        }
    }
}
