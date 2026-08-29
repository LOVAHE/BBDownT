using BBDownT;
using BBDownT.Core;

namespace BBDownT.Tests;

public sealed class CookieRefreshTests
{
    [Fact]
    public void NormalizeLoginCookieUsesAuthenticationSetCookieHeaders()
    {
        const string loginUrl = "https://passport.biligame.com/crossDomain?ticket=ticket-value&gourl=https%3A%2F%2Fwww.bilibili.com&first_domain=.bilibili.com";
        string[] setCookieHeaders =
        [
            "SESSDATA=session-value; Path=/; Domain=.bilibili.com; HttpOnly; Secure",
            "bili_jct=csrf-value; Path=/; Domain=.bilibili.com",
            "DedeUserID=12345; Path=/; Domain=.bilibili.com"
        ];

        var cookie = BBDownTCookieRefreshUtil.NormalizeLoginCookie(loginUrl, "refresh-value", setCookieHeaders);

        Assert.Contains("SESSDATA=session-value", cookie);
        Assert.Contains("bili_jct=csrf-value", cookie);
        Assert.Contains("DedeUserID=12345", cookie);
        Assert.Contains("ac_time_value=refresh-value", cookie);
        Assert.DoesNotContain("ticket=", cookie);
        Assert.DoesNotContain("gourl=", cookie);
        Assert.DoesNotContain("first_domain=", cookie);
        Assert.True(BBDownTCookieRefreshUtil.HasRequiredLoginCookies(cookie));
    }

    [Fact]
    public void NormalizeLoginCookieRejectsRedirectMetadataAsAuthentication()
    {
        const string loginUrl = "https://passport.biligame.com/crossDomain?ticket=ticket-value&gourl=https%3A%2F%2Fwww.bilibili.com&first_domain=.bilibili.com";

        var cookie = BBDownTCookieRefreshUtil.NormalizeLoginCookie(loginUrl, "refresh-value");

        Assert.Equal("ac_time_value=refresh-value", cookie);
        Assert.False(BBDownTCookieRefreshUtil.HasRequiredLoginCookies(cookie));
    }

    [Fact]
    public void ParseCookieRefreshStateReturnsServerTimestamp()
    {
        const string response = """
            {"code":0,"message":"0","ttl":1,"data":{"refresh":true,"timestamp":1724882400123}}
            """;

        var state = BBDownTCookieRefreshUtil.ParseCookieRefreshState(response);

        Assert.True(state.Refresh);
        Assert.Equal(1724882400123, state.Timestamp);
    }

    [Fact]
    public void ParseCookieRefreshStatePreservesApiError()
    {
        const string response = """
            {"code":-101,"message":"账号未登录","ttl":1}
            """;

        var exception = Assert.Throws<InvalidOperationException>(
            () => BBDownTCookieRefreshUtil.ParseCookieRefreshState(response));

        Assert.Equal("检查Cookie刷新状态失败: 账号未登录", exception.Message);
    }

    [Fact]
    public void RefreshFailureDiagnosticIsNotMistakenForCookieHeader()
    {
        var message = BBDownTCookieRefreshUtil.FormatRefreshFailureMessage(
            new InvalidOperationException("correspondPath过期或错误"));

        var redacted = Logger.RedactSensitiveText(message);

        Assert.Equal(message, redacted);
        Assert.Contains("原因：correspondPath过期或错误", redacted);
    }

    [Fact]
    public void RefreshFailureDiagnosticStillRedactsCredentials()
    {
        var message = BBDownTCookieRefreshUtil.FormatRefreshFailureMessage(
            new InvalidOperationException("接口拒绝 SESSDATA=secret-value"));

        var redacted = Logger.RedactSensitiveText(message);

        Assert.Contains("原因：接口拒绝", redacted);
        Assert.Contains("SESSDATA=<redacted>", redacted);
        Assert.DoesNotContain("secret-value", redacted);
    }
}
