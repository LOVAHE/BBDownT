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

    [Fact]
    public void CallbackHandler_PinsAddressAndRejectsRedirects()
    {
        using var handler = BBDownTApiServer.CreatePinnedCallbackHandler(IPAddress.Parse("8.8.8.8"));

        Assert.False(handler.AllowAutoRedirect);
        Assert.NotNull(handler.ConnectCallback);
    }
}
