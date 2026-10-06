using System.Security.Cryptography;

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
}
