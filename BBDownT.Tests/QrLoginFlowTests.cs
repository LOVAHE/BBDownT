namespace BBDownT.Tests;

public class QrLoginFlowTests
{
    [Fact]
    public async Task SharedPolling_NotifiesScanOnceAndStopsAtSuccess()
    {
        var statuses = new Queue<QrLoginStatus>([
            QrLoginStatus.Waiting, QrLoginStatus.Scanned, QrLoginStatus.Scanned,
            QrLoginStatus.Success, QrLoginStatus.Expired
        ]);
        var scanned = 0;
        var delays = new List<TimeSpan>();
        var result = await BBDownTLoginUtil.WaitForQrLoginAsync(
            () => Task.FromResult(statuses.Dequeue()),
            interval => { delays.Add(interval); return Task.CompletedTask; },
            TimeSpan.FromSeconds(2), () => scanned++, 10);

        Assert.Equal(QrLoginStatus.Success, result);
        Assert.Equal(1, scanned);
        Assert.Equal(4, delays.Count);
        Assert.All(delays, interval => Assert.Equal(TimeSpan.FromSeconds(2), interval));
        Assert.Single(statuses);
    }

    [Fact]
    public async Task SharedPolling_ExpiryStopsImmediately()
    {
        var calls = 0;
        var result = await BBDownTLoginUtil.WaitForQrLoginAsync(
            () => { calls++; return Task.FromResult(QrLoginStatus.Expired); },
            _ => Task.CompletedTask, TimeSpan.FromSeconds(1),
            () => throw new Exception("No scan expected"), 10);

        Assert.Equal(QrLoginStatus.Expired, result);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task SharedPolling_DeadlineDoesNotConfirmLogin()
    {
        var calls = 0;
        var result = await BBDownTLoginUtil.WaitForQrLoginAsync(
            () => { calls++; return Task.FromResult(QrLoginStatus.Waiting); },
            _ => Task.CompletedTask, TimeSpan.Zero,
            () => throw new Exception("No scan expected"), 3);

        Assert.Equal(QrLoginStatus.Waiting, result);
        Assert.Equal(3, calls);
    }

    [Fact]
    public async Task SharedPolling_UnknownStatusDoesNotConfirmLogin()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => BBDownTLoginUtil.WaitForQrLoginAsync(
            () => Task.FromResult((QrLoginStatus)123), _ => Task.CompletedTask, TimeSpan.Zero,
            () => throw new Exception("No scan expected"), 1));
    }

    [Theory]
    [InlineData("BBDownT.data")]
    public async Task SharedSaving_UsesPlatformFileAndPreservesContent(string fileName)
    {
        string? savedPath = null;
        string? savedContent = null;
        await BBDownTLoginUtil.SaveLoginDataAsync("/in-memory", fileName, "synthetic-credential",
            (path, content) =>
            {
                savedPath = path;
                savedContent = content;
                return Task.CompletedTask;
            });

        Assert.Equal(Path.Combine("/in-memory", fileName), savedPath);
        Assert.Equal("synthetic-credential", savedContent);
    }
}
