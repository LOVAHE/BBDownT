using System.Net;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text.Json;
using BBDownT.Core.Util;

namespace BBDownT.Tests;

public class ErrorTextTests
{
    private const string PrivateMessage = "https://name:password@example.test/path?access_token=secret#cookie SESSDATA=secret";

    [Fact]
    public void TransportFailures_AreSummarizedWithoutTheirMessages()
    {
        Assert.Equal("域名解析失败", ErrorText.Describe(new HttpRequestException(HttpRequestError.NameResolutionError,
            PrivateMessage, new SocketException((int)SocketError.HostNotFound))));
        Assert.Equal("服务器返回 HTTP 403", ErrorText.Describe(new HttpRequestException(PrivateMessage, null, HttpStatusCode.Forbidden)));
        Assert.Equal("请求超时", ErrorText.Describe(new TaskCanceledException(PrivateMessage, new TimeoutException(PrivateMessage))));
        Assert.Equal("TLS 安全连接失败", ErrorText.Describe(new HttpRequestException(PrivateMessage, new AuthenticationException(PrivateMessage))));
        Assert.Equal("网络连接失败（ConnectionReset）", ErrorText.Describe(new IOException("net_io_readfailure, Connection reset by peer",
            new SocketException((int)SocketError.ConnectionReset))));
    }

    [Fact]
    public void OwnMessages_AreShownWithoutCredentialsOrQueryStrings()
    {
        var description = ErrorText.Describe(new IOException(PrivateMessage));

        Assert.Contains("https://example.test/path", description);
        Assert.DoesNotContain("password", description);
        Assert.DoesNotContain("access_token", description);
        Assert.DoesNotContain("SESSDATA=secret", description);
    }

    [Fact]
    public void Context_IsCombinedWithTheRootCause()
    {
        Assert.Equal("分片 3 下载失败（服务器返回 HTTP 502）", ErrorText.Describe(
            new IOException("分片 3 下载失败", new HttpRequestException("bad gateway", null, HttpStatusCode.BadGateway))));
        Assert.Equal("任务失败：磁盘已满", ErrorText.Describe(new InvalidOperationException("任务失败", new IOException("磁盘已满"))));
    }

    [Fact]
    public void ApiRefusals_KeepTheirReason()
    {
        var refusal = new BilibiliApiException("获取视频信息失败：啥都木有（错误码 -404）", -404);

        Assert.Equal(refusal.Message, ErrorText.Describe(new IOException("wrapped", refusal)));
        Assert.False(ErrorText.SuggestsUpdate(refusal));
    }

    [Fact]
    public void ResourceKeys_BecomeReadableDescriptions()
    {
        Assert.Equal("接口返回的数据格式与预期不同", ErrorText.Describe(new KeyNotFoundException("Arg_KeyNotFound")));
        Assert.Equal("没有权限读写文件或目录：/data/out.mp4",
            ErrorText.Describe(new UnauthorizedAccessException("UnauthorizedAccess_IODenied_Path, /data/out.mp4")));
    }

    [Fact]
    public void ProtobufFailuresAndCancellation_HaveReadableDescriptions()
    {
        var protobuf = Assert.ThrowsAny<Google.Protobuf.InvalidProtocolBufferException>(
            () => BBDownT.Core.Protobuf.PlayViewReply.Parser.ParseFrom(new byte[] { 0 }));

        Assert.Equal("接口返回的数据格式与预期不同", ErrorText.Describe(protobuf));
        Assert.True(ErrorText.SuggestsUpdate(protobuf));
        Assert.Equal("操作已取消", ErrorText.Describe(new OperationCanceledException()));
        Assert.False(ErrorText.SuggestsUpdate(new OperationCanceledException()));
    }

    [Fact]
    public void OnlyUnexpectedResponseShapes_SuggestUpdating()
    {
        Assert.True(ErrorText.SuggestsUpdate(new KeyNotFoundException("Arg_KeyNotFound")));
        Assert.True(ErrorText.SuggestsUpdate(new InvalidOperationException("wrapped", new JsonException())));
        Assert.False(ErrorText.SuggestsUpdate(new HttpRequestException("unavailable", null, HttpStatusCode.ServiceUnavailable)));
        Assert.False(ErrorText.SuggestsUpdate(new IOException("磁盘已满")));
    }
}
