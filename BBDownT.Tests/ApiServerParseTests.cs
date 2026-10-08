using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace BBDownT.Tests;

public class ApiServerParseTests
{
    private static BBDownTApiServer CreateServer() => new(new BBDownTServerOptions { DownloadRoot = Path.GetTempPath() });

    [Theory]
    [InlineData("{\"UserAgent\":\"x\"}")]
    [InlineData("{\"Area\":\"hk\"}")]
    [InlineData("{\"Host\":\"example.test\"}")]
    [InlineData("{\"UposHost\":\"example.test\"}")]
    [InlineData("{\"VideoStream\":\"4K\"}")]
    [InlineData("{\"Page\":0}")]
    [InlineData("{\"SelectPage\":\"1-\"}")]
    [InlineData("{\"SelectPage\":\"0\"}")]
    public void ValidateParseRequest_RejectsOptionsThatWouldChangeGlobalState(string extra)
    {
        var req = System.Text.Json.JsonSerializer.Deserialize(extra, SourceGenerationContext.Default.ParseRequest)!;
        req.Url = "BV1xx";

        Assert.NotNull(CreateServer().ValidateParseRequest(req));
    }

    [Fact]
    public void ValidateParseRequest_IgnoresDownloadOnlyOptions()
    {
        var req = new ParseRequest { Url = "BV1xx", DownloadAll = true, Interactive = true, CallBackWebHook = "https://example.test/cb" };

        Assert.Null(CreateServer().ValidateParseRequest(req));
        Assert.False(req.DownloadAll);
        Assert.False(req.Interactive);
        Assert.True(req.OnlyShowInfo);
        Assert.Null(req.CallBackWebHook);
    }

    [Fact]
    public async Task RunParse_TimesOutWithoutBlockingAndKeepsConcurrencyLimit()
    {
        var server = CreateServer();
        var gate = new TaskCompletionSource<ParseResponse>();
        server.ParseRunner = (_, _) => gate.Task;
        server.ParseTimeout = TimeSpan.FromMilliseconds(50);
        server.ParseQueueWait = TimeSpan.FromMilliseconds(50);

        var first = await server.RunParseAsync(new ParseRequest { Url = "BV1" }, CancellationToken.None);
        var second = await server.RunParseAsync(new ParseRequest { Url = "BV2" }, CancellationToken.None);
        var third = await server.RunParseAsync(new ParseRequest { Url = "BV3" }, CancellationToken.None);

        Assert.Equal(StatusCodes.Status504GatewayTimeout, await StatusOf(first));
        Assert.Equal(StatusCodes.Status504GatewayTimeout, await StatusOf(second));
        // 两个超时的解析仍在后台运行，名额未释放
        Assert.Equal(StatusCodes.Status429TooManyRequests, await StatusOf(third));

        gate.SetException(new InvalidOperationException("late"));
        await Task.Delay(50);
        server.ParseRunner = (_, _) => throw new ParseRejectedException("space");
        Assert.Equal(StatusCodes.Status422UnprocessableEntity,
            await StatusOf(await server.RunParseAsync(new ParseRequest { Url = "BV4" }, CancellationToken.None)));
    }

    [Fact]
    public async Task RunParse_WaitsBrieflyForASlotInsteadOfFailingImmediately()
    {
        var server = CreateServer();
        server.ParseQueueWait = TimeSpan.FromSeconds(5);
        var slow = new TaskCompletionSource<ParseResponse>();
        server.ParseRunner = (_, _) => slow.Task;
        var first = server.RunParseAsync(new ParseRequest { Url = "BV1" }, CancellationToken.None);
        var second = server.RunParseAsync(new ParseRequest { Url = "BV2" }, CancellationToken.None);

        // 两个名额都被占用；第三个请求排队，前面的解析很快结束后它就能拿到名额
        server.ParseRunner = (_, _) => Task.FromException<ParseResponse>(new ParseRejectedException("queued and ran"));
        var third = server.RunParseAsync(new ParseRequest { Url = "BV3" }, CancellationToken.None);
        await Task.Delay(100);
        Assert.False(third.IsCompleted);
        slow.SetException(new InvalidOperationException("first two finished"));

        Assert.Equal(StatusCodes.Status422UnprocessableEntity, await StatusOf(await third.WaitAsync(TimeSpan.FromSeconds(5))));
        Assert.Equal(StatusCodes.Status502BadGateway, await StatusOf(await first));
        Assert.Equal(StatusCodes.Status502BadGateway, await StatusOf(await second));
    }

    [Fact]
    public async Task RunParse_StopsWaitingForASlotWhenTheClientGoesAway()
    {
        var server = CreateServer();
        server.ParseQueueWait = TimeSpan.FromSeconds(30);
        var gate = new TaskCompletionSource<ParseResponse>();
        server.ParseRunner = (_, _) => gate.Task;
        server.ParseTimeout = TimeSpan.FromSeconds(30);
        _ = server.RunParseAsync(new ParseRequest { Url = "BV1" }, CancellationToken.None);
        _ = server.RunParseAsync(new ParseRequest { Url = "BV2" }, CancellationToken.None);
        using var aborted = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        var result = await server.RunParseAsync(new ParseRequest { Url = "BV3" }, aborted.Token).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(499, await StatusOf(result));
        gate.SetCanceled();
    }

    [Fact]
    public async Task RunParse_CancelsTheParseOnTimeoutSoTheSlotIsFreedRightAway()
    {
        var server = CreateServer();
        server.ParseTimeout = TimeSpan.FromMilliseconds(100);
        server.ParseQueueWait = TimeSpan.FromSeconds(2);
        var cancelled = 0;
        server.ParseRunner = async (_, ct) =>
        {
            try { await Task.Delay(Timeout.Infinite, ct); }
            catch (OperationCanceledException) { Interlocked.Increment(ref cancelled); throw; }
            return null!;
        };

        var first = server.RunParseAsync(new ParseRequest { Url = "BV1" }, CancellationToken.None);
        var second = server.RunParseAsync(new ParseRequest { Url = "BV2" }, CancellationToken.None);
        Assert.Equal(StatusCodes.Status504GatewayTimeout, await StatusOf(await first));
        Assert.Equal(StatusCodes.Status504GatewayTimeout, await StatusOf(await second));

        // 超时后解析被取消、名额立即释放，下一个请求不会因为名额被占满而得到429
        server.ParseRunner = (_, _) => Task.FromException<ParseResponse>(new ParseRejectedException("got a slot"));
        var watch = System.Diagnostics.Stopwatch.StartNew();
        Assert.Equal(StatusCodes.Status422UnprocessableEntity,
            await StatusOf(await server.RunParseAsync(new ParseRequest { Url = "BV3" }, CancellationToken.None)));
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(1), watch.Elapsed.ToString());
        Assert.Equal(2, Volatile.Read(ref cancelled));
    }

    [Fact]
    public async Task RunParse_CancelsTheParseWhenTheClientGoesAway()
    {
        var server = CreateServer();
        server.ParseTimeout = TimeSpan.FromSeconds(30);
        var started = new TaskCompletionSource();
        var cancelled = new TaskCompletionSource();
        server.ParseRunner = async (_, ct) =>
        {
            started.SetResult();
            try { await Task.Delay(Timeout.Infinite, ct); }
            catch (OperationCanceledException) { cancelled.SetResult(); throw; }
            return null!;
        };
        using var aborted = new CancellationTokenSource();

        var request = server.RunParseAsync(new ParseRequest { Url = "BV1" }, aborted.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        aborted.Cancel();

        Assert.Equal(499, await StatusOf(await request.WaitAsync(TimeSpan.FromSeconds(5))));
        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task RunParse_PassesTheTokenThatTheHttpHelpersUse()
    {
        var server = CreateServer();
        CancellationToken seen = default;
        server.ParseRunner = (_, ct) => { seen = ct; return Task.FromException<ParseResponse>(new ParseRejectedException("x")); };

        await server.RunParseAsync(new ParseRequest { Url = "BV1" }, CancellationToken.None);

        Assert.True(seen.CanBeCanceled);
    }

    [Theory]
    [InlineData(typeof(System.Text.Json.JsonException), "B站返回了无法识别的内容")]
    [InlineData(typeof(KeyNotFoundException), "B站返回了无法识别的内容")]
    [InlineData(typeof(HttpRequestException), "无法连接B站接口")]
    public async Task RunParse_BadGatewayShowsAnExplanationInsteadOfTheRawException(Type error, string expected)
    {
        var server = CreateServer();
        server.ParseRunner = (_, _) => Task.FromException<ParseResponse>((Exception)Activator.CreateInstance(error, "ExpectedJsonTokens LineNumber: 0")!);

        var (code, body) = await Execute(await server.RunParseAsync(new ParseRequest { Url = "BV1" }, CancellationToken.None));

        Assert.Equal(StatusCodes.Status502BadGateway, code);
        Assert.Contains(expected, body);
        Assert.DoesNotContain("ExpectedJsonTokens", body);
        Assert.DoesNotContain("解析失败", body);
    }

    [Fact]
    public void BadGatewayMessage_KeepsOwnChineseMessagesAndExplainsHttpStatus()
    {
        Assert.Equal("互动视频获取分P信息失败", BBDownTApiServer.BadGatewayMessage(new Exception("互动视频获取分P信息失败")));
        Assert.Contains("HTTP 412", BBDownTApiServer.BadGatewayMessage(new HttpRequestException("x", null, System.Net.HttpStatusCode.PreconditionFailed)));
    }

    [Fact]
    public void RejectedParseLog_ListsOnlyTheFieldsTheClientSent()
    {
        var server = CreateServer();
        var req = new ParseRequest { Url = "BV1xx", SelectPage = "abc" };
        var sent = BBDownTApiServer.ChangedFieldNames(req);

        Assert.NotNull(server.ValidateParseRequest(req));
        // 规范化已经改写了请求(OnlyShowInfo=true)，但日志只列客户端发送的字段
        Assert.True(req.OnlyShowInfo);
        var log = BBDownTApiServer.DescribeRequestForLog(req, sent);
        Assert.Contains("请求字段：Url, SelectPage；", log);
        Assert.DoesNotContain("OnlyShowInfo", log);
        Assert.DoesNotContain("WorkDir", log);
    }

    [Theory]
    [InlineData(typeof(ArgumentException), StatusCodes.Status400BadRequest)]
    [InlineData(typeof(InvalidOperationException), StatusCodes.Status502BadGateway)]
    public async Task RunParse_MapsFailuresToStatusCodes(Type error, int status)
    {
        var server = CreateServer();
        server.ParseRunner = (_, _) => Task.FromException<ParseResponse>((Exception)Activator.CreateInstance(error, "SESSDATA=secret failed")!);

        var result = await server.RunParseAsync(new ParseRequest { Url = "BV1" }, CancellationToken.None);
        var (code, body) = await Execute(result);

        Assert.Equal(status, code);
        Assert.DoesNotContain("secret", body);
    }

    private static async Task<int> StatusOf(IResult result) => (await Execute(result)).Status;

    private static async Task<(int Status, string Body)> Execute(IResult result)
    {
        var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        var context = new DefaultHttpContext { RequestServices = services };
        using var body = new MemoryStream();
        context.Response.Body = body;
        await result.ExecuteAsync(context);
        return (context.Response.StatusCode, System.Text.Encoding.UTF8.GetString(body.ToArray()));
    }
}
