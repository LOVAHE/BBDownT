using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Http;

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

    [Fact]
    public void CallbackHandler_PinsAddressAndRejectsRedirects()
    {
        using var handler = BBDownTApiServer.CreatePinnedCallbackHandler(IPAddress.Parse("8.8.8.8"));

        Assert.False(handler.AllowAutoRedirect);
        Assert.NotNull(handler.ConnectCallback);
    }

    [Theory]
    [InlineData("https://www.bilibili.com/video/BV1xx411c7mD", true)]
    [InlineData("https://b23.tv/share", true)]
    [InlineData("https://www.bilibili.tv/en/play/1034193", true)]
    [InlineData("ep123", true)]
    [InlineData("http://127.0.0.1:8080/admin", false)]
    [InlineData("http://169.254.169.254/latest/meta-data", false)]
    [InlineData("https://example.test/video/BV1xx411c7mD", false)]
    [InlineData("https://www.bilibili.com.evil.test/video/BV1xx411c7mD", false)]
    [InlineData("https://user@www.bilibili.com/video/BV1xx411c7mD", false)]
    public void ValidateAndNormalizeServerRequest_AcceptsOnlyBilibiliLinksOrIds(string url, bool accepted)
    {
        var validationMessage = new BBDownTApiServer().ValidateAndNormalizeServerRequest(new ServeRequestOptions { Url = url });

        if (accepted) Assert.Null(validationMessage);
        else Assert.Equal("Url只能是B站链接或av/BV/ep/ss等编号", validationMessage);
    }

    [Theory]
    [InlineData("\"FFmpegPath\":\"/bin/sh\"", "服务器任务不能指定FFmpegPath、Mp4boxPath或Aria2cPath")]
    [InlineData("\"Mp4boxPath\":\"/bin/sh\"", "服务器任务不能指定FFmpegPath、Mp4boxPath或Aria2cPath")]
    [InlineData("\"Aria2cPath\":\"/bin/sh\"", "服务器任务不能指定FFmpegPath、Mp4boxPath或Aria2cPath")]
    [InlineData("\"Aria2cProxy\":\"http://proxy.example.test:3128\"", "服务器任务不支持旧版兼容参数")]
    [InlineData("\"OnlyHevc\":true", "服务器任务不支持旧版兼容参数")]
    [InlineData("\"OnlyAvc\":true", "服务器任务不支持旧版兼容参数")]
    [InlineData("\"OnlyAv1\":true", "服务器任务不支持旧版兼容参数")]
    [InlineData("\"AddDfnSubfix\":true", "服务器任务不支持旧版兼容参数")]
    [InlineData("\"NoPaddingPageNum\":true", "服务器任务不支持旧版兼容参数")]
    [InlineData("\"BandwithAscending\":true", "服务器任务不支持旧版兼容参数")]
    public void ValidateAndNormalizeServerRequest_RejectsExecutablePathsAndLegacyOptionsFromJson(string field, string message)
    {
        var request = JsonSerializer.Deserialize("{\"Url\":\"BV1xx411c7mD\"," + field + "}",
            SourceGenerationContext.Default.ServeRequestOptions)!;

        var validationMessage = new BBDownTApiServer().ValidateAndNormalizeServerRequest(request);

        Assert.StartsWith(message, validationMessage);
    }

    [Theory]
    [InlineData("127.0.0.1:23333", null, null, true)]
    [InlineData("localhost:23333", null, null, true)]
    [InlineData("[::1]:23333", null, null, true)]
    [InlineData("localhost:23333", "http://localhost:8080", null, true)]
    [InlineData("127.0.0.1:23333", "http://127.0.0.1:3000", null, true)]
    [InlineData("127.0.0.1:23333", "https://evil.example.test", null, false)]
    [InlineData("127.0.0.1:23333", "null", null, false)]
    [InlineData("evil.example.test:23333", null, null, false)]
    [InlineData("", null, null, false)]
    [InlineData("127.0.0.1:23333", null, "X-Forwarded-For", false)]
    [InlineData("127.0.0.1:23333", null, "Forwarded", false)]
    [InlineData("127.0.0.1:23333", null, "X-Real-IP", false)]
    [InlineData("127.0.0.1:23333", null, "x-forwarded-proto", false)]
    [InlineData("127.0.0.1:23333", null, "Via", false)]
    public void TokenlessServer_AcceptsOnlyDirectLocalRequests(string host, string? origin, string? forwardingHeader, bool accepted)
    {
        var context = new DefaultHttpContext();
        context.Request.Host = new HostString(host);
        if (origin is not null) context.Request.Headers.Origin = origin;
        if (forwardingHeader is not null) context.Request.Headers[forwardingHeader] = "203.0.113.7";

        Assert.Equal(accepted, BBDownTApiServer.IsDirectLocalRequest(context.Request));
    }
}
