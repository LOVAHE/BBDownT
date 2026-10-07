using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;

namespace BBDownT.Tests;

// These file lifecycle tests use the repository's existing ownership fixture.
public class ResumePersistenceIntegrationTests
{
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task SameStrongVersion_MergesEvenWhenOptionalModifiedHeadersDiffer(bool firstHasDate, bool secondHasDate)
    {
        using var files = new MediaTestDirectory();
        var first = files.FilePath("first.vclip");
        files.FilePath("first.vclip.resume");
        var second = files.FilePath("second.vclip");
        files.FilePath("second.vclip.resume");
        var destination = files.FilePath("track.mp4");
        files.FilePath("track.mp4.resume");
        byte[] bytes = [1, 2, 3, 4];
        await File.WriteAllBytesAsync(first, bytes[..2]);
        await File.WriteAllBytesAsync(second, bytes[2..]);
        var date = DateTimeOffset.Parse("2026-10-06T00:00:00Z");
        var source = DownloadResumeState.SourceHash("https://cdn.test/track");
        await new DownloadResumeState("track", source, 0, 1, 4, true, 2,
            Convert.ToHexString(SHA256.HashData(bytes[..2])), new("\"v1\"", firstHasDate ? date : null))
            .SaveAsync(first + ".resume");
        await new DownloadResumeState("track", source, 2, 3, 4, true, 2,
            Convert.ToHexString(SHA256.HashData(bytes[2..])), new("\"v1\"", secondHasDate ? date.AddSeconds(1) : null))
            .SaveAsync(second + ".resume");

        await BBDownTDownloadUtil.MergeTrackClipsAsync([first, second], destination, new() { ResourceIdentity = "track" });

        Assert.Equal(bytes, await File.ReadAllBytesAsync(destination));
        var state = await DownloadResumeState.LoadAsync(destination + ".resume");
        Assert.True(state!.Complete);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(bytes)), state.LocalSha256);
    }

    [Theory]
    [InlineData("\"first\"", "\"second\"", false)]
    [InlineData(null, null, false)]
    [InlineData("\"first\"", null, true)]
    public async Task DifferentOrUnprovenVersions_CannotBeMerged(string? firstTag, string? secondTag, bool sameDate)
    {
        using var files = new MediaTestDirectory();
        var first = files.FilePath("first.vclip");
        files.FilePath("first.vclip.resume");
        var second = files.FilePath("second.vclip");
        files.FilePath("second.vclip.resume");
        var destination = files.FilePath("track.mp4");
        byte[] bytes = [1, 2, 3, 4];
        await File.WriteAllBytesAsync(first, bytes[..2]);
        await File.WriteAllBytesAsync(second, bytes[2..]);
        var date = DateTimeOffset.Parse("2026-10-06T00:00:00Z");
        var source = DownloadResumeState.SourceHash("https://cdn.test/track");
        await new DownloadResumeState("track", source, 0, 1, 4, true, 2,
            Convert.ToHexString(SHA256.HashData(bytes[..2])), new(firstTag, date)).SaveAsync(first + ".resume");
        await new DownloadResumeState("track", source, 2, 3, 4, true, 2,
            Convert.ToHexString(SHA256.HashData(bytes[2..])), new(secondTag, sameDate ? date : date.AddSeconds(1)))
            .SaveAsync(second + ".resume");

        await Assert.ThrowsAsync<InvalidDataException>(() => BBDownTDownloadUtil.MergeTrackClipsAsync(
            [first, second], destination, new() { ResourceIdentity = "track" }));

        Assert.False(File.Exists(destination));
        Assert.Equal(bytes[..2], await File.ReadAllBytesAsync(first));
        Assert.Equal(bytes[2..], await File.ReadAllBytesAsync(second));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task InterruptedPrefixVerification_OnlyAccumulatesWithAStrongCurrentVersion(bool strong)
    {
        using var files = new MediaTestDirectory();
        var path = files.FilePath("part.vclip");
        files.FilePath("part.vclip.resume");
        byte[] bytes = [1, 2, 3, 4, 5, 6, 7, 8];
        await File.WriteAllBytesAsync(path, bytes[..6]);
        var date = DateTimeOffset.Parse("2026-10-06T00:00:00Z");
        var validator = new DownloadResumeValidator(strong ? "\"v1\"" : null, date);
        var historical = new DownloadResumeState("track", DownloadResumeState.SourceHash("https://cdn.test/old"),
            0, 7, 8, false, 6, Convert.ToHexString(SHA256.HashData(bytes[..6])), validator);
        await historical.SaveAsync(path + ".resume");
        using var handler = new VerificationSource(bytes, validator, date);
        using var client = new HttpClient(handler);
        for (var attempt = 0; attempt < (strong ? 3 : 2); attempt++)
        {
            await Assert.ThrowsAsync<BBDownT.Core.Util.DownloadInterruptedException>(() =>
                BBDownTDownloadUtil.RangeDownloadToTmpAsync(0, "https://cdn.test/current", path, 0, 7,
                    (_, _, _) => { }, true, client, resourceIdentity: "track", expectedResource: new(8, validator)));
            var saved = await DownloadResumeState.LoadAsync(path + ".resume");
            Assert.NotNull(saved);
            if (strong)
            {
                Assert.Equal((attempt + 1) * 2, saved.LocalLength);
                Assert.Equal(DownloadResumeState.SourceHash("https://cdn.test/current"), saved.SourceUriHash);
                Assert.Equal(Convert.ToHexString(SHA256.HashData(bytes[..(int)saved.LocalLength])), saved.LocalSha256);
                Assert.False(saved.Complete);
                if (attempt == 0)
                {
                    using var changed = new HttpClient(new VerificationSource(bytes, new("\"different\"", date), date));
                    await Assert.ThrowsAsync<InvalidDataException>(() => BBDownTDownloadUtil.RangeDownloadToTmpAsync(
                        0, "https://cdn.test/current", path, 0, 7, (_, _, _) => { }, true, changed,
                        resourceIdentity: "track", expectedResource: new(8, validator)));
                    Assert.Equal(saved, await DownloadResumeState.LoadAsync(path + ".resume"));
                }
            }
            else Assert.Equal(historical, saved);
            Assert.Equal(bytes[..6], await File.ReadAllBytesAsync(path));
        }
        if (strong)
        {
            await BBDownTDownloadUtil.RangeDownloadToTmpAsync(0, "https://cdn.test/current", path, 0, 7,
                (_, _, _) => { }, true, client, resourceIdentity: "track", expectedResource: new(8, validator));
            Assert.Equal(new long[] { 0, 2, 4, 6 }, handler.Starts);
            Assert.Equal(bytes, await File.ReadAllBytesAsync(path));
            Assert.True((await DownloadResumeState.LoadAsync(path + ".resume"))!.Complete);
        }
        else Assert.Equal(new long[] { 0, 0 }, handler.Starts);
    }

    private sealed class VerificationSource(byte[] bytes, DownloadResumeValidator validator, DateTimeOffset date) : HttpMessageHandler
    {
        internal List<long> Starts { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var range = Assert.Single(request.Headers.Range!.Ranges);
            var start = range.From!.Value;
            Starts.Add(start);
            Assert.Equal(7, range.To);
            var payload = bytes[(int)start..];
            Stream body = start < 6 ? new VerificationCutStream(payload) : new MemoryStream(payload);
            var response = new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = new StreamContent(body) };
            if (validator.EntityTag is not null) response.Headers.ETag = EntityTagHeaderValue.Parse(validator.EntityTag);
            response.Content.Headers.LastModified = date;
            response.Content.Headers.ContentLength = payload.Length;
            response.Content.Headers.ContentRange = new ContentRangeHeaderValue(start, 7, 8);
            return Task.FromResult(response);
        }
    }

    private sealed class VerificationCutStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (Position >= 2) throw new IOException("controlled verification interruption");
            return ValueTask.FromResult(Read(buffer.Span[..Math.Min(buffer.Length, 2 - (int)Position)]));
        }
    }

    private const string SignedMediaUrl = "https://upos-bstar1-mirrorakam.akamaized.net/iupxcodeboss/di/34/episode-1-231210110000.m4s"
        + "?e=object&os=akam&oi=1&platform=android&mid=0&deadline=123&trid=trace-a&upsig=signature-a"
        + "&uparams=e,os,oi,platform,mid,deadline,trid&hdnts=auth-a";

    private static string RefreshMediaUrl(string url)
        => url.Replace("deadline=123", "deadline=456").Replace("trid=trace-a", "trid=trace-b")
            .Replace("signature-a", "signature-b").Replace("auth-a", "auth-b")
            .Replace("uparams=e,os,oi,platform,mid,deadline,trid", "uparams=trid,deadline,mid,platform,oi,os,e");

    [Theory]
    [InlineData("authentication")]
    [InlineData("device-and-storage-route")]
    [InlineData("path")]
    [InlineData("host")]
    [InlineData("semantic-query")]
    [InlineData("version")]
    [InlineData("local-corruption")]
    public async Task InterruptedMediaSession_ReusesOnlyUnchangedObjectsAcrossAuthenticationRefresh(string change)
    {
        const int part = 20 * 1024 * 1024;
        using var files = new MediaTestDirectory();
        var destination = files.FilePath("track.mp4");
        files.FilePath("track.mp4.resume");
        var first = files.FilePath("00000_track.vclip");
        files.FilePath("00000_track.vclip.resume");
        var second = files.FilePath("00001_track.vclip");
        files.FilePath("00001_track.vclip.resume");
        var bytes = Enumerable.Repeat((byte)42, part + 4).ToArray();
        var config = new BBDownTDownloadUtil.DownloadConfig
        { ResourceIdentity = "episode-video-720-avc", RetryDelay = (_, _) => Task.CompletedTask };
        using (var previous = new HttpClient(new Source(bytes, failSuffix: true)))
        {
            // Establish the complete part before the deliberate peer failure can cancel it.
            await BBDownTDownloadUtil.RangeDownloadToTmpAsync(0, SignedMediaUrl, first, 0, part - 1, (_, _, _) => { },
                true, previous, resourceIdentity: config.ResourceIdentity,
                expectedResource: new(bytes.Length, new("\"version\"", null)));
            await Assert.ThrowsAnyAsync<Exception>(() => BBDownTDownloadUtil.MultiThreadDownloadFileAsync(
                SignedMediaUrl, destination, config, previous));
        }

        var saved = await DownloadResumeState.LoadAsync(first + ".resume");
        Assert.True(saved!.Complete);
        Assert.NotNull(saved.SourceObjectHash);
        Assert.Equal(1, new FileInfo(second).Length);
        var url = RefreshMediaUrl(SignedMediaUrl);
        if (change == "device-and-storage-route") url = url.Replace("os=akam", "os=cosovbv") + "&buvid=next-device";
        if (change == "path") url = url.Replace("episode-1-", "episode-reencoded-1-");
        if (change == "host") url = url.Replace("upos-bstar1-mirrorakam.akamaized.net", "upos-hz-mirrorakam.akamaized.net");
        if (change == "semantic-query") url = url.Replace("mid=0", "mid=1");
        if (change == "local-corruption")
        {
            await using var corrupt = File.OpenWrite(first);
            corrupt.Position = 100;
            corrupt.WriteByte(99);
        }
        else if (change is not "authentication" and not "device-and-storage-route") bytes[100] = 99;

        using var source = new Source(bytes, entityTag: change == "version" ? "\"new-version\"" : "\"version\"");
        using var current = new HttpClient(source);
        var clips = await BBDownTDownloadUtil.MultiThreadDownloadFileAsync(url, destination, config, current);
        if (change is "authentication" or "device-and-storage-route")
        {
            Assert.Equal((long)part + 1, Assert.Single(source.Starts));
            Assert.Equal(DownloadResumeState.SourceHash(url), (await DownloadResumeState.LoadAsync(first + ".resume"))!.SourceUriHash);
        }
        else
        {
            Assert.Contains(0L, source.Starts);
        }
        await BBDownTDownloadUtil.MergeTrackClipsAsync(clips, destination, config);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(destination));
        Assert.True((await DownloadResumeState.LoadAsync(destination + ".resume"))!.Complete);
    }

    [Fact]
    public async Task LegacyRecord_IsVerifiedOnceBeforeLaterAuthenticationRefreshCanReuseTheTrack()
    {
        using var files = new MediaTestDirectory();
        var destination = files.Write("track.mp4", "DATA");
        files.FilePath("track.mp4.resume");
        files.FilePath("track.mp4.verify.tmp");
        files.FilePath("track.mp4.verify.tmp.resume");
        await SaveState(destination, SignedMediaUrl, "video", 0, null, 4, true);
        var config = new BBDownTDownloadUtil.DownloadConfig { ResourceIdentity = "video" };
        var refreshed = RefreshMediaUrl(SignedMediaUrl);
        using (var migration = new Source("DATA"u8.ToArray()))
        using (var client = new HttpClient(migration))
        {
            Assert.Empty(await BBDownTDownloadUtil.MultiThreadDownloadFileAsync(refreshed, destination, config, client));
            Assert.Equal(0L, Assert.Single(migration.Starts));
        }
        Assert.NotNull((await DownloadResumeState.LoadAsync(destination + ".resume"))!.SourceObjectHash);
        using var next = new Source("DATA"u8.ToArray());
        using var current = new HttpClient(next);
        Assert.Empty(await BBDownTDownloadUtil.MultiThreadDownloadFileAsync(refreshed.Replace("deadline=456", "deadline=789"),
            destination, config, current));
        Assert.Empty(next.Starts);
        Assert.Equal("DATA", File.ReadAllText(destination));
    }

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

    private sealed class Source(byte[] bytes, bool failSuffix = false, bool failAllRanges = false,
        string entityTag = "\"version\"") : HttpMessageHandler
    {
        internal ConcurrentQueue<long> Starts { get; } = new();
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var range = request.Headers.Range?.Ranges.Single();
            if (range is null)
            {
                var probe = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([]) };
                probe.Content.Headers.ContentLength = bytes.Length;
                probe.Headers.ETag = new EntityTagHeaderValue(entityTag);
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
            response.Headers.ETag = new EntityTagHeaderValue(entityTag);
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
