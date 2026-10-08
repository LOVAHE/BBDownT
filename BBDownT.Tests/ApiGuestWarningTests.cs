namespace BBDownT.Tests;

/// <summary>
/// APP/TV 接口没有 access_token 时按游客解析：两者的限制不同，提示要分别说明；WEB 不提示
/// </summary>
public class ApiGuestWarningTests
{
    [Fact]
    public void AppWithoutToken_WarnsAbout480PAndSingleCodec()
    {
        var warning = Program.MissingApiTokenWarning(new MyOption { UseAppApi = true }, "");

        Assert.NotNull(warning);
        Assert.Contains("APP", warning);
        Assert.Contains("通常最高 480P，且一次只返回一种编码", warning);
        Assert.Contains("网页扫码登录对APP接口无效", warning);
    }

    [Fact]
    public void TvWithoutToken_Warns1080PAndAboveAreMissingButNot480P()
    {
        var warning = Program.MissingApiTokenWarning(new MyOption { UseTvApi = true }, "");

        Assert.NotNull(warning);
        Assert.Contains("TV", warning);
        // 实测TV游客最高 720P，1080P 高清(qn 80)也拿不到
        Assert.Contains("拿不到 1080P 及以上画质（未登录通常最高 720P）", warning);
        Assert.DoesNotContain("480P", warning);
        Assert.Contains("logintv", warning);
    }

    [Theory]
    [InlineData(true, false, "通常最高 480P，且一次只返回一种编码")]
    [InlineData(false, true, "拿不到 1080P 及以上画质（未登录通常最高 720P）")]
    public void WebHint_SaysTheSameButDoesNotMentionCommandLineLogin(bool app, bool tv, string expected)
    {
        var hint = VideoParseService.GuestApiHint(new MyOption { UseAppApi = app, UseTvApi = tv }, authenticated: false);

        Assert.NotNull(hint);
        Assert.Contains(expected, hint);
        Assert.Contains("改用 WEB 接口", hint);
        Assert.DoesNotContain("logintv", hint);
        Assert.DoesNotContain("access_token", hint);
        Assert.Null(VideoParseService.GuestApiHint(new MyOption { UseAppApi = app, UseTvApi = tv }, authenticated: true));
    }

    [Theory]
    [InlineData(false, false, "")]
    [InlineData(true, false, "token")]
    [InlineData(false, true, "token")]
    public void WebOrAuthenticatedApi_HasNoWarning(bool app, bool tv, string token)
    {
        Assert.Null(Program.MissingApiTokenWarning(new MyOption { UseAppApi = app, UseTvApi = tv }, token));
    }

    [Fact]
    public void LoginCheckLog_SaysTheAppApiDoesNotUseTheWebLogin()
    {
        Assert.Equal("检测账号登录...", Program.LoginCheckMessage(new MyOption()));
        var app = Program.LoginCheckMessage(new MyOption { UseAppApi = true });
        Assert.Contains("网页账号", app);
        Assert.Contains("APP接口只认access_token", app);
    }
}
