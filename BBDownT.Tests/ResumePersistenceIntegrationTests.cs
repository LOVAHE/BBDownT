using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;

namespace BBDownT.Tests;

// These file lifecycle tests use the repository's existing ownership fixture.
public class ResumePersistenceIntegrationTests
{
    [Fact]
    public async Task MultiplePartsWithoutValidators_CompareContiguousOldPrefixAgainstOneFullNewResponse()
    {
        const int part = 20 * 1024 * 1024;
        const string oldUrl = "https://cdn.test/media?signature=old";
        const string newUrl = "https://cdn.test/media?signature=new";
        const string identity = "episode-video-720-avc";
        using var files = new MediaTestDirectory();
        var destination = files.FilePath("track.mp4");
        files.FilePath("track.mp4.resume");
        var temporary = files.FilePath("track.mp4.tmp");
        files.FilePath("track.mp4.tmp.resume");
        var first = files.FilePath("00000_track.vclip");
        files.FilePath("00000_track.vclip.resume");
        var second = files.FilePath("00001_track.vclip");
        files.FilePath("00001_track.vclip.resume");
        var old = new byte[part + 8];
        Array.Fill(old, (byte)42);
        await File.WriteAllBytesAsync(first, old.AsMemory(0, part).ToArray());
        await File.WriteAllBytesAsync(second, old.AsMemory(part, 2).ToArray());
        await new DownloadResumeState(identity, DownloadResumeState.SourceHash(oldUrl), 0, part - 1,
            old.Length, true, part, Convert.ToHexString(SHA256.HashData(old.AsSpan(0, part))), new(null, null))
            .SaveAsync(first + ".resume");
        await new DownloadResumeState(identity, DownloadResumeState.SourceHash(oldUrl), part, old.Length - 1,
            old.Length, false, 2, Convert.ToHexString(SHA256.HashData(old.AsSpan(part, 2))), new(null, null))
            .SaveAsync(second + ".resume");
        var current = (byte[])old.Clone();
        current[part / 2] = 99; // First and last bytes remain equal; sparse sampling would miss this.
        using var source = new NoValidatorSource(current, request =>
        {
            Assert.Equal(newUrl, request.RequestUri!.OriginalString);
            var range = Assert.Single(request.Headers.Range!.Ranges);
            Assert.Equal(0L, range.From);
            Assert.Null(range.To);
            Assert.Null(request.Headers.IfRange);
            // The fallback must seed the whole contiguous old prefix before verification.
            Assert.Equal((long)part + 2, new FileInfo(temporary).Length);
        });
        using var client = new HttpClient(source);

        var clips = await BBDownTDownloadUtil.MultiThreadDownloadFileAsync(newUrl, destination,
            new() { ResourceIdentity = identity }, client);

        Assert.Empty(clips); // The full-response fallback has already produced the track.
        Assert.Equal(1, source.Probes);
        Assert.Equal(1, source.BodyRequests);
        Assert.Equal(current.Length, source.BodyBytesRead);
        Assert.Equal(current, await File.ReadAllBytesAsync(destination));
        var completed = await DownloadResumeState.LoadAsync(destination + ".resume");
        Assert.NotNull(completed);
        Assert.True(completed.Complete);
        Assert.False(completed.Validator.IsUsable);
        Assert.Equal(DownloadResumeState.SourceHash(newUrl), completed.SourceUriHash);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(current)), completed.LocalSha256);
        Assert.False(File.Exists(first));
        Assert.False(File.Exists(first + ".resume"));
        Assert.False(File.Exists(second));
        Assert.False(File.Exists(second + ".resume"));
        Assert.False(File.Exists(temporary));
        Assert.False(File.Exists(temporary + ".resume"));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task InterruptedSession_ReparsePreservesPartsAndValidatesChangedSignedUrl(bool changedUrl, bool changedBytes)
    {
        const int part = 20 * 1024 * 1024;
        const string original = "https://cdn.test/media?signature=old";
        using var files = new MediaTestDirectory();
        var destination = files.FilePath("track.mp4");
        files.FilePath("track.mp4.resume");
        var first = files.FilePath("00000_track.vclip");
        files.FilePath("00000_track.vclip.resume");
        var second = files.FilePath("00001_track.vclip");
        files.FilePath("00001_track.vclip.resume");
        var bytes = new byte[part + 4];
        Array.Fill(bytes, (byte)42);
        var config = new BBDownTDownloadUtil.DownloadConfig
        { ResourceIdentity = "episode-video-720-avc", RetryDelay = (_, _) => Task.CompletedTask };
        using (var previous = new HttpClient(new Source(bytes, failSuffix: true)))
        {
            await BBDownTDownloadUtil.RangeDownloadToTmpAsync(0, original, first, 0, part - 1, (_, _, _) => { },
                true, previous, resourceIdentity: config.ResourceIdentity,
                expectedResource: new(bytes.Length, new("\"version\"", null)));
            await Assert.ThrowsAnyAsync<Exception>(() => BBDownTDownloadUtil.MultiThreadDownloadFileAsync(
                original, destination, config, previous));
        }
        Assert.True((await DownloadResumeState.LoadAsync(first + ".resume"))!.Complete);
        Assert.Equal(1, new FileInfo(second).Length);
        if (changedBytes) bytes[100] = 99; // Same opaque ETag on another URI is not a global content hash.
        var url = changedUrl ? original.Replace("old", "new") : original;
        using var source = new Source(bytes);
        using var current = new HttpClient(source);
        var clips = await BBDownTDownloadUtil.MultiThreadDownloadFileAsync(url, destination, config, current);
        if (changedUrl)
        {
            Assert.Contains(0L, source.Starts);
            Assert.Contains((long)part, source.Starts);
        }
        else
        {
            Assert.DoesNotContain(0L, source.Starts);
            Assert.Contains((long)part + 1, source.Starts);
        }
        await BBDownTDownloadUtil.MergeTrackClipsAsync(clips, destination, config);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(destination));
        Assert.True((await DownloadResumeState.LoadAsync(destination + ".resume"))!.Complete);
        Assert.False(File.Exists(first));
        Assert.False(File.Exists(second));
    }

    [Fact]
    public async Task CompleteVideoTrack_IsReusedAfterAudioFailureWithoutBodyTransfer()
    {
        using var files = new MediaTestDirectory();
        var destination = files.FilePath("video.mp4");
        files.FilePath("video.mp4.resume");
        var clip = files.FilePath("00000_video.vclip");
        files.FilePath("00000_video.vclip.resume");
        byte[] bytes = [1, 2, 3, 4];
        using var first = new HttpClient(new Source(bytes));
        var config = new BBDownTDownloadUtil.DownloadConfig { ResourceIdentity = "video-avc" };
        var clips = await BBDownTDownloadUtil.MultiThreadDownloadFileAsync("https://cdn.test/video", destination, config, first);
        await BBDownTDownloadUtil.MergeTrackClipsAsync(clips, destination, config);
        Assert.False(File.Exists(clip));
        using var source = new Source(bytes);
        using var next = new HttpClient(source);
        Assert.Empty(await BBDownTDownloadUtil.MultiThreadDownloadFileAsync("https://cdn.test/video", destination, config, next));
        Assert.Empty(source.Starts);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(destination));
    }

    [Fact]
    public async Task SameSizeAndSameOpaqueTag_WithDifferentRepresentationDoesNotReuseTrack()
    {
        using var files = new MediaTestDirectory();
        var destination = files.Write("track.mp4", "OLD!");
        files.FilePath("track.mp4.resume");
        files.FilePath("00000_track.vclip");
        files.FilePath("00000_track.vclip.resume");
        const string url = "https://cdn.test/selected";
        await SaveState(destination, url, "video-720-avc", 0, null, 4, true);
        using var source = new Source("NEW!"u8.ToArray());
        using var client = new HttpClient(source);
        var config = new BBDownTDownloadUtil.DownloadConfig { ResourceIdentity = "video-1080-hevc" };
        var clips = await BBDownTDownloadUtil.MultiThreadDownloadFileAsync(url, destination, config, client);
        Assert.Contains(0L, source.Starts);
        Assert.Equal("OLD!", File.ReadAllText(destination));
        await BBDownTDownloadUtil.MergeTrackClipsAsync(clips, destination, config);
        Assert.Equal("NEW!", File.ReadAllText(destination));
    }

    [Fact]
    public async Task HardExitTail_RangeStartsAtHashedCheckpointWithoutLosingIt()
    {
        using var files = new MediaTestDirectory();
        var path = files.FilePath("part.vclip");
        files.FilePath("part.vclip.resume");
        await File.WriteAllBytesAsync(path, [1, 2]);
        const string url = "https://cdn.test/track";
        await SaveState(path, url, "video", 0, 5, 6, false);
        await File.WriteAllBytesAsync(path, [1, 2, 3, 4]); // Last writes outlived the durable checkpoint.
        using var source = new Source([1, 2, 3, 4, 5, 6]);
        using var client = new HttpClient(source);
        await BBDownTDownloadUtil.RangeDownloadToTmpAsync(0, url, path, 0, 5, (_, _, _) => { }, true, client,
            resourceIdentity: "video", expectedResource: new(6, new("\"version\"", null)));
        Assert.Equal(new[] { 2L }, source.Starts);
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5, 6 }, await File.ReadAllBytesAsync(path));
    }

    [Fact]
    public async Task LegacyCompleteClip_IsRecoveredByFullComparisonAndKeepsCompletedRecord()
    {
        using var files = new MediaTestDirectory();
        var path = files.FilePath("old.vclip");
        files.FilePath("old.vclip.resume");
        await File.WriteAllBytesAsync(path, [1, 2, 3, 4]);
        await new DownloadResumeValidator("\"version\"", null).SaveAsync(path + ".resume");
        using var source = new Source([1, 2, 3, 4]);
        using var client = new HttpClient(source);
        await BBDownTDownloadUtil.RangeDownloadToTmpAsync(0, "https://cdn.test/track", path, 0, 3,
            (_, _, _) => { }, true, client, resourceIdentity: "video",
            expectedResource: new(4, new("\"version\"", null)));
        Assert.Equal(new[] { 0L }, source.Starts);
        Assert.True((await DownloadResumeState.LoadAsync(path + ".resume"))!.Complete);
    }

    [Fact]
    public async Task GenericCover_UsesRoleSpecificTemporaryNameAndDoesNotLeaveTrackRecord()
    {
        using var files = new MediaTestDirectory();
        var path = files.FilePath("cover.jpg");
        files.FilePath("cover.jpg.tmp");
        files.FilePath("cover.jpg.tmp.resume");
        using var client = new HttpClient(new Source([1, 2, 3, 4]));
        await BBDownTDownloadUtil.DownloadFileAsync("https://cdn.test/cover", path, new(), client);
        Assert.True(File.Exists(path));
        Assert.False(File.Exists(path + ".resume"));
        Assert.False(File.Exists(path + ".tmp"));
        Assert.False(File.Exists(path + ".tmp.resume"));
    }

    [Fact]
    public async Task AudioFailure_WithSharedStemKeepsItsOwnTemporaryFileWhenVideoIsReused()
    {
        using var files = new MediaTestDirectory();
        var video = files.FilePath("media.mp4");
        files.FilePath("media.mp4.resume");
        files.FilePath("media.mp4.tmp");
        files.FilePath("media.mp4.tmp.resume");
        var audio = files.FilePath("media.m4a");
        var audioTemporary = files.FilePath("media.m4a.tmp");
        files.FilePath("media.m4a.tmp.resume");
        var videoConfig = new BBDownTDownloadUtil.DownloadConfig
        { ResourceIdentity = "video", RetryDelay = (_, _) => Task.CompletedTask };
        using (var client = new HttpClient(new Source([1, 2, 3, 4])))
            await BBDownTDownloadUtil.DownloadFileAsync("https://cdn.test/video", video, videoConfig, client);
        using (var client = new HttpClient(new Source([9, 8, 7, 6], failAllRanges: true)))
            await Assert.ThrowsAnyAsync<Exception>(() => BBDownTDownloadUtil.DownloadFileAsync(
                "https://cdn.test/audio", audio,
                new() { ResourceIdentity = "audio", RetryDelay = (_, _) => Task.CompletedTask }, client));
        var retained = await File.ReadAllBytesAsync(audioTemporary);
        using var source = new Source([1, 2, 3, 4]);
        using var next = new HttpClient(source);
        await BBDownTDownloadUtil.DownloadFileAsync("https://cdn.test/video", video, videoConfig, next);
        Assert.Empty(source.Starts);
        Assert.Equal(retained, await File.ReadAllBytesAsync(audioTemporary));
        Assert.False(File.Exists(video + ".tmp"));
    }

    [Fact]
    public async Task FullCheckpointWithoutCompletionFlag_IsPromotedAfterSharedProbeWithoutBodyTransfer()
    {
        using var files = new MediaTestDirectory();
        var path = files.FilePath("part.vclip");
        files.FilePath("part.vclip.resume");
        await File.WriteAllBytesAsync(path, [1, 2, 3, 4]);
        const string url = "https://cdn.test/track";
        await SaveState(path, url, "video", 0, 3, 4, false);
        using var source = new Source([1, 2, 3, 4]);
        using var client = new HttpClient(source);
        var metadata = await BBDownTDownloadUtil.GetResourceMetadataAsync(url, client);
        await BBDownTDownloadUtil.RangeDownloadToTmpAsync(0, url, path, 0, 3, (_, _, _) => { }, true, client,
            resourceIdentity: "video", expectedResource: metadata);
        Assert.Empty(source.Starts);
        Assert.True((await DownloadResumeState.LoadAsync(path + ".resume"))!.Complete);
    }

    [Fact]
    public async Task InvalidLocalDigest_DoesNotDestroyBytesBeforeAnHttpFailure()
    {
        using var files = new MediaTestDirectory();
        var path = files.FilePath("part.vclip");
        files.FilePath("part.vclip.resume");
        await File.WriteAllBytesAsync(path, [1, 2, 3, 4]);
        const string url = "https://cdn.test/track";
        var state = new DownloadResumeState("video", DownloadResumeState.SourceHash(url), 0, 3, 4,
            true, 4, new string('A', 64), new("\"version\"", null));
        await state.SaveAsync(path + ".resume");
        using var client = new HttpClient(new FailingSource());
        await Assert.ThrowsAnyAsync<HttpRequestException>(() => BBDownTDownloadUtil.RangeDownloadToTmpAsync(
            0, url, path, 0, 3, (_, _, _) => { }, true, client, resourceIdentity: "video",
            expectedResource: new(4, new("\"version\"", null))));
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, await File.ReadAllBytesAsync(path));
        Assert.Equal(state, await DownloadResumeState.LoadAsync(path + ".resume"));
    }

    private static async Task SaveState(string path, string url, string identity, long from, long? to, long total, bool complete)
    {
        var bytes = await File.ReadAllBytesAsync(path);
        await new DownloadResumeState(identity, DownloadResumeState.SourceHash(url), from, to, total, complete,
            bytes.Length, Convert.ToHexString(SHA256.HashData(bytes)), new("\"version\"", null)).SaveAsync(path + ".resume");
    }

    private sealed class Source(byte[] bytes, bool failSuffix = false, bool failAllRanges = false) : HttpMessageHandler
    {
        internal ConcurrentQueue<long> Starts { get; } = new();
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var range = request.Headers.Range?.Ranges.Single();
            if (range is null)
            {
                var probe = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([]) };
                probe.Content.Headers.ContentLength = bytes.Length;
                probe.Headers.ETag = new EntityTagHeaderValue("\"version\"");
                return Task.FromResult(probe);
            }
            var start = range.From!.Value;
            var end = range.To ?? bytes.Length - 1;
            Starts.Enqueue(start);
            // Keep the outage active after the first saved byte. A longer retry
            // budget must not let this synthetic source finish one byte at a time.
            if ((failAllRanges && start > 0) || (failSuffix && start > 20 * 1024 * 1024))
                throw new HttpRequestException("synthetic persistent disconnect");
            var payload = bytes[(int)start..((int)end + 1)];
            Stream stream = failAllRanges || (failSuffix && start >= 20 * 1024 * 1024)
                ? new DisconnectingStream(payload) : new MemoryStream(payload);
            var response = new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = new StreamContent(stream) };
            response.Headers.ETag = new EntityTagHeaderValue("\"version\"");
            response.Content.Headers.ContentLength = payload.Length;
            response.Content.Headers.ContentRange = new ContentRangeHeaderValue(start, end, bytes.Length);
            return Task.FromResult(response);
        }
    }

    private sealed class FailingSource : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
    }

    private sealed class NoValidatorSource(byte[] bytes, Action<HttpRequestMessage> onTransfer) : HttpMessageHandler
    {
        internal int Probes { get; private set; }
        internal int BodyRequests { get; private set; }
        internal int BodyBytesRead { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            HttpContent content;
            if (request.Headers.Range is null)
            {
                Probes++;
                content = new ByteArrayContent([]);
            }
            else
            {
                BodyRequests++;
                onTransfer(request);
                content = new StreamContent(new CountingStream(bytes, count => BodyBytesRead += count));
            }
            content.Headers.ContentLength = bytes.Length;
            // No ETag or Last-Modified on either the metadata probe or the body response.
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }
    }

    private sealed class CountingStream(byte[] bytes, Action<int> onRead) : MemoryStream(bytes)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var count = Read(buffer.Span);
            onRead(count);
            return ValueTask.FromResult(count);
        }
    }

    private sealed class DisconnectingStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (Position > 0) throw new IOException("synthetic disconnect");
            return ValueTask.FromResult(Read(buffer.Span[..1]));
        }
    }
}
