using System.Net;
using System.Text;
using BBDownT;
using BBDownT.Core;

namespace BBDownT.Tests;

public sealed class CookieRefreshTests
{
    private const string StoredCookie = "SESSDATA=old-session;bili_jct=old-csrf;DedeUserID=1;ac_time_value=old-refresh";

    [Fact]
    public async Task ManualCookie_IsNotRefreshedBecauseItsReplacementCouldNotBeSaved()
    {
        var original = Config.COOKIE;
        var endpoints = new RefreshEndpoints();
        using var client = new HttpClient(endpoints);
        try
        {
            Config.COOKIE = StoredCookie;
            await BBDownTCookieRefreshUtil.TryRefreshCookieAsync(null, client,
                (_, _) => throw new InvalidOperationException("a manual cookie has no file"));

            Assert.Equal(StoredCookie, Config.COOKIE);
            Assert.Equal(new[] { "GET /x/passport-login/web/cookie/info" }, endpoints.Requests);
        }
        finally
        {
            Config.COOKIE = original;
        }
    }

    [Fact]
    public async Task FileCookie_IsSavedBeforeConfirmationRetiresTheOldOne()
    {
        var original = Config.COOKIE;
        var endpoints = new RefreshEndpoints();
        using var client = new HttpClient(endpoints);
        string? saved = null;
        try
        {
            Config.COOKIE = StoredCookie;
            await BBDownTCookieRefreshUtil.TryRefreshCookieAsync("/in-memory/BBDownT.data", client, (path, cookie) =>
            {
                Assert.Equal("/in-memory/BBDownT.data", path);
                endpoints.Requests.Add("SAVE");
                saved = cookie;
                return Task.CompletedTask;
            });

            Assert.Equal(new[]
            {
                "GET /x/passport-login/web/cookie/info", "GET /correspond", "POST /x/passport-login/web/cookie/refresh",
                "SAVE", "POST /x/passport-login/web/confirm/refresh"
            }, endpoints.Requests);
            Assert.Equal(saved, Config.COOKIE);
            Assert.Contains("SESSDATA=new-session", saved);
            Assert.Contains("ac_time_value=new-refresh", saved);
            Assert.Contains("refresh_token=old-refresh", endpoints.RefreshForm);
            Assert.DoesNotContain(endpoints.Cookies, cookie => cookie.Contains("ac_time_value") || cookie.Contains("refresh"));
        }
        finally
        {
            Config.COOKIE = original;
        }
    }

    [Fact]
    public async Task SaveFailure_KeepsTheOldCookieAndNeverConfirms()
    {
        var original = Config.COOKIE;
        var endpoints = new RefreshEndpoints();
        using var client = new HttpClient(endpoints);
        try
        {
            Config.COOKIE = StoredCookie;
            await BBDownTCookieRefreshUtil.TryRefreshCookieAsync("/in-memory/BBDownT.data", client,
                (_, _) => throw new IOException("read-only directory"));

            Assert.Equal(StoredCookie, Config.COOKIE);
            Assert.DoesNotContain("POST /x/passport-login/web/confirm/refresh", endpoints.Requests);
        }
        finally
        {
            Config.COOKIE = original;
        }
    }

    private sealed class RefreshEndpoints : HttpMessageHandler
    {
        internal List<string> Requests { get; } = [];
        internal List<string> Cookies { get; } = [];
        internal string RefreshForm { get; private set; } = "";

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            Requests.Add($"{request.Method} {(path.StartsWith("/correspond/1/") ? "/correspond" : path)}");
            if (request.Headers.TryGetValues("Cookie", out var cookie)) Cookies.Add(string.Join("; ", cookie));
            switch (path)
            {
                case "/x/passport-login/web/cookie/info":
                    return Json("""{"code":0,"data":{"refresh":true,"timestamp":1724882400123}}""");
                case "/x/passport-login/web/cookie/refresh":
                    RefreshForm = await request.Content!.ReadAsStringAsync(cancellationToken);
                    var refreshed = Json("""{"code":0,"data":{"refresh_token":"new-refresh"}}""");
                    refreshed.Headers.TryAddWithoutValidation("Set-Cookie", "SESSDATA=new-session; Path=/; Domain=.bilibili.com");
                    refreshed.Headers.TryAddWithoutValidation("Set-Cookie", "bili_jct=new-csrf; Path=/; Domain=.bilibili.com");
                    return refreshed;
                case "/x/passport-login/web/confirm/refresh":
                    return Json("""{"code":0}""");
                default:
                    return path.StartsWith("/correspond/1/")
                        ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<div id=\"1-name\">refresh-csrf</div>") }
                        : new HttpResponseMessage(HttpStatusCode.NotFound);
            }
        }

        private static HttpResponseMessage Json(string body)
            => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }

    [Theory]
    [InlineData("", false)]
    [InlineData("SESSDATA=", false)]
    [InlineData("SESSDATA=test-session", true)]
    [InlineData("SESSDATA=test-session; bili_jct=", true)]
    [InlineData("SESSDATA=test-session; bili_jct=   ", true)]
    [InlineData("SESSDATA=test-session; bili_jct=test-csrf", false)]
    [InlineData(" sessdata = test-session ; BILI_JCT = test-csrf ", false)]
    [InlineData("SESSDATA=mentions-bili_jct; unrelated=value", true)]
    [InlineData("bili_jct=test-csrf", false)]
    public void MissingBiliJctWarning_UsesParsedCookieValues(string cookieHeader, bool expected)
    {
        Assert.Equal(expected, BBDownTCookieRefreshUtil.IsMissingBiliJct(cookieHeader));
    }

    [Fact]
    public async Task MissingBiliJct_RefreshLeavesManualCookieUnchanged()
    {
        var original = Config.COOKIE;
        const string manualCookie = "SESSDATA=test-session; unrelated=keep-this";
        try
        {
            Config.COOKIE = manualCookie;
            await BBDownTCookieRefreshUtil.TryRefreshCookieAsync(null);
            Assert.Equal(manualCookie, Config.COOKIE);
        }
        finally
        {
            Config.COOKIE = original;
        }
    }

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
