using System.Net;
using System.Text.Json.Nodes;
using System.Web;

namespace BBDownT.Tests;

public class IntlLoginTests
{
    [Fact]
    public async Task LoginHelp_DescribesOnlyTheLoginFlowWithoutParentDownloadAndServerOptions()
    {
        var original = Console.Out;
        using var output = new StringWriter();
        try
        {
            Console.SetOut(output);
            var exit = await Program.InvokeCommandLineAsync(["loginintl", "--help"],
                _ => throw new Exception("No download expected"), () => throw new Exception("No migration expected"),
                loginIntl: _ => throw new Exception("No login expected"));
            Assert.Equal(0, exit);
            Assert.Contains("loginintl [options]", output.ToString());
            Assert.Contains("--import-cookie", output.ToString());
            Assert.DoesNotContain("<url>", output.ToString());
            Assert.DoesNotContain("--api-token", output.ToString());
            Assert.DoesNotContain("--config-file", output.ToString());
        }
        finally { Console.SetOut(original); }
    }

    [Fact]
    public void TicketQr_RendersWithoutDoubleWidthTerminalWrapping()
    {
        using var generator = new QRCoder.QRCodeGenerator();
        using var data = generator.CreateQrCode("https://www.biliintl.com/h5/en/qrcode/login?ticket=" + new string('x', 160),
            QRCoder.QRCodeGenerator.ECCLevel.Q);
        var rows = BBDownTIntlLoginUtil.CreateQrRows(data);
        Assert.All(rows, row => Assert.Equal(data.ModuleMatrix.Count, row.Length));
        Assert.Equal((data.ModuleMatrix.Count + 1) / 2, rows.Length);
        Assert.True(rows[0].Length <= 80);
    }

    [Fact]
    public async Task Generate_UsesOfficialEndpointAndReturnsDecodedTicketWithoutGlobalCookies()
    {
        const string ticket = "synthetic+/=&?#";
        using var handler = new StubHandler((request, _) =>
        {
            Assert.Equal("passport.bilibili.tv", request.RequestUri!.Host);
            Assert.Equal("/x/intl/passport-login/qrcode/auth/url", request.RequestUri.AbsolutePath);
            var query = HttpUtility.ParseQueryString(request.RequestUri.Query);
            Assert.Equal("en_US", query["sLocale"]);
            Assert.Equal("web", query["platform"]);
            Assert.False(request.Headers.Contains("Cookie"));
            Assert.Equal("https://www.bilibili.tv/", request.Headers.Referrer!.AbsoluteUri);
            Assert.Equal("https://www.bilibili.tv/", Assert.Single(request.Headers.GetValues("Referer")));
            Assert.Equal("https://www.bilibili.tv", Assert.Single(request.Headers.GetValues("Origin")));
            Assert.Equal("same-site", Assert.Single(request.Headers.GetValues("Sec-Fetch-Site")));
            return Json(QrResponse(ticket));
        });
        using var client = new IntlLoginClient(handler, disposeHandler: false);

        var result = await client.GenerateAsync();

        Assert.Equal(ticket, result.Ticket);
        Assert.Equal("www.biliintl.com", result.Url.Host);
    }

    [Theory]
    [InlineData(10018101, "Waiting")]
    [InlineData(10018102, "Scanned")]
    [InlineData(10018100, "Expired")]
    [InlineData(0, "Success")]
    public async Task Poll_MapsOfficialStatusAndEscapesTicket(int code, string expected)
    {
        const string ticket = "synthetic&next=https://evil.test/+?=#";
        using var handler = new StubHandler((request, _) =>
        {
            Assert.Equal("/x/intl/passport-login/qrcode/auth/fetch", request.RequestUri!.AbsolutePath);
            var query = HttpUtility.ParseQueryString(request.RequestUri.Query);
            Assert.Equal(ticket, query["ticket"]);
            Assert.Null(query["next"]);
            return Json(PollResponse(code, "https://www.biliintl.com/after-login"));
        });
        using var client = new IntlLoginClient(handler, false);

        var result = await client.PollAsync(ticket);

        Assert.Equal(Enum.Parse<IntlQrStatus>(expected), result.Status);
        if (code == 0) Assert.Equal("www.biliintl.com", result.GoUrl!.Host);
        else Assert.Null(result.GoUrl);
    }

    [Fact]
    public async Task Poll_UnknownCodeFailsWithoutExposingUpstreamMessage()
    {
        using var handler = new StubHandler((_, _) => Json("{\"code\":12345,\"message\":\"synthetic-secret-cookie\"}"));
        using var client = new IntlLoginClient(handler, false);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => client.PollAsync("synthetic-ticket"));

        Assert.DoesNotContain("synthetic-secret", error.ToString());
    }

    [Theory]
    [InlineData("http://www.biliintl.com/sso?secret=synthetic")]
    [InlineData("https://api.bilibili.tv:8443/sso?secret=synthetic")]
    [InlineData("https://secret@api.bilibili.tv/sso")]
    [InlineData("https://api.bilibili.tv.evil.test/sso?secret=synthetic")]
    [InlineData("https://www.bilibili.com/sso?secret=synthetic")]
    [InlineData("//evil.test/sso?secret=synthetic")]
    public async Task Complete_RejectsUntrustedSsoTargetsBeforeRequestingThem(string target)
    {
        using var handler = new StubHandler((_, _) => Json(SsoResponse(target)));
        using var client = new IntlLoginClient(handler, false);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => client.CompleteAsync(null));

        Assert.Single(handler.Requests);
        Assert.DoesNotContain("secret", error.ToString());
        Assert.DoesNotContain("evil.test", error.ToString());
    }

    [Fact]
    public async Task Complete_ValidatesEveryTargetBeforeFollowingAnySsoUrl()
    {
        using var handler = new StubHandler((_, _) => Json(SsoResponse(
            "https://api.bilibili.tv/sso", "https://evil.test/sso?synthetic-secret")));
        using var client = new IntlLoginClient(handler, false);

        await Assert.ThrowsAsync<InvalidOperationException>(() => client.CompleteAsync(null));

        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Complete_RejectsUntrustedGoUrlBeforeFirstRequest()
    {
        using var handler = new StubHandler((_, _) => throw new Exception("No request expected"));
        using var client = new IntlLoginClient(handler, false);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.CompleteAsync(new Uri("https://evil.test/?synthetic-secret")));

        Assert.Empty(handler.Requests);
        Assert.DoesNotContain("synthetic-secret", error.ToString());
    }

    [Fact]
    public async Task Complete_FollowsTrustedRelativeRedirectAndReturnsApiApplicableCookies()
    {
        using var handler = new StubHandler((request, index) => index switch
        {
            0 => Json(SsoResponse("https://passport.bilibili.tv/sso/start")),
            1 => Redirect("//api.bilibili.tv/sso/finish"),
            2 => WithCookie(Json("{}"), "intl_auth=synthetic-session; Domain=.bilibili.tv; Path=/; Secure"),
            3 => Json(UserResponse(true)),
            _ => throw new Exception("Unexpected request")
        });
        using var client = new IntlLoginClient(handler, false);

        var cookie = await client.CompleteAsync(null);

        Assert.Equal("intl_auth=synthetic-session", cookie);
        Assert.Equal("api.bilibili.tv", handler.Requests[2].Host);
        Assert.Equal(4, handler.Requests.Count);
    }

    [Fact]
    public async Task LoginRequestMetadata_FollowsInternationalOriginAcrossTrustedSsoDomains()
    {
        using var handler = new StubHandler((request, index) =>
        {
            Assert.Equal("https://www.bilibili.tv/", Assert.Single(request.Headers.GetValues("Referer")));
            Assert.Equal(index == 1 ? "cross-site" : "same-site",
                Assert.Single(request.Headers.GetValues("Sec-Fetch-Site")));
            return index switch
            {
                0 => WithCookie(Json(SsoResponse("https://www.biliintl.com/sso")),
                    "intl_auth=synthetic-session; Domain=.bilibili.tv; Path=/; Secure"),
                1 => Json("{}"),
                2 => Json(UserResponse(true)),
                _ => throw new Exception("Unexpected request")
            };
        });
        using var client = new IntlLoginClient(handler, false);

        Assert.Equal("intl_auth=synthetic-session", await client.CompleteAsync(null));
        Assert.Equal(3, handler.Requests.Count);
    }

    [Fact]
    public async Task Complete_NeverFollowsAnUntrustedRedirectOrLeaksItsQuery()
    {
        using var handler = new StubHandler((_, index) => index == 0
            ? Json(SsoResponse("https://api.bilibili.tv/sso"))
            : Redirect("https://evil.test/?ticket=synthetic-secret"));
        using var client = new IntlLoginClient(handler, false);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => client.CompleteAsync(null));

        Assert.Equal(2, handler.Requests.Count);
        Assert.All(handler.Requests, request => Assert.NotEqual("evil.test", request.Host));
        Assert.DoesNotContain("synthetic-secret", error.ToString());
    }

    [Fact]
    public async Task Complete_BoundsRedirectChains()
    {
        using var handler = new StubHandler((_, index) => index == 0
            ? Json(SsoResponse("https://api.bilibili.tv/sso")) : Redirect("/sso"));
        using var client = new IntlLoginClient(handler, false);

        await Assert.ThrowsAsync<InvalidOperationException>(() => client.CompleteAsync(null));

        Assert.Equal(10, handler.Requests.Count);
    }

    [Fact]
    public async Task SuccessfulPollCookies_AreSentToSsoAndNeedNoDomesticCookieNames()
    {
        using var handler = new StubHandler((request, index) =>
        {
            if (index == 0) return WithCookie(Json(PollResponse(0)),
                "intl_auth=synthetic-poll-session; Domain=.bilibili.tv; Path=/; Secure");
            Assert.Contains("intl_auth=synthetic-poll-session", Assert.Single(request.Headers.GetValues("Cookie")));
            return Json(index == 1 ? SsoResponse() : UserResponse(true));
        });
        using var client = new IntlLoginClient(handler, false);

        var poll = await client.PollAsync("synthetic-ticket");
        var cookie = await client.CompleteAsync(poll.GoUrl);

        Assert.Equal("intl_auth=synthetic-poll-session", cookie);
        Assert.DoesNotContain("SESSDATA", cookie);
        Assert.DoesNotContain("bili_jct", cookie);
    }

    [Fact]
    public async Task GenerateCookies_AreRetainedOnlyInsideTheLoginClient()
    {
        using var handler = new StubHandler((request, index) =>
        {
            if (index == 0) return WithCookie(Json(QrResponse()), "qr_context=synthetic-context; Path=/; Secure");
            Assert.Contains("qr_context=synthetic-context", Assert.Single(request.Headers.GetValues("Cookie")));
            return Json(PollResponse(10018101));
        });
        using var client = new IntlLoginClient(handler, false);

        var qr = await client.GenerateAsync();
        Assert.Equal(IntlQrStatus.Waiting, (await client.PollAsync(qr.Ticket)).Status);
    }

    [Fact]
    public async Task Complete_UsesGoUrlToEstablishLoginWhenInitialSsoIsUnauthenticated()
    {
        using var handler = new StubHandler((_, index) => index switch
        {
            0 => Json("{\"code\":-101}"),
            1 => Redirect("https://api.bilibili.tv/sso/finish"),
            2 => WithCookie(Json("{}"), "intl_auth=synthetic-go-session; Domain=.bilibili.tv; Path=/; Secure"),
            3 => Json(SsoResponse()),
            4 => Json(UserResponse(true)),
            _ => throw new Exception("Unexpected request")
        });
        using var client = new IntlLoginClient(handler, false);

        var cookie = await client.CompleteAsync(new Uri("https://www.biliintl.com/after-login"));

        Assert.Equal("intl_auth=synthetic-go-session", cookie);
        Assert.Equal(5, handler.Requests.Count);
        Assert.Equal("/x/intl/passport-login/web/sso/list", handler.Requests[3].AbsolutePath);
    }

    [Theory]
    [InlineData(".evil.test")]
    [InlineData(".tv")]
    [InlineData(".biliintl.com")]
    public async Task CookieDomainErrors_AreRejectedWithoutExposingCookieValues(string domain)
    {
        using var handler = new StubHandler((_, index) => index == 0 ? Json(SsoResponse("https://api.bilibili.tv/sso"))
            : WithCookie(Json("{}"), "intl_auth=synthetic-secret; Domain=" + domain + "; Path=/; Secure"));
        using var client = new IntlLoginClient(handler, false);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => client.CompleteAsync(null));

        Assert.DoesNotContain("synthetic-secret", error.ToString());
        Assert.DoesNotContain(domain, error.ToString());
    }

    [Fact]
    public async Task SsoSuccessWithOnlyAnonymousCookies_DoesNotReplacePreviousCookie()
    {
        using var handler = new StubHandler((_, index) => index switch
        {
            0 => Json(QrResponse()),
            1 => Json(PollResponse(0)),
            2 => WithCookie(Json(SsoResponse()), "regionforbid=synthetic-anonymous; Domain=.bilibili.tv; Path=/"),
            3 => Json(UserResponse(false)),
            _ => throw new Exception("Unexpected request")
        });
        using var client = new IntlLoginClient(handler, false);
        var saved = "intl_auth=old-cookie";
        var output = new List<string>();

        var exit = await Run(client, cookie => { saved = cookie; return Task.CompletedTask; }, output);

        Assert.Equal(1, exit);
        Assert.Equal("intl_auth=old-cookie", saved);
        Assert.DoesNotContain(output, line => line.Contains("登录成功"));
        Assert.DoesNotContain(output, line => line.Contains("synthetic-anonymous"));
    }

    [Fact]
    public async Task CookiePresenceWithoutAuthenticatedSsoStatus_IsInsufficient()
    {
        using var handler = new StubHandler((_, index) => index == 0
            ? WithCookie(Json(PollResponse(0)), "intl_auth=synthetic-session; Domain=.bilibili.tv; Path=/; Secure")
            : Json("{\"code\":-101}"));
        using var client = new IntlLoginClient(handler, false);
        await client.PollAsync("synthetic-ticket");

        await Assert.ThrowsAsync<InvalidOperationException>(() => client.CompleteAsync(null));
    }

    [Theory]
    [InlineData("{\"is_login\":false}")]
    [InlineData("{\"is_login\":\"true\"}")]
    [InlineData("{}")]
    [InlineData("null")]
    public async Task ApiUserState_MustExplicitlyConfirmLoginEvenWhenSessionCookiesExist(string userData)
    {
        var response = new JsonObject { ["code"] = 0, ["data"] = JsonNode.Parse(userData) }.ToJsonString();
        using var handler = new StubHandler((_, index) => index == 0
            ? WithCookie(Json(SsoResponse()), "arbitrary_name=synthetic-session; Domain=.bilibili.tv; Path=/; Secure")
            : Json(response));
        using var client = new IntlLoginClient(handler, false);

        await Assert.ThrowsAsync<InvalidOperationException>(() => client.CompleteAsync(null));

        Assert.Equal("api.bilibili.tv", handler.Requests[1].Host);
        Assert.Equal("/intl/gateway/web/v2/user", handler.Requests[1].AbsolutePath);
    }

    [Fact]
    public async Task ApiConfirmedLogin_PreservesDeviceAndSessionCookiesInSavedHeader()
    {
        using var handler = new StubHandler((request, index) =>
        {
            if (index == 0) return WithCookie(WithCookie(Json(SsoResponse()),
                "intl_auth=synthetic-session; Domain=.bilibili.tv; Path=/; Secure"),
                "device_id=synthetic-device; Domain=.bilibili.tv; Path=/; Secure");
            var query = HttpUtility.ParseQueryString(request.RequestUri!.Query);
            Assert.Equal("en_US", query["s_locale"]);
            Assert.Equal("web", query["platform"]);
            var sentCookie = Assert.Single(request.Headers.GetValues("Cookie"));
            Assert.Contains("intl_auth=synthetic-session", sentCookie);
            Assert.Contains("device_id=synthetic-device", sentCookie);
            return Json(UserResponse(true));
        });
        using var client = new IntlLoginClient(handler, false);

        var cookie = await client.CompleteAsync(null);

        Assert.Contains("intl_auth=synthetic-session", cookie);
        Assert.Contains("device_id=synthetic-device", cookie);
    }

    [Fact]
    public async Task ApiUserSuccessWithoutApiApplicableCookie_IsStillRejected()
    {
        using var handler = new StubHandler((_, index) => Json(index == 0 ? SsoResponse() : UserResponse(true)));
        using var client = new IntlLoginClient(handler, false);

        await Assert.ThrowsAsync<InvalidOperationException>(() => client.CompleteAsync(null));
    }

    [Fact]
    public async Task ScannedMessage_IsShownOnceAndCookieIsSavedOnlyAfterCompletion()
    {
        using var handler = new StubHandler((_, index) => index switch
        {
            0 => Json(QrResponse()),
            1 => Json(PollResponse(10018102)),
            2 => Json(PollResponse(10018102)),
            3 => WithCookie(Json(PollResponse(0)), "intl_auth=synthetic-session; Domain=.bilibili.tv; Path=/; Secure"),
            4 => Json(SsoResponse()),
            5 => Json(UserResponse(true)),
            _ => throw new Exception("Unexpected request")
        });
        using var client = new IntlLoginClient(handler, false);
        var saves = new List<string>();
        var output = new List<string>();

        var exit = await Run(client, cookie => { saves.Add(cookie); return Task.CompletedTask; }, output);

        Assert.Equal(0, exit);
        Assert.Equal("intl_auth=synthetic-session", Assert.Single(saves));
        Assert.Single(output, line => line.Contains("扫码成功"));
        Assert.DoesNotContain(output, line => line.Contains("synthetic-ticket") || line.Contains("synthetic-session"));
    }

    [Fact]
    public async Task ExpiredQrCode_DoesNotSaveOrContinuePolling()
    {
        using var handler = new StubHandler((_, index) => Json(index == 0 ? QrResponse() : PollResponse(10018100)));
        using var client = new IntlLoginClient(handler, false);
        var saves = 0;

        var exit = await Run(client, _ => { saves++; return Task.CompletedTask; }, []);

        Assert.Equal(1, exit);
        Assert.Equal(0, saves);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task PollingDeadline_IsBoundedAndPreservesOldCookie()
    {
        using var handler = new StubHandler((_, index) => Json(index == 0 ? QrResponse() : PollResponse(10018101)));
        using var client = new IntlLoginClient(handler, false);
        var saves = 0;
        var delays = new List<TimeSpan>();

        var exit = await BBDownTIntlLoginUtil.RunWithDependenciesAsync(false, client,
            _ => { saves++; return Task.CompletedTask; }, () => null, _ => { }, _ => { },
            time => { delays.Add(time); return Task.CompletedTask; }, maxPolls: 3);

        Assert.Equal(1, exit);
        Assert.Equal(0, saves);
        Assert.Equal(4, handler.Requests.Count);
        Assert.Equal(3, delays.Count);
        Assert.All(delays, time => Assert.Equal(TimeSpan.FromSeconds(2), time));
    }

    [Fact]
    public async Task NetworkTimeout_IsSanitizedAndDoesNotWriteCookie()
    {
        using var handler = new StubHandler((_, _) => throw new TaskCanceledException("synthetic-secret-ticket"));
        using var client = new IntlLoginClient(handler, false);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => client.GenerateAsync());
        Assert.DoesNotContain("synthetic-secret", error.ToString());
        var saves = 0;
        var output = new List<string>();

        Assert.Equal(1, await Run(client, _ => { saves++; return Task.CompletedTask; }, output));
        Assert.Equal(0, saves);
        Assert.DoesNotContain(output, line => line.Contains("synthetic-secret"));
    }

    [Fact]
    public async Task JsonBodyRead_ReceivesDeadlineTokenAndSanitizesCancellation()
    {
        using var handler = new StubHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new CancelledBodyContent()
        });
        using var client = new IntlLoginClient(handler, false);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => client.GenerateAsync());

        Assert.Contains("超时", error.Message);
        Assert.DoesNotContain("synthetic-secret", error.ToString());
    }

    [Fact]
    public async Task ManualCookieImport_NormalizesAndSavesWithoutClaimingAuthentication()
    {
        var saved = new List<string>();
        var output = new List<string>();

        var exit = await BBDownTIntlLoginUtil.RunWithDependenciesAsync(true, null,
            cookie => { saved.Add(cookie); return Task.CompletedTask; },
            () => " Cookie: intl_auth=synthetic-secret;  language=en; ", output.Add,
            _ => throw new Exception("No QR expected"), _ => throw new Exception("No polling expected"));

        Assert.Equal(0, exit);
        Assert.Equal("intl_auth=synthetic-secret; language=en", Assert.Single(saved));
        Assert.Contains("已保存国际站Cookie，-intl将自动使用", output);
        Assert.DoesNotContain(output, line => line.Contains("登录成功") || line.Contains("synthetic-secret"));
    }

    [Theory]
    [InlineData(ConsoleKey.Escape, false)]
    [InlineData(ConsoleKey.C, true)]
    [InlineData(ConsoleKey.D, true)]
    public void HiddenCookieInput_CancelKeysReturnNoCredential(ConsoleKey key, bool control)
    {
        Assert.Null(BBDownTIntlLoginUtil.ReadCookieKeys(() => new ConsoleKeyInfo('\0', key, false, false, control)));
    }

    [Fact]
    public void HiddenCookieInput_BackspaceAndControlUHandleEditsWithoutEcho()
    {
        var keys = new Queue<ConsoleKeyInfo>([
            new('s', ConsoleKey.S, false, false, false), new('\0', ConsoleKey.U, false, false, true),
            new('a', ConsoleKey.A, false, false, false), new('b', ConsoleKey.B, false, false, false),
            new('\b', ConsoleKey.Backspace, false, false, false), new('\r', ConsoleKey.Enter, false, false, false)
        ]);

        Assert.Equal("a", BBDownTIntlLoginUtil.ReadCookieKeys(keys.Dequeue));
    }

    [Fact]
    public void HiddenCookieInput_RejectsOverlongCredentialWithoutUnboundedAccumulation()
    {
        var reads = 0;

        var cookie = BBDownTIntlLoginUtil.ReadCookieKeys(() =>
        {
            reads++;
            return new ConsoleKeyInfo('x', ConsoleKey.X, false, false, false);
        });

        Assert.Null(cookie);
        Assert.Equal(16385, reads);
    }

    [Theory]
    [InlineData(16384, true)]
    [InlineData(16385, false)]
    public void RedirectedCookieInput_HasTheSameLengthLimit(int length, bool accepted)
    {
        using var reader = new StringReader(new string('x', length) + "\n");

        var cookie = BBDownTIntlLoginUtil.ReadRedirectedCookie(reader);

        if (accepted) Assert.Equal(length, cookie!.Length);
        else Assert.Null(cookie);
    }

    [Theory]
    [InlineData("")]
    [InlineData("bad-format-synthetic-secret")]
    [InlineData("intl_auth=synthetic-secret\nInjected: value")]
    public async Task InvalidManualCookie_DoesNotOverwriteExistingInMemoryFile(string input)
    {
        var old = "intl_auth=old-cookie";
        var writes = 0;
        var output = new List<string>();

        var exit = await BBDownTIntlLoginUtil.RunWithDependenciesAsync(true, null,
            cookie => IntlCookieStore.SaveAsync("/in-memory", cookie, (_, content) =>
            {
                old = content;
                writes++;
                return Task.CompletedTask;
            }), () => input, output.Add, _ => { }, _ => Task.CompletedTask);

        Assert.Equal(1, exit);
        Assert.Equal(0, writes);
        Assert.Equal("intl_auth=old-cookie", old);
        Assert.DoesNotContain(output, line => line.Contains("synthetic-secret"));
    }

    [Fact]
    public async Task InvalidCookieStoreSave_DoesNotInvokeWriter()
    {
        var writes = 0;

        await Assert.ThrowsAsync<ArgumentException>(() => IntlCookieStore.SaveAsync("/in-memory", "invalid",
            (_, _) => { writes++; return Task.CompletedTask; }));

        Assert.Equal(0, writes);
    }

    [Fact]
    public async Task CookieStoreSave_UsesOnlyIntlFileAndNormalizedContent()
    {
        string? path = null;
        string? content = null;

        await IntlCookieStore.SaveAsync("/in-memory", "Cookie: intl_auth=synthetic; lang=en;",
            (savedPath, savedContent) => { path = savedPath; content = savedContent; return Task.CompletedTask; });

        Assert.Equal(Path.Combine("/in-memory", "BBDownTIntl.data"), path);
        Assert.Equal("intl_auth=synthetic; lang=en", content);
    }

    [Theory]
    [InlineData(true, "BBDownTIntl.data", "intl_auth=synthetic-intl")]
    [InlineData(false, "BBDownT.data", "SESSDATA=synthetic-domestic")]
    public void CredentialLoading_IsolatesInternationalAndDomesticRealms(bool international, string filename, string expected)
    {
        var files = new Dictionary<string, string>
        {
            [Path.Combine("/in-memory", "BBDownTIntl.data")] = "intl_auth=synthetic-intl",
            [Path.Combine("/in-memory", "BBDownT.data")] = "SESSDATA=synthetic-domestic"
        };
        var reads = new List<string>();

        var result = IntlCookieStore.Load("", "/in-memory", international, files.ContainsKey,
            path => { reads.Add(path); return files[path]; });

        Assert.Equal(expected, result.Cookie);
        Assert.Equal(Path.Combine("/in-memory", filename), result.FilePath);
        Assert.Equal(result.FilePath, Assert.Single(reads));
    }

    [Fact]
    public void ExplicitCookie_OverridesSavedCredentialWithoutReadingFiles()
    {
        var result = IntlCookieStore.Load("intl_auth=explicit", "/in-memory", true,
            _ => throw new Exception("No lookup expected"), _ => throw new Exception("No read expected"));

        Assert.Equal("intl_auth=explicit", result.Cookie);
        Assert.Null(result.FilePath);
    }

    [Fact]
    public void MissingIntlCookie_DoesNotFallbackToDomesticFile()
    {
        var lookups = new List<string>();
        var result = IntlCookieStore.Load("", "/in-memory", true,
            path => { lookups.Add(path); return path.EndsWith("/BBDownT.data"); },
            _ => throw new Exception("No domestic read expected"));

        Assert.Equal("", result.Cookie);
        Assert.Null(result.FilePath);
        Assert.Equal(Path.Combine("/in-memory", "BBDownTIntl.data"), Assert.Single(lookups));
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("--cookie", true)]
    [InlineData("--import-cookie", true)]
    public async Task LoginIntlCommand_RoutesModeAndExitCodeToInjectedHandler(string? option, bool expectedImport)
    {
        var calls = 0;
        var args = option is null ? new[] { "loginintl" } : new[] { "loginintl", option };

        var exit = await Program.InvokeCommandLineAsync(args,
            _ => throw new Exception("Download must not run"), () => throw new Exception("Migration must not run"),
            loginIntl: import => { Assert.Equal(expectedImport, import); calls++; return Task.FromResult(7); });

        Assert.Equal(7, exit);
        Assert.Equal(1, calls);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void LoginClient_DisposesOnlyOwnedHandler(bool ownsHandler)
    {
        using var handler = new StubHandler((_, _) => throw new Exception("No request expected"));

        using (var client = new IntlLoginClient(handler, ownsHandler)) { }

        Assert.Equal(ownsHandler, handler.Disposed);
    }

    private static Task<int> Run(IntlLoginClient client, Func<string, Task> save, List<string> output)
        => BBDownTIntlLoginUtil.RunWithDependenciesAsync(false, client, save, () => null, output.Add,
            _ => { }, _ => Task.CompletedTask, maxPolls: 5);

    private static string QrResponse(string ticket = "synthetic-ticket") => new JsonObject
    {
        ["code"] = 0,
        ["data"] = new JsonObject
        {
            ["qr_type"] = 1,
            ["qr_url"] = "https://www.biliintl.com/h5/en/qrcode/login?ticket=" + Uri.EscapeDataString(ticket)
        }
    }.ToJsonString();

    private static string PollResponse(int code, string? goUrl = null) => new JsonObject
    {
        ["code"] = code,
        ["data"] = new JsonObject { ["go_url"] = goUrl }
    }.ToJsonString();

    private static string SsoResponse(params string[] targets) => new JsonObject
    {
        ["code"] = 0,
        ["data"] = new JsonObject { ["sso"] = new JsonArray(targets.Select(target => (JsonNode?)JsonValue.Create(target)).ToArray()) }
    }.ToJsonString();

    private static string UserResponse(bool loggedIn) => new JsonObject
    {
        ["code"] = 0,
        ["data"] = new JsonObject { ["is_login"] = loggedIn }
    }.ToJsonString();

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body) };
    private static HttpResponseMessage WithCookie(HttpResponseMessage response, string cookie)
    {
        response.Headers.TryAddWithoutValidation("Set-Cookie", cookie);
        return response;
    }
    private static HttpResponseMessage Redirect(string target)
    {
        var response = new HttpResponseMessage(HttpStatusCode.Found);
        response.Headers.Location = new Uri(target, UriKind.RelativeOrAbsolute);
        return response;
    }

    private sealed class StubHandler(Func<HttpRequestMessage, int, HttpResponseMessage> respond) : HttpMessageHandler
    {
        internal List<Uri> Requests { get; } = [];
        internal bool Disposed { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!);
            return Task.FromResult(respond(request, Requests.Count - 1));
        }
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
    }

    private sealed class CancelledBodyContent : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
            => throw new NotSupportedException("A cancellable body read is required");
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken)
        {
            Assert.True(cancellationToken.CanBeCanceled);
            throw new OperationCanceledException("synthetic-secret-body");
        }
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
    }
}
