using BBDownT.Core;

namespace BBDownT.Tests;

public class ConfigCredentialScopeTests : IDisposable
{
    private readonly string originalCookie = Config.COOKIE;
    private readonly string originalToken = Config.TOKEN;
    private readonly bool originalIntl = Config.COOKIE_IS_INTL;

    public void Dispose()
    {
        Config.COOKIE = originalCookie;
        Config.TOKEN = originalToken;
        Config.COOKIE_IS_INTL = originalIntl;
    }

    [Fact]
    public void InternationalCookieFlagIsIsolatedLikeTheCookie()
    {
        // 正在运行的国际站任务把全局开关设为 true：国内的解析预览不能因此改用国际站客户端、停发Cookie
        Config.COOKIE_IS_INTL = true;

        using (Config.UseCredentials("domestic", ""))
        {
            Assert.False(Config.COOKIE_IS_INTL);
            using (Config.UseCredentials("intl", "", international: true))
            {
                Assert.True(Config.COOKIE_IS_INTL);
                Config.COOKIE_IS_INTL = false;
                Assert.False(Config.COOKIE_IS_INTL);
            }
            Assert.False(Config.COOKIE_IS_INTL);
        }

        Assert.True(Config.COOKIE_IS_INTL);
    }

    [Fact]
    public void ReadsInsideScopeSeeTheScopedValuesAndGlobalsComeBackAfterDispose()
    {
        Config.COOKIE = "global-cookie";
        Config.TOKEN = "global-token";

        using (Config.UseCredentials("scoped-cookie", "scoped-token"))
        {
            Assert.Equal("scoped-cookie", Config.COOKIE);
            Assert.Equal("scoped-token", Config.TOKEN);
        }

        Assert.Equal("global-cookie", Config.COOKIE);
        Assert.Equal("global-token", Config.TOKEN);
    }

    [Fact]
    public void WriteInsideScope_GoesToTheScopeAndIsReadBack_WithoutTouchingGlobals()
    {
        Config.COOKIE = "global-cookie";
        Config.TOKEN = "global-token";

        using (Config.UseCredentials("old-cookie", "old-token"))
        {
            // 例如 Cookie 刷新：先写 Config.COOKIE，再把 Config.COOKIE 写回文件
            Config.COOKIE = "refreshed-cookie";
            Config.TOKEN = "new-token";
            Assert.Equal("refreshed-cookie", Config.COOKIE);
            Assert.Equal("new-token", Config.TOKEN);
            Assert.Equal("global-cookie", ReadGlobalCookieFromAnotherFlow());
        }

        Assert.Equal("global-cookie", Config.COOKIE);
        Assert.Equal("global-token", Config.TOKEN);
    }

    [Fact]
    public async Task WriteInsideAwaitedCallee_IsVisibleToTheCallerInTheSameScope()
    {
        Config.COOKIE = "global-cookie";

        using (Config.UseCredentials("old-cookie", ""))
        {
            await RefreshAsync("refreshed-cookie");
            Assert.Equal("refreshed-cookie", Config.COOKIE);
        }

        Assert.Equal("global-cookie", Config.COOKIE);
    }

    [Fact]
    public async Task ConcurrentScopes_AreIsolatedFromEachOtherAndFromGlobals()
    {
        Config.COOKIE = "global-cookie";
        Config.TOKEN = "global-token";
        var bothInside = new Barrier(2);

        async Task<(string Before, string After)> RunScope(string name)
        {
            await Task.Yield();
            using var _ = Config.UseCredentials($"{name}-cookie", $"{name}-token");
            bothInside.SignalAndWait(TimeSpan.FromSeconds(5));
            var before = Config.COOKIE + "|" + Config.TOKEN;
            Config.COOKIE = $"{name}-written";
            await Task.Delay(20);
            bothInside.SignalAndWait(TimeSpan.FromSeconds(5));
            return (before, Config.COOKIE + "|" + Config.TOKEN);
        }

        var results = await Task.WhenAll(Task.Run(() => RunScope("a")), Task.Run(() => RunScope("b")));

        Assert.Equal(("a-cookie|a-token", "a-written|a-token"), results[0]);
        Assert.Equal(("b-cookie|b-token", "b-written|b-token"), results[1]);
        Assert.Equal("global-cookie", Config.COOKIE);
        Assert.Equal("global-token", Config.TOKEN);
    }

    [Fact]
    public void NestedScope_RestoresTheOuterScopeAndDisposeIsIdempotent()
    {
        Config.COOKIE = "global-cookie";
        var outer = Config.UseCredentials("outer", "");
        var inner = Config.UseCredentials("inner", "");
        Assert.Equal("inner", Config.COOKIE);

        inner.Dispose();
        inner.Dispose();
        Assert.Equal("outer", Config.COOKIE);

        outer.Dispose();
        Assert.Equal("global-cookie", Config.COOKIE);
    }

    private static async Task RefreshAsync(string value)
    {
        await Task.Yield();
        Config.COOKIE = value;
    }

    // 不在作用域内的另一个执行流(例如正在运行的下载任务)读取到的仍是全局值
    private static string ReadGlobalCookieFromAnotherFlow()
    {
        using (ExecutionContext.SuppressFlow())
        {
            return Task.Run(() => Config.COOKIE).GetAwaiter().GetResult();
        }
    }
    [Fact]
    public void NetworkSettings_StartFromDefaultsInsideTheScope_AndWritesStayInside()
    {
        var (host, epHost, tvHost, area) = (Config.HOST, Config.EPHOST, Config.TVHOST, Config.AREA);
        try
        {
            Config.HOST = "global-host.example";
            Config.EPHOST = "global-ep.example";
            Config.TVHOST = "global-tv.example";
            Config.AREA = "hk";

            using (Config.UseCredentials("", ""))
            {
                Assert.Equal(("api.bilibili.com", "api.bilibili.com", "api.snm0516.aisee.tv", ""),
                    (Config.HOST, Config.EPHOST, Config.TVHOST, Config.AREA));
                Config.AREA = "tw";
                Assert.Equal("tw", Config.AREA);
            }

            Assert.Equal(("global-host.example", "global-ep.example", "global-tv.example", "hk"),
                (Config.HOST, Config.EPHOST, Config.TVHOST, Config.AREA));
        }
        finally
        {
            (Config.HOST, Config.EPHOST, Config.TVHOST, Config.AREA) = (host, epHost, tvHost, area);
        }
    }
}
