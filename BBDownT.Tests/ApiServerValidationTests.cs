using System.Net;

namespace BBDownT.Tests;

public class ApiServerValidationTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ValidateAndNormalizeServerRequest_RejectsMissingUrl(string? url)
    {
        var server = new BBDownTApiServer();
        var request = new ServeRequestOptions { Url = url! };

        var validationMessage = server.ValidateAndNormalizeServerRequest(request);

        Assert.Equal("Url不能为空", validationMessage);
    }

    [Fact]
    public void ValidateAndNormalizeServerRequest_AcceptsValidUrlAndNormalizesWorkDir()
    {
        var server = new BBDownTApiServer();
        var request = new ServeRequestOptions { Url = "BV1xx411c7mD" };

        var validationMessage = server.ValidateAndNormalizeServerRequest(request);

        Assert.Null(validationMessage);
        Assert.Equal(Path.GetFullPath(Environment.CurrentDirectory), request.WorkDir);
    }

    [Fact]
    public void ValidateAndNormalizeServerRequest_RejectsPrivateCallbackByDefault()
    {
        var server = new BBDownTApiServer();
        var request = new ServeRequestOptions
        {
            Url = "BV1xx411c7mD",
            CallBackWebHook = "http://127.0.0.1/callback"
        };

        var validationMessage = server.ValidateAndNormalizeServerRequest(request);

        Assert.Contains("内网", validationMessage);
    }

    [Theory]
    [InlineData("127.0.0.1", true)]
    [InlineData("10.0.0.1", true)]
    [InlineData("172.16.0.1", true)]
    [InlineData("192.168.1.1", true)]
    [InlineData("169.254.1.1", true)]
    [InlineData("100.64.0.1", true)]
    [InlineData("8.8.8.8", false)]
    [InlineData("2001:4860:4860::8888", false)]
    [InlineData("fc00::1", true)]
    [InlineData("fd00::1", true)]
    [InlineData("::1", true)]
    public void IsPrivateOrReservedAddress_ClassifiesNetworkBoundary(string value, bool expected)
    {
        Assert.Equal(expected, BBDownTApiServer.IsPrivateOrReservedAddress(IPAddress.Parse(value)));
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("1-")]
    [InlineData("0")]
    [InlineData("3-1")]
    [InlineData("ALL,1")]
    [InlineData("1,,2")]
    public void ValidateAndNormalizeServerRequest_RejectsMalformedSelectPageBeforeQueueing(string selectPage)
    {
        var request = new ServeRequestOptions { Url = "BV1xx411c7mD", SelectPage = selectPage };

        var message = new BBDownTApiServer().ValidateAndNormalizeServerRequest(request);

        Assert.NotNull(message);
        Assert.StartsWith("「分P」写法无效", message);
        Assert.Contains("ALL", message);
    }

    [Theory]
    [InlineData("1")]
    [InlineData(" 1, 3-5 ,LATEST ")]
    [InlineData("all")]
    [InlineData("1-2147483647")]
    [InlineData("")]
    public void ValidateAndNormalizeServerRequest_OnlyChecksSelectPageSyntax(string selectPage)
    {
        var request = new ServeRequestOptions { Url = "BV1xx411c7mD", SelectPage = selectPage };
        var watch = System.Diagnostics.Stopwatch.StartNew();

        Assert.Null(new BBDownTApiServer().ValidateAndNormalizeServerRequest(request));
        // 超大范围不会被展开
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(1));
    }

    [Fact]
    public void ValidateAndNormalizeServerRequest_ExplainsDownloadAllWithTheWebUiWording()
    {
        var request = new ServeRequestOptions { Url = "https://www.bilibili.com/video/BV1xx411c7mD", DownloadAll = true };

        var message = new BBDownTApiServer().ValidateAndNormalizeServerRequest(request);

        Assert.NotNull(message);
        Assert.StartsWith("「UP主空间链接：下载全部投稿」只适用于 space.bilibili.com/数字 形式的空间链接", message);
        Assert.Contains("DownloadAll", message);
        // 命令行仍使用参数名
        Assert.Contains("--download-all", SpaceBatchDownload.ValidateOptions(request));
    }

    [Fact]
    public void RejectedRequestLog_ListsFieldNamesWithoutValuesOrUrlQuery()
    {
        var request = new ServeRequestOptions
        {
            Url = "https://www.bilibili.com/video/BV1xx411c7mD/?spm_id_from=333&vd_source=secret-share",
            Cookie = "SESSDATA=secret-cookie",
            AccessToken = "secret-token",
            DownloadAll = true,
            DelayPerVideo = 10
        };

        var description = BBDownTApiServer.DescribeRequestForLog(request);

        Assert.Equal(["Url", "Cookie", "AccessToken", "DownloadAll"], BBDownTApiServer.ChangedFieldNames(request));
        Assert.Contains("Cookie", description);
        Assert.Contains("https://www.bilibili.com/video/BV1xx411c7mD/", description);
        Assert.DoesNotContain("secret", description);
        Assert.DoesNotContain("spm_id_from", description);
        Assert.Equal("BV1xx411c7mD", BBDownTApiServer.DescribeUrlForLog("BV1xx411c7mD"));
        Assert.Equal("非链接输入（9 个字符）", BBDownTApiServer.DescribeUrlForLog("【标题】 分享文本"));
    }

    [Fact]
    public void CallbackHandler_PinsAddressAndRejectsRedirects()
    {
        using var handler = BBDownTApiServer.CreatePinnedCallbackHandler(IPAddress.Parse("8.8.8.8"));

        Assert.False(handler.AllowAutoRedirect);
        Assert.NotNull(handler.ConnectCallback);
    }
}
