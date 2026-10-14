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
                0, "https://example.test/media", path, 0, null, httpClient: client);

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
                0, "https://example.test/media", path, 0, null, httpClient: client);

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
                0, "https://example.test/media", path, 0, null, httpClient: client);

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
                0, "https://example.test/media", path, 0, null, httpClient: client);

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
                    0, "https://example.test/media", path, 0, null, httpClient: client));

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
                    0, "https://example.test/media", path, 10, 11, true, client));
        }
        finally
        {
            File.Delete(path);
            File.Delete(path + ".resume");
        }
    }

    [Fact]
    public async Task CompletePart_IsReusedWhenItsOwnProbeReturnsABareCdnTag()
    {
        const string url = "https://media.example.test/upgcxcode/01/123.m4s"
            + "?e=object&deadline=123&trid=synthetic-trace&upsig=synthetic-secret&uparams=e";
        const string tag = "b61d09ee67e4ba9f273afea2bbe248b1";
        var objectHash = DownloadMediaSource.CreateObjectHash(url);
        Assert.NotNull(objectHash);
        var path = Path.GetTempFileName();
        byte[] bytes = [1, 2, 3, 4];
        await File.WriteAllBytesAsync(path, bytes);
        await new DownloadResumeState("video", DownloadResumeState.SourceHash(url), 0, null, 4, true, 4,
            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)), new($"\"{tag}\"", null))
            { SourceObjectHash = objectHash }.SaveAsync(path + ".resume");
        try
        {
            var bodyRequests = 0;
            using var client = CreateClient(request =>
            {
                if (request.Headers.Range is not null) bodyRequests++;
                var response = CreateResponse(HttpStatusCode.OK, [], declaredLength: 4);
                response.Headers.TryAddWithoutValidation("ETag", tag);
                return response;
            });

            await BBDownTDownloadUtil.RangeDownloadToTmpAsync(0, url, path, 0, null, httpClient: client,
                resourceIdentity: "video", sourceObjectHash: objectHash);

            Assert.Equal(0, bodyRequests);
            Assert.Equal(bytes, await File.ReadAllBytesAsync(path));
        }
        finally
        {
            File.Delete(path);
            File.Delete(path + ".resume");
        }
    }

    [Theory]
    [InlineData("\"entity-v2\"", typeof(InvalidDataException))]
    [InlineData("\"entity-v1\"", typeof(NotSupportedException))]
    public async Task BoundedClip_FullResponseToIfRangeWithAnotherVersionIsAVersionChange(string entityTag, Type expected)
    {
        var path = Path.GetTempFileName();
        try
        {
            using var client = CreateClient(request =>
            {
                Assert.Equal("\"entity-v1\"", request.Headers.IfRange?.EntityTag?.ToString());
                return CreateResponse(HttpStatusCode.OK, new byte[12], entityTag: entityTag);
            });

            await Assert.ThrowsAsync(expected, () =>
                BBDownTDownloadUtil.RangeDownloadToTmpAsync(
                    0, "https://example.test/media", path, 10, 11, true, client,
                    expectedResource: new(12, new("\"entity-v1\"", null))));
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
                    failOnRangeNotSupported: to is not null, httpClient: client));

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
                    0, "https://example.test/media", path, 0, null, httpClient: client));
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
        return new HttpClient(new DelegateHandler(responder));
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
}
