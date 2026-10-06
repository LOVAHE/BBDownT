using System.Security.Cryptography;
using BBDownT.Core.Util;

namespace BBDownT.Tests;

public class ResumeStreamTests
{
    [Fact]
    public async Task InterruptedSuffix_PreservesCheckpointAndResumesWithoutReplacingPrefix()
    {
        using var local = Local([1, 2]);
        using var broken = new BrokenStream([3, 4, 5, 6], 2);
        var saved = new List<(long Length, string Hash, bool Complete)>();
        var error = await Assert.ThrowsAsync<DownloadInterruptedException>(() => BBDownTDownloadUtil.CopyVerifiedRangeAsync(
            local, broken, 2, false, 4, _ => { }, (length, hash, complete) =>
            { saved.Add((length, hash, complete)); return Task.CompletedTask; }));
        Assert.IsType<IOException>(error.InnerException);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, local.ToArray());
        Assert.Equal((4L, Digest([1, 2, 3, 4]), false), saved.Single());
        using var suffix = new MemoryStream(new byte[] { 5, 6 });
        await BBDownTDownloadUtil.CopyVerifiedRangeAsync(local, suffix, 4, false, 2, _ => { },
            (length, hash, complete) => { saved.Add((length, hash, complete)); return Task.CompletedTask; });
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5, 6 }, local.ToArray());
        Assert.Equal((6L, Digest(local.ToArray()), true), saved.Last());
    }

    [Fact]
    public async Task ValidatorMissing_VerifiesEveryRetainedByteIncludingChangedInterior()
    {
        byte[] old = [1, 2, 3, 4, 5, 6, 7, 8];
        byte[] current = [1, 2, 3, 99, 5, 6, 7, 8, 9];
        using var local = Local(old);
        using var remote = new BrokenStream(current, int.MaxValue);
        await BBDownTDownloadUtil.CopyVerifiedRangeAsync(local, remote, 0, true, current.Length, _ => { },
            (_, _, _) => Task.CompletedTask);
        Assert.Equal(current.Length, remote.BytesRead);
        Assert.Equal(current, local.ToArray());
    }

    [Fact]
    public async Task InterruptionWhileComparingPrefix_DoesNotDiscardUnverifiedHistoricalTail()
    {
        using var local = Local([1, 2, 3, 4, 5, 6]);
        using var remote = new BrokenStream([1, 2, 3, 4, 5, 6, 7], 2);
        var checkpoints = 0;
        await Assert.ThrowsAsync<DownloadInterruptedException>(() => BBDownTDownloadUtil.CopyVerifiedRangeAsync(local,
            remote, 0, true, 7, _ => { }, (_, _, _) => { checkpoints++; return Task.CompletedTask; }));
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5, 6 }, local.ToArray());
        Assert.Equal(0, checkpoints);
    }

    [Fact]
    public async Task HardExitTail_ContinuesFromCheckpointAndVerifiesOnlyUncheckpointedBytes()
    {
        using var local = Local([1, 2, 3, 4, 5, 6]);
        var state = State(3, Digest([1, 2, 3]), false);
        Assert.True(await state.MatchesStreamAsync(local));
        using var remote = new BrokenStream([4, 5, 6, 7, 8], int.MaxValue);
        await BBDownTDownloadUtil.CopyVerifiedRangeAsync(local, remote, 3, true, 5, _ => { },
            (_, _, complete) => { Assert.True(complete); return Task.CompletedTask; });
        Assert.Equal(5, remote.BytesRead);
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }, local.ToArray());
    }

    [Fact]
    public async Task CorruptRecordedPrefix_IsNotTrustedEvenWhenTheExtraTailExists()
    {
        using var local = Local([1, 99, 3, 4]);
        Assert.False(await State(3, Digest([1, 2, 3]), false).MatchesStreamAsync(local));
    }

    [Fact]
    public async Task ShortResponse_LeavesVerifiedBytesAndAnIncompleteCheckpoint()
    {
        using var local = Local([]);
        using var remote = new MemoryStream(new byte[] { 1, 2 });
        var complete = true;
        await Assert.ThrowsAsync<DownloadInterruptedException>(() => BBDownTDownloadUtil.CopyVerifiedRangeAsync(local,
            remote, 0, false, 4, _ => { }, (_, _, finished) => { complete = finished; return Task.CompletedTask; }));
        Assert.False(complete);
        Assert.Equal(new byte[] { 1, 2 }, local.ToArray());
    }

    [Fact]
    public async Task IdleRemoteReadTimesOutWithoutDiscardingCheckpoint()
    {
        using var local = Local([1, 2]);
        using var remote = new HangingStream();
        var saved = new List<(long Length, bool Complete)>();
        var error = await Assert.ThrowsAsync<DownloadInterruptedException>(() => BBDownTDownloadUtil.CopyVerifiedRangeAsync(
            local, remote, 2, false, 1, _ => { }, (length, _, complete) =>
            { saved.Add((length, complete)); return Task.CompletedTask; }, readTimeout: TimeSpan.FromMilliseconds(100)));

        Assert.IsType<TimeoutException>(error.InnerException);
        Assert.True(NetworkRetry.IsTransient(error));
        Assert.Equal(new byte[] { 1, 2 }, local.ToArray());
        Assert.Equal(new[] { (2L, false) }, saved);
    }

    [Fact]
    public async Task LocalWriteFailureIsPreservedAndNotClassifiedAsNetworkFailure()
    {
        var failure = new IOException("local disk is full");
        using var local = new UnwritableStream(failure);
        using var remote = new MemoryStream(new byte[] { 1, 2 });
        var actual = await Assert.ThrowsAsync<IOException>(() => BBDownTDownloadUtil.CopyVerifiedRangeAsync(
            local, remote, 0, false, 2, _ => { }, (_, _, _) => Task.CompletedTask));

        Assert.Same(failure, actual);
        Assert.False(NetworkRetry.IsTransient(actual));
    }

    [Fact]
    public async Task ExplicitReadCancellationDoesNotBecomeNetworkTimeout()
    {
        using var cancellation = new CancellationTokenSource();
        using var local = Local([1, 2]);
        using var remote = new HangingStream(() => cancellation.Cancel());
        var saved = new List<(long Length, bool Complete)>();
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => BBDownTDownloadUtil.CopyVerifiedRangeAsync(
            local, remote, 2, false, 1, _ => { }, (length, _, complete) =>
            { saved.Add((length, complete)); return Task.CompletedTask; },
            readTimeout: TimeSpan.FromSeconds(10), cancellationToken: cancellation.Token));

        Assert.False(NetworkRetry.IsTransient(error, cancellation.Token));
        Assert.Equal(new byte[] { 1, 2 }, local.ToArray());
        Assert.Equal(new[] { (2L, false) }, saved);
    }

    private static DownloadResumeState State(long length, string digest, bool complete)
        => new("track", "source", 0, 7, 8, complete, length, digest, new("\"v1\"", null));
    private static string Digest(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    private static MemoryStream Local(byte[] bytes)
    { var stream = new MemoryStream(); stream.Write(bytes); return stream; }

    private sealed class BrokenStream(byte[] bytes, int limit) : MemoryStream(bytes)
    {
        internal int BytesRead { get; private set; }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (Position >= limit) throw new IOException("synthetic disconnect");
            var read = Read(buffer.Span[..Math.Min(buffer.Length, limit - (int)Position)]);
            BytesRead += read;
            return ValueTask.FromResult(read);
        }
    }

    private sealed class HangingStream(Action? started = null) : MemoryStream
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            started?.Invoke();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }
    }

    private sealed class UnwritableStream(IOException failure) : MemoryStream
    {
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
            => ValueTask.FromException(failure);
    }
}
