using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using BBDownT.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace BBDownT;

/// <summary>
/// 解析预览接口：不进入下载队列，与正在执行的任务并发运行
/// </summary>
public partial class BBDownTApiServer
{
    internal TimeSpan ParseTimeout { get; set; } = TimeSpan.FromSeconds(45);
    /// <summary>
    /// 名额已满时最多排队等待的时间；连续切换接口、重新粘贴时前一次解析可能还没结束
    /// </summary>
    internal TimeSpan ParseQueueWait { get; set; } = TimeSpan.FromSeconds(5);
    internal const int MaxConcurrentParses = 2;
    private readonly SemaphoreSlim parseSlots = new(MaxConcurrentParses, MaxConcurrentParses);

    /// <summary>
    /// 测试替换点；默认使用真实的B站解析
    /// </summary>
    internal Func<ParseRequest, CancellationToken, Task<ParseResponse>> ParseRunner { get; set; } = VideoParseService.Default.ParseAsync;

    private void MapParseApi(WebApplication app)
    {
        app.MapPost("/parse", async (MyOptionBindingResult<ParseRequest> bindingResult, HttpContext context) =>
        {
            if (!bindingResult.IsValid)
            {
                LogUnreadableRequest("/parse", bindingResult.Exception);
                return Results.BadRequest("输入有误");
            }
            var req = bindingResult.Result!;
            // 规范化会补上 OnlyShowInfo、WorkDir 等字段，日志里只列客户端实际发送的字段
            var sentFields = ChangedFieldNames(req);
            var validationMessage = ValidateParseRequest(req);
            if (validationMessage is not null)
            {
                LogRejectedRequest("/parse", req, validationMessage, sentFields);
                return Results.BadRequest(validationMessage);
            }
            return await RunParseAsync(req, context.RequestAborted, sentFields);
        });
    }

    /// <summary>
    /// 解析预览只读取全局登录态，拒绝会改写进程级配置的参数
    /// </summary>
    internal string? ValidateParseRequest(ParseRequest req)
    {
        if (!string.IsNullOrWhiteSpace(req.UserAgent) || !string.IsNullOrWhiteSpace(req.Area) || HasCustomNetworkHost(req))
        {
            return "解析预览不支持自定义UserAgent、Area或解析/下载Host，请直接添加任务";
        }
        if (req.Page is < 1)
        {
            return "Page必须是从1开始的分P序号";
        }
        // 这些选项只影响下载阶段，预览时忽略
        req.DownloadAll = false;
        req.Interactive = false;
        req.OnlyShowInfo = true;
        req.CallBackWebHook = null;
        req.Debug = false;
        return ValidateAndNormalizeServerRequest(req);
    }

    internal async Task<IResult> RunParseAsync(ParseRequest req, CancellationToken aborted, List<string>? sentFields = null)
    {
        try
        {
            if (!await parseSlots.WaitAsync(ParseQueueWait, aborted))
            {
                return Results.Text("同时进行的解析过多，请稍后再试", statusCode: StatusCodes.Status429TooManyRequests);
            }
        }
        catch (OperationCanceledException) when (aborted.IsCancellationRequested)
        {
            return Results.StatusCode(499);
        }

        // 超时或客户端断开时取消解析：令牌经 HTTPUtil.FlowCancellation 传给解析流程里的每个HTTP请求，
        // 正在进行的请求立即中止，名额随之释放。名额仍在解析真正结束后才释放，保证并发上限。
        var parseCts = CancellationTokenSource.CreateLinkedTokenSource(aborted);
        parseCts.CancelAfter(ParseTimeout);
        var parseToken = parseCts.Token;
        Task<ParseResponse> work;
        var runner = ParseRunner;
        try
        {
            work = Task.Run(() => runner(req, parseToken));
        }
        catch
        {
            parseSlots.Release();
            parseCts.Dispose();
            throw;
        }
        _ = work.ContinueWith(t =>
        {
            _ = t.Exception;
            parseSlots.Release();
            parseCts.Dispose();
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

        try
        {
            var result = await work.WaitAsync(ParseTimeout, aborted);
            return Results.Json(result, AppJsonSerializerContext.Default.ParseResponse);
        }
        catch (OperationCanceledException) when (aborted.IsCancellationRequested)
        {
            return Results.StatusCode(499);
        }
        catch (Exception e) when (e is TimeoutException || (e is OperationCanceledException && parseToken.IsCancellationRequested))
        {
            return Results.Text($"解析超时（{ParseTimeout.TotalSeconds:0}秒），B站接口可能限流，请稍后重试", statusCode: StatusCodes.Status504GatewayTimeout);
        }
        catch (ParseRejectedException e)
        {
            return Results.Text(e.Message, statusCode: StatusCodes.Status422UnprocessableEntity);
        }
        catch (ArgumentException e)
        {
            var message = Logger.RedactSensitiveText(e.Message);
            LogRejectedRequest("/parse", req, message, sentFields);
            return Results.BadRequest(message);
        }
        catch (Exception e)
        {
            // 异常原文(NativeAOT下常常只剩资源键)只写进日志，界面上给出可以理解的说明
            Logger.LogWarn($"解析预览失败（{DescribeUrlForLog(req.Url)}）：{e.GetType().Name}: {Logger.RedactSensitiveText(e.Message)}");
            Logger.LogDebug("解析预览失败: {0}", e);
            return Results.Text(BadGatewayMessage(e), statusCode: StatusCodes.Status502BadGateway);
        }
    }

    /// <summary>
    /// 502 时给用户看的说明：网络错误和B站返回了无法识别的内容分开说明，不带异常原文
    /// </summary>
    internal static string BadGatewayMessage(Exception e) => e switch
    {
        System.Net.Http.HttpRequestException { StatusCode: { } code } => $"B站接口返回 HTTP {(int)code}，可能被限流，请稍后重试",
        System.Net.Http.HttpRequestException or System.Net.Sockets.SocketException => "无法连接B站接口，请检查网络后重试",
        OperationCanceledException => "B站接口长时间没有响应，请稍后重试",
        // 本程序自己抛出的说明(如“互动视频获取分P信息失败”)原样显示
        _ when e.GetType() == typeof(Exception) || e is System.IO.InvalidDataException => Logger.RedactSensitiveText(e.Message),
        _ => "B站返回了无法识别的内容（视频可能不存在、已删除或需要登录），请确认链接是否正确，或稍后重试"
    };
}
