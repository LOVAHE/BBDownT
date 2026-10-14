using BBDownT.Core.Util;

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
    [InlineData("""{"code":0,"data":{"code":86101,"message":"未扫码"}}""", "Waiting")]
    [InlineData("""{"code":0,"data":{"code":86090,"message":"二维码已扫码未确认"}}""", "Scanned")]
    [InlineData("""{"code":0,"data":{"code":86038,"message":"二维码已失效"}}""", "Expired")]
    [InlineData("""{"code":0,"data":{"code":0,"message":"","url":"https://example.invalid/"}}""", "Success")]
    public void WebPolling_MapsDocumentedStatuses(string response, string expected)
    {
        Assert.Equal(expected, BBDownTLoginUtil.ReadWebQrStatus(response).ToString());
    }

    [Theory]
    [InlineData("""{"code":86039,"message":"二维码尚未确认"}""", "Waiting")]
    [InlineData("""{"code":86090,"message":"二维码已扫码未确认"}""", "Scanned")]
    [InlineData("""{"code":86038,"message":"二维码已失效"}""", "Expired")]
    [InlineData("""{"code":0,"message":"0","data":{"access_token":"synthetic"}}""", "Success")]
    public void TvPolling_MapsDocumentedStatuses(string response, string expected)
    {
        Assert.Equal(expected, BBDownTLoginUtil.ReadTvQrStatus(response).ToString());
    }

    [Fact]
    public void Polling_UnknownStatusIsAnErrorRatherThanSuccess()
    {
        var web = Assert.Throws<BilibiliApiException>(() => BBDownTLoginUtil.ReadWebQrStatus(
            """{"code":0,"data":{"code":86099,"message":"未知状态"}}"""));
        var tv = Assert.Throws<BilibiliApiException>(() => BBDownTLoginUtil.ReadTvQrStatus(
            """{"code":-3,"message":"API校验密匙错误"}"""));

        Assert.Equal(86099, web.Code);
        Assert.Equal("查询扫码状态失败：未知状态（错误码 86099）", web.Message);
        Assert.Equal(-3, tv.Code);
        Assert.Equal("查询扫码状态失败：API校验密匙错误（错误码 -3）", tv.Message);
        Assert.Throws<InvalidOperationException>(() => BBDownTLoginUtil.ReadTvQrStatus("""{"code":"pending"}"""));
    }

    [Fact]
    public async Task SharedSaving_ReplacesTheFileWithOwnerOnlyPermissions()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"bbd-login-data-{Guid.NewGuid():N}");
        var path = Path.Combine(directory, "BBDownT.data");
        try
        {
            Directory.CreateDirectory(directory);
            File.WriteAllText(path, "old-credential");
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite
                    | UnixFileMode.GroupRead | UnixFileMode.OtherRead);

            await BBDownTLoginUtil.SaveLoginDataAsync(path, "synthetic-credential");

            Assert.Equal("synthetic-credential", File.ReadAllText(path));
            Assert.Equal(new[] { path }, Directory.GetFiles(directory));
            if (!OperatingSystem.IsWindows())
                Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
        }
        finally
        {
            File.Delete(path);
            Directory.Delete(directory);
        }
    }
}
