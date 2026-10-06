using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using BBDownT.Core.Util;

namespace BBDownT.Tests;

public class DownloadRetryIntegrationTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CdnOutage_PreservesCompletedPartAndResumesOnlyMissingSuffix(bool recovers)
    {
        const int part = 20 * 1024 * 1024;
        const string url = "https://cdn.test/media?signature=synthetic-secret";
        const string identity = "selected-video";
        using var files = new MediaTestDirectory();
        var destination = files.FilePath("video.mp4");
        files.FilePath("video.mp4.resume");
        var first = files.FilePath("00000_video.vclip");
        files.FilePath("00000_video.vclip.resume");
        var second = files.FilePath("00001_video.vclip");
        files.FilePath("00001_video.vclip.resume");
        var bytes = new byte[part + 8];
        Array.Fill(bytes, (byte)42, 0, part);
        new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }.CopyTo(bytes, part);
        await File.WriteAllBytesAsync(first, bytes.AsMemory(0, part).ToArray());
        await new DownloadResumeState(identity, DownloadResumeState.SourceHash(url), 0, part - 1,
            bytes.Length, true, part, Convert.ToHexString(SHA256.HashData(bytes.AsSpan(0, part))),
            new("\"version\"", null)).SaveAsync(first + ".resume");
        var clock = new VirtualClock();
        using var source = new OutageSource(bytes, clock, recovers ? TimeSpan.FromSeconds(60) : TimeSpan.FromMinutes(10));
        using var client = new HttpClient(source);
        var config = new BBDownTDownloadUtil.DownloadConfig
        {
            ResourceIdentity = identity, RetryDelay = clock.Delay, MaxParallelDownloads = 2
        };

        if (recovers)
        {
            var clips = await BBDownTDownloadUtil.MultiThreadDownloadFileAsync(url, destination, config, client);
            await BBDownTDownloadUtil.MergeTrackClipsAsync(clips, destination, config);
            Assert.Equal(bytes, await File.ReadAllBytesAsync(destination));
            Assert.Equal(8, source.BodyBytesRead);
        }
        else
        {
            var error = await Assert.ThrowsAsync<IOException>(() =>
                BBDownTDownloadUtil.MultiThreadDownloadFileAsync(url, destination, config, client));
            var cause = Assert.IsType<RetryableHttpStatusException>(error.InnerException);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, cause.StatusCode);
            Assert.DoesNotContain("synthetic-secret", error.Message);
            Assert.Equal(new byte[] { 1, 2, 3 }, await File.ReadAllBytesAsync(second));
            var checkpoint = await DownloadResumeState.LoadAsync(second + ".resume");
            Assert.NotNull(checkpoint);
            Assert.False(checkpoint.Complete);
            Assert.Equal(3, checkpoint.LocalLength);
            Assert.True((await DownloadResumeState.LoadAsync(first + ".resume"))!.Complete);
            Assert.False(File.Exists(destination));
        }
        Assert.Equal(NetworkRetry.DownloadDelays, clock.Delays);
        Assert.Equal(TimeSpan.FromSeconds(62), clock.Elapsed);
        Assert.DoesNotContain(0L, source.Starts);
        Assert.Equal(1, source.Starts.Count(start => start == part));
        Assert.All(source.Starts.Skip(1), start => Assert.Equal((long)part + 3, start));
    }

    [Fact]
    public async Task MetadataProbe_UsesRequestBackoffAndKeepsHttpFailureOutOfResourceChangeLogic()
    {
        var clock = new VirtualClock();
        var calls = 0;
        using var client = new HttpClient(new Handler((_, _) =>
        {
            calls++;
            return Task.FromResult(calls < 3 ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                : Probe(4));
        }));

        var metadata = await BBDownTDownloadUtil.GetResourceMetadataAsync("https://cdn.test/media", client,
            retryDelay: clock.Delay);

        Assert.Equal(4, metadata.TotalLength);
        Assert.Equal(3, calls);
        Assert.Equal(new[] { TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(3) }, clock.Delays);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PermanentRemoteProtocolFailure_FailsOnceAndPreservesOriginalCause(bool httpProtocolError)
    {
        using var files = new MediaTestDirectory();
        var destination = files.FilePath("video.mp4");
        files.FilePath("video.mp4.tmp");
        files.FilePath("video.mp4.tmp.resume");
        var clock = new VirtualClock();
        Exception cause = httpProtocolError ? new HttpIOException(HttpRequestError.InvalidResponse, "synthetic bad protocol")
            : new InvalidDataException("synthetic bad gzip");
        var calls = 0;
        using var client = new HttpClient(new Handler((_, _) =>
        {
            calls++;
            return Task.FromResult(Partial(new ThrowingStream(cause), 0, 3, 4));
        }));

        async Task Download() => await BBDownTDownloadUtil.DownloadFileAsync(
            "https://cdn.test/media", destination, new() { RetryDelay = clock.Delay }, client);
        var error = httpProtocolError ? await Assert.ThrowsAsync<HttpIOException>(Download)
            : (Exception)await Assert.ThrowsAsync<InvalidDataException>(Download);

        Assert.Same(cause, error);
        Assert.Equal(1, calls);
        Assert.Empty(clock.Delays);
        Assert.False(File.Exists(destination));
    }

    [Fact]
    public async Task HeaderTimeout_RemainsANetworkCauseAfterTheClipWrapsIt()
    {
        using var files = new MediaTestDirectory();
        var destination = files.FilePath("video.mp4");
        files.FilePath("00000_video.vclip");
        var clock = new VirtualClock();
        var calls = 0;
        using var client = new HttpClient(new Handler(async (request, token) =>
        {
            if (request.Headers.Range is null) return Probe(4);
            calls++;
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            throw new InvalidOperationException("Unreachable");
        })) { Timeout = TimeSpan.FromMilliseconds(30) };

        var error = await Assert.ThrowsAsync<IOException>(() => BBDownTDownloadUtil.MultiThreadDownloadFileAsync(
            "https://cdn.test/media", destination,
            new() { RetryDelay = clock.Delay, MaxParallelDownloads = 1, ResourceIdentity = "video" }, client));

        var interrupted = Assert.IsType<DownloadInterruptedException>(error.InnerException);
        Assert.IsAssignableFrom<OperationCanceledException>(interrupted.InnerException);
        Assert.True(NetworkRetry.IsTransient(error));
        Assert.Equal(6, calls);
        Assert.Equal(NetworkRetry.DownloadDelays, clock.Delays);
    }

    private static HttpResponseMessage Probe(long length)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([]) };
        response.Content.Headers.ContentLength = length;
        response.Headers.ETag = new EntityTagHeaderValue("\"version\"");
        return response;
    }

    private static HttpResponseMessage Partial(Stream stream, long start, long end, long total)
    {
        var response = new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = new StreamContent(stream) };
        response.Content.Headers.ContentLength = end - start + 1;
        response.Content.Headers.ContentRange = new ContentRangeHeaderValue(start, end, total);
        response.Headers.ETag = new EntityTagHeaderValue("\"version\"");
        return response;
    }

    private sealed class VirtualClock
    {
        internal List<TimeSpan> Delays { get; } = [];
        internal TimeSpan Elapsed { get; private set; }
        internal Task Delay(TimeSpan duration, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Delays.Add(duration);
            Elapsed += duration;
            return Task.CompletedTask;
        }
    }

    private sealed class OutageSource(byte[] bytes, VirtualClock clock, TimeSpan outage) : HttpMessageHandler
    {
        internal ConcurrentQueue<long> Starts { get; } = new();
        internal int BodyBytesRead { get; private set; }
        private TimeSpan? unavailableUntil;
        private bool interrupted;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Headers.Range is null) return Task.FromResult(Probe(bytes.Length));
            var range = Assert.Single(request.Headers.Range.Ranges);
            var start = range.From!.Value;
            Assert.True(start >= 20 * 1024 * 1024, "Completed part must not request another body");
            Starts.Enqueue(start);
            if (unavailableUntil is { } until && clock.Elapsed < until)
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
            var end = range.To ?? bytes.Length - 1;
            var payload = bytes[(int)start..((int)end + 1)];
            Stream stream;
            if (!interrupted)
            {
                interrupted = true;
                stream = new PrefixStream(payload, count => BodyBytesRead += count,
                    () => unavailableUntil = clock.Elapsed + outage);
            }
            else stream = new CountingStream(payload, count => BodyBytesRead += count);
            return Task.FromResult(Partial(stream, start, end, bytes.Length));
        }
    }

    private sealed class PrefixStream(byte[] bytes, Action<int> onRead, Action onDisconnect) : MemoryStream(bytes)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Position >= 3)
            {
                onDisconnect();
                throw new IOException("synthetic connection reset", new SocketException((int)SocketError.ConnectionReset));
            }
            var count = Read(buffer.Span[..Math.Min(3, buffer.Length)]);
            onRead(count);
            return ValueTask.FromResult(count);
        }
    }

    private sealed class CountingStream(byte[] bytes, Action<int> onRead) : MemoryStream(bytes)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var count = Read(buffer.Span);
            onRead(count);
            return ValueTask.FromResult(count);
        }
    }

    private sealed class ThrowingStream(Exception error) : MemoryStream
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            => ValueTask.FromException<int>(error);
    }

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> action) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => action(request, cancellationToken);
    }
}
