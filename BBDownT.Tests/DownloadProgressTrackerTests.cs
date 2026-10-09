using System.Security.Cryptography;
using BBDownT.Core.Util;

namespace BBDownT.Tests;

public class DownloadProgressTrackerTests
{
    [Fact]
    public void RestoredPartsCountTowardCompletionButNotTransferSpeed()
    {
        var frames = new List<(double Progress, long Bytes, string? Status)>();
        var logs = new List<string>();
        var tracker = new DownloadProgressTracker(100, [40, 60],
            (progress, bytes, status) => frames.Add((progress, bytes, status)), logs.Add);
        tracker.Restore(0, 40, false);
        tracker.Restore(1, 20, false);
        tracker.ReportRestored();

        Assert.Equal((0.6, 0L, null), frames.Last());
        Assert.Contains("60.00%", Assert.Single(logs));
        tracker.Report(0, new(40, 0)); // A cached complete part did not arrive over the wire.
        tracker.Report(1, new(30, 10));
        Assert.Equal((0.7, 10L, null), frames.Last());
    }

    [Fact]
    public void VerificationKeepsRetainedProgressAndLogsOnlyOnceAcrossPartsAndRetries()
    {
        var frames = new List<(double Progress, long Bytes, string? Status)>();
        var logs = new List<string>();
        var tracker = new DownloadProgressTracker(100, [50, 50],
            (progress, bytes, status) => frames.Add((progress, bytes, status)), logs.Add);
        tracker.Restore(0, 40, true);
        tracker.Restore(1, 20, true);
        tracker.ReportRestored();
        tracker.Report(0, new(40, 10, true, 10, 40));
        tracker.Report(1, new(20, 5, true, 5, 20));

        Assert.Equal((0.6, 15L, "校验 25.00%"), frames.Last());
        tracker.Report(0, new(40, 0, true, 0, 40)); // A retry has to repeat untrusted verification.
        tracker.Report(0, new(40, 40, false, 40, 40));
        tracker.Report(1, new(20, 15, false, 20, 20));
        Assert.Equal((0.6, 70L, null), frames.Last());
        tracker.Report(1, new(50, 30));
        Assert.Equal((0.9, 100L, null), frames.Last());
        Assert.Single(logs, text => text == "正在校验已有数据...");
    }

    [Fact]
    public void ConcurrentPartsProduceCoherentCompletionAndTransferTotals()
    {
        (double Progress, long Bytes, string? Status) last = default;
        var tracker = new DownloadProgressTracker(1000, Enumerable.Repeat(10L, 100).ToArray(),
            (progress, bytes, status) => last = (progress, bytes, status), _ => { });

        Parallel.For(0, 100, index => tracker.Report(index, new(10, 10)));

        Assert.Equal((1.0, 1000L, null), last);
    }

    [Fact]
    public void ShorterRetryUsesCurrentRangeAlongsidePendingAndCompletedParts()
    {
        var frames = new List<(double Progress, long Bytes, string? Status)>();
        var logs = new List<string>();
        var tracker = new DownloadProgressTracker(30, [10, 10, 10],
            (progress, bytes, status) => frames.Add((progress, bytes, status)), logs.Add);
        tracker.Restore(0, 6, true);
        tracker.Restore(1, 4, true); // This part is still queued.
        tracker.Restore(2, 5, true);
        tracker.ReportRestored();
        tracker.Report(2, new(5, 5, false, 5, 5));
        tracker.Report(0, new(6, 2, true, 2, 6));
        tracker.Report(0, new(3, 1, false, 2, 2)); // Only the two matched bytes finish verification.
        Assert.Equal("校验 63.64%", frames.Last().Status);

        tracker.Report(0, new(3, 0, true, 0, 3));
        Assert.Equal("校验 41.67%", frames.Last().Status); // 5 completed, 3 retry, 4 queued.
        tracker.Report(0, new(3, 1, true, 1, 3));
        Assert.Equal("校验 50.00%", frames.Last().Status);
        tracker.Report(0, new(3, 1, true, 2, 3));
        Assert.Equal("校验 58.33%", frames.Last().Status);

        tracker.Report(1, new(4, 0)); // The queued checkpoint turns out to be reusable.
        Assert.Equal("校验 87.50%", frames.Last().Status);
        tracker.Report(2, new(6, 1)); // Downloading a suffix retains its finished verification.
        Assert.Equal("校验 87.50%", frames.Last().Status);
        tracker.Report(0, new(3, 1, false, 3, 3));
        Assert.Null(frames.Last().Status);
        Assert.Single(logs, text => text == "正在校验已有数据...");
    }

    [Theory]
    [InlineData(0, "校验 33.33%")]
    public void MismatchedPrefixCountsOnlyMatchedBytesWhileAnotherPartIsVerifying(
        long matchedBytes, string expectedStatus)
    {
        (double Progress, long Bytes, string? Status) last = default;
        var tracker = new DownloadProgressTracker(16, [8, 8],
            (progress, bytes, status) => last = (progress, bytes, status), _ => { });
        tracker.Restore(0, 6, true);
        tracker.Restore(1, 3, true);
        tracker.ReportRestored();
        tracker.Report(1, new(3, 1, true, 1, 3));

        tracker.Report(0, new(matchedBytes + 1, 1, false, matchedBytes, matchedBytes));

        Assert.Equal(expectedStatus, last.Status);
        Assert.Equal(2L, last.Bytes);
        tracker.Report(0, new(matchedBytes + 2, 1)); // A suffix does not erase completed comparison.
        Assert.Equal(expectedStatus, last.Status);
    }

    [Fact]
    public void ConcurrentShorterRetriesKeepVerificationWithinCurrentRanges()
    {
        var frames = new List<(double Progress, long Bytes, string? Status)>();
        var logs = new List<string>();
        var tracker = new DownloadProgressTracker(1000, Enumerable.Repeat(10L, 100).ToArray(),
            (progress, bytes, status) => frames.Add((progress, bytes, status)), logs.Add);
        for (var index = 0; index < 100; index++) tracker.Restore(index, 6, true);
        tracker.ReportRestored();
        Parallel.For(0, 100, index => tracker.Report(index, new(3, 1, true, 1, 3)));

        Assert.Equal((0.3, 100L, "校验 33.33%"), frames.Last());
        Assert.All(frames, frame => Assert.InRange(frame.Progress, 0, 1));
        tracker.Report(0, new(3, 0, true, -1, 3));
        Assert.Equal("校验 33.00%", frames.Last().Status);
        tracker.Report(0, new(3, 0, true, 100, 3));
        Assert.Equal("校验 34.00%", frames.Last().Status);
        Assert.Equal(100L, frames.Last().Bytes);
        Assert.Single(logs, text => text == "正在校验已有数据...");
    }

    [Fact]
    public async Task StreamingMismatchThenDisconnectReverifiesShorterCheckpointWithCurrentPercentage()
    {
        using var local = new MemoryStream();
        local.Write([1, 2, 3, 4, 5, 6]);
        using var broken = new BrokenOneByteStream([1, 2, 99, 4, 5, 6, 7, 8], 3);
        var frames = new List<(double Progress, long Bytes, string? Status)>();
        var logs = new List<string>();
        var checkpoints = new List<(long Length, string Digest, bool Complete)>();
        var tracker = new DownloadProgressTracker(8, [8],
            (progress, bytes, status) => frames.Add((progress, bytes, status)), logs.Add);
        tracker.Restore(0, 6, true);
        tracker.ReportRestored();
        Task Save(long length, string digest, bool complete)
        {
            checkpoints.Add((length, digest, complete));
            return Task.CompletedTask;
        }

        var error = await Assert.ThrowsAsync<DownloadInterruptedException>(() =>
            BBDownTDownloadUtil.CopyVerifiedRangeAsync(local, broken, 0, true, 8, _ => { },
                Save, onTransferProgress: update => tracker.Report(0, update)));

        Assert.IsType<IOException>(error.InnerException);
        Assert.Equal(new byte[] { 1, 2, 99 }, local.ToArray());
        Assert.Equal((3L, Convert.ToHexString(SHA256.HashData(local.ToArray())), false), Assert.Single(checkpoints));
        Assert.Equal((3.0 / 8, 3L, null), frames.Last());

        var retryStart = frames.Count;
        using var retry = new OneByteStream([1, 2, 99, 4, 5, 6, 7, 8]);
        await BBDownTDownloadUtil.CopyVerifiedRangeAsync(local, retry, 0, true, 8, _ => { },
            Save, onTransferProgress: update => tracker.Report(0, update));

        Assert.Equal((3.0 / 8, 3L, "校验 0.00%"), frames[retryStart]);
        Assert.Equal((3.0 / 8, 4L, "校验 33.33%"), frames[retryStart + 1]);
        Assert.Equal((3.0 / 8, 5L, "校验 66.67%"), frames[retryStart + 2]);
        Assert.Equal((1.0, 11L, null), frames.Last());
        Assert.Equal(new byte[] { 1, 2, 99, 4, 5, 6, 7, 8 }, local.ToArray());
        Assert.Equal((8L, Convert.ToHexString(SHA256.HashData(local.ToArray())), true), checkpoints.Last());
        Assert.Single(logs, text => text == "正在校验已有数据...");
    }

    [Fact]
    public async Task StreamingVerificationRetainsPercentageUntilMismatchAndCountsAllReceivedBytes()
    {
        using var local = new MemoryStream();
        local.Write([1, 2, 3, 4, 5, 6]);
        using var remote = new OneByteStream([1, 2, 99, 4, 5, 6, 7, 8]);
        var frames = new List<DownloadProgressUpdate>();
        string? digest = null;

        await BBDownTDownloadUtil.CopyVerifiedRangeAsync(local, remote, 0, true, 8, _ => { },
            (_, hash, _) => { digest = hash; return Task.CompletedTask; }, onTransferProgress: frames.Add);

        Assert.Equal(new DownloadProgressUpdate(6, 0, true, 0, 6), frames[0]);
        Assert.Equal(6, frames[1].CompletedBytes);
        Assert.Equal(6, frames[2].CompletedBytes);
        Assert.Equal(3, frames[3].CompletedBytes); // Only the matching prefix survived replacement.
        Assert.False(frames[3].Verifying);
        Assert.Equal(8, frames.Sum(update => update.TransferredBytes));
        Assert.Equal(new byte[] { 1, 2, 99, 4, 5, 6, 7, 8 }, local.ToArray());
        Assert.Equal(Convert.ToHexString(SHA256.HashData(local.ToArray())), digest);
    }

    [Fact]
    public async Task StreamingAppendReportsRestoredCheckpointWithoutCountingItAsReceived()
    {
        using var local = new MemoryStream();
        local.Write([1, 2, 3, 4]);
        using var remote = new OneByteStream([5, 6]);
        var frames = new List<DownloadProgressUpdate>();

        await BBDownTDownloadUtil.CopyVerifiedRangeAsync(local, remote, 4, false, 2, _ => { },
            (_, _, _) => Task.CompletedTask, onTransferProgress: frames.Add);

        Assert.Equal(new DownloadProgressUpdate(4, 0), frames[0]);
        Assert.Equal(6, frames.Last().CompletedBytes);
        Assert.Equal(2, frames.Sum(update => update.TransferredBytes));
        Assert.All(frames, update => Assert.False(update.Verifying));
    }

    private sealed class OneByteStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(Read(buffer.Span[..Math.Min(1, buffer.Length)]));
    }

    private sealed class BrokenOneByteStream(byte[] bytes, int limit) : MemoryStream(bytes)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (Position >= limit) throw new IOException("synthetic disconnect");
            return ValueTask.FromResult(Read(buffer.Span[..Math.Min(1, buffer.Length)]));
        }
    }
}
