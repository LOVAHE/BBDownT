using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using System.Threading;
using System.Threading.Tasks;
using BBDownT.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
namespace BBDownT;

public class BBDownTApiServer
{
    private WebApplication? app;
    private readonly List<DownloadTask> runningTasks = [];
    private readonly List<DownloadTask> finishedTasks = [];
    private readonly ConcurrentQueue<ServeRequestOptions> pendingTasks = new();
    private readonly SemaphoreSlim pendingTaskSignal = new(0);
    private int pendingTaskCount;
    private int workerStarted;
    private BBDownTServerOptions serverOptions = new();
    private string apiToken = "";
    private bool requireApiToken;

    public void SetUpServer(BBDownTServerOptions? options = null)
    {
        if (app is not null) return;
        serverOptions = options ?? new BBDownTServerOptions();
        var builder = WebApplication.CreateSlimBuilder();
        builder.Services.ConfigureHttpJsonOptions((options) =>
        {
            options.SerializerOptions.TypeInfoResolver = JsonTypeInfoResolver.Combine(options.SerializerOptions.TypeInfoResolver, AppJsonSerializerContext.Default);
        });
        builder.Services.AddCors((options) =>
        {
            options.AddPolicy("AllowAnyOrigin",
                policy =>
                {
                    policy.AllowAnyOrigin()
                          .AllowAnyMethod()
                          .AllowAnyHeader();
                });
        });
        app = builder.Build();
        app.UseCors("AllowAnyOrigin");
        app.Use(async (context, next) =>
        {
            if (!HasValidApiToken(context))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                await context.Response.WriteAsync("Unauthorized");
                return;
            }
            await next();
        });
        var taskStatusApi = app.MapGroup("/get-tasks");
        taskStatusApi.MapGet("/", handler: () => Results.Json(new DownloadTaskCollection(runningTasks, finishedTasks), AppJsonSerializerContext.Default.DownloadTaskCollection));
        taskStatusApi.MapGet("/running", handler: () => Results.Json(runningTasks, AppJsonSerializerContext.Default.ListDownloadTask));
        taskStatusApi.MapGet("/finished", handler: () => Results.Json(finishedTasks, AppJsonSerializerContext.Default.ListDownloadTask));
        taskStatusApi.MapGet("/{id}", (string id) =>
        {
            var task = finishedTasks.FirstOrDefault(a => a.Aid == id);
            var rtask = runningTasks.FirstOrDefault(a => a.Aid == id);
            if (rtask is not null) task = rtask;
            if (task is null)
            {
                return Results.NotFound();
            }
            return Results.Json(task, AppJsonSerializerContext.Default.DownloadTask);
        });
        app.MapPost("/add-task", (MyOptionBindingResult<ServeRequestOptions> bindingResult) =>
        {
            if (!bindingResult.IsValid)
            {
                //var exception = bindingResult.Exception;
                return Results.BadRequest("输入有误");
            }
            var req = bindingResult.Result!;
            var validationMessage = ValidateAndNormalizeServerRequest(req);
            if (validationMessage is not null)
            {
                return Results.BadRequest(validationMessage);
            }
            if (!TryEnqueueDownloadTask(req))
            {
                return Results.Text("任务队列已满", statusCode: StatusCodes.Status429TooManyRequests);
            }
            return Results.Accepted();
        });
        var finishedRemovalApi = app.MapGroup("remove-finished");
        finishedRemovalApi.MapGet("/", () => { finishedTasks.RemoveAll(t => true); return Results.Ok(); });
        finishedRemovalApi.MapGet("/failed", () => { finishedTasks.RemoveAll(t => !t.IsSuccessful); return Results.Ok(); });
        finishedRemovalApi.MapGet("/{id}", (string id) => { finishedTasks.RemoveAll(t => t.Aid == id); return Results.Ok(); });
    }

    public void Run(string url, string? configuredApiToken)
    {
        if (app is null) return;
        bool result = Uri.TryCreate(url, UriKind.Absolute, out Uri? uriResult)
            && uriResult.Scheme == Uri.UriSchemeHttp;
        if (!result)
        {
            Console.BackgroundColor = ConsoleColor.Red;
            Console.ForegroundColor = ConsoleColor.White;
            Console.WriteLine($"{url}不是合法的http URL，url示例：http://0.0.0.0:5000");
            Console.WriteLine("如果您需要https，请额外配置反向代理");
            Console.ResetColor();
            Console.WriteLine();
            Thread.Sleep(1);
            Environment.Exit(1);
        }
        ConfigureApiToken(uriResult!, configuredApiToken);
        EnsureQueueWorkerStarted();
        app.Run(url);
    }

    private bool TryEnqueueDownloadTask(ServeRequestOptions request)
    {
        var count = Interlocked.Increment(ref pendingTaskCount);
        if (count > serverOptions.MaxQueueLength)
        {
            Interlocked.Decrement(ref pendingTaskCount);
            return false;
        }

        pendingTasks.Enqueue(request);
        pendingTaskSignal.Release();
        return true;
    }

    private void EnsureQueueWorkerStarted()
    {
        if (Interlocked.Exchange(ref workerStarted, 1) == 1)
        {
            return;
        }

        _ = Task.Run(ProcessPendingTasksAsync);
    }

    private async Task ProcessPendingTasksAsync()
    {
        while (true)
        {
            await pendingTaskSignal.WaitAsync();
            if (!pendingTasks.TryDequeue(out var req))
            {
                continue;
            }

            try
            {
                var downloadTask = await AddDownloadTaskAsync(req);
                await SendCallbackAsync(req, downloadTask);
            }
            catch (Exception e)
            {
                Logger.LogDebug("服务器任务处理异常: {0}", e);
            }
            finally
            {
                Interlocked.Decrement(ref pendingTaskCount);
            }
        }
    }

    private static async Task SendCallbackAsync(ServeRequestOptions req, DownloadTask downloadTask)
    {
        if (string.IsNullOrEmpty(req.CallBackWebHook))
        {
            return;
        }

        string callback = req.CallBackWebHook;
        var client = new HttpClient();
        string? jsonContent = JsonSerializer.Serialize(downloadTask, AppJsonSerializerContext.Default.DownloadTask);
        try
        {
            await client.PostAsync(callback, new StringContent(jsonContent, Encoding.UTF8, "application/json"));
        }
        catch (Exception e)
        {
            Logger.LogDebug("回调失败: {0}", e.Message);
        }
    }

    private string? ValidateAndNormalizeServerRequest(ServeRequestOptions req)
    {
        if (!serverOptions.AllowAria2cArgs && !string.IsNullOrWhiteSpace(req.Aria2cArgs))
        {
            return "服务器默认不允许传入Aria2cArgs，如确需使用请启动时配置 --server-allow-aria2c-args";
        }

        if (serverOptions.AllowCustomOutput)
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(req.WorkDir))
        {
            return "服务器默认不允许任务自定义WorkDir，如确需使用请启动时配置 --server-allow-custom-output";
        }

        if (HasUnsafeOutputPattern(req.FilePattern) || HasUnsafeOutputPattern(req.MultiFilePattern))
        {
            return "服务器默认只允许相对输出路径，且不能包含上级目录";
        }

        req.WorkDir = Path.GetFullPath(serverOptions.DownloadRoot);
        return null;
    }

    private static bool HasUnsafeOutputPattern(string? pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern))
        {
            return false;
        }

        if (Path.IsPathRooted(pattern))
        {
            return true;
        }

        return pattern
            .Replace('\\', '/')
            .Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Any(part => part == "..");
    }

    private void ConfigureApiToken(Uri listenUri, string? configuredApiToken)
    {
        var token = configuredApiToken?.Trim() ?? "";
        var generatedToken = false;
        if (string.IsNullOrEmpty(token) && !IsLoopbackListenHost(listenUri.Host))
        {
            token = GenerateApiToken();
            generatedToken = true;
        }

        apiToken = token;
        requireApiToken = !string.IsNullOrEmpty(apiToken);

        if (!requireApiToken)
        {
            Console.WriteLine("API鉴权未启用：当前仅监听本机地址。");
            return;
        }

        Console.WriteLine("API鉴权已启用，请在请求头中携带 Authorization: Bearer <token> 或 X-BBDownT-Token: <token>。");
        if (generatedToken)
        {
            Console.WriteLine($"本次自动生成的API Token: {apiToken}");
        }
    }

    private static bool IsLoopbackListenHost(string host)
    {
        if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return IPAddress.TryParse(host, out var address) && IPAddress.IsLoopback(address);
    }

    private static string GenerateApiToken()
    {
        return Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
    }

    private bool HasValidApiToken(HttpContext context)
    {
        if (!requireApiToken)
        {
            return true;
        }

        if (context.Request.Headers.TryGetValue("Authorization", out var authorization))
        {
            const string bearerPrefix = "Bearer ";
            var authorizationValue = authorization.ToString();
            if (authorizationValue.StartsWith(bearerPrefix, StringComparison.OrdinalIgnoreCase)
                && IsApiTokenMatch(authorizationValue[bearerPrefix.Length..].Trim()))
            {
                return true;
            }
        }

        return context.Request.Headers.TryGetValue("X-BBDownT-Token", out var token)
            && IsApiTokenMatch(token.ToString().Trim());
    }

    private bool IsApiTokenMatch(string candidate)
    {
        if (string.IsNullOrEmpty(candidate) || candidate.Length != apiToken.Length)
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(candidate),
            Encoding.UTF8.GetBytes(apiToken));
    }

    private async Task<DownloadTask> AddDownloadTaskAsync(MyOption option)
    {
        var aid = await BBDownTUtil.GetAvIdAsync(option.Url);
        DownloadTask? runningTask = runningTasks.FirstOrDefault(task => task.Aid == aid);
        if (runningTask is not null)
        {
            return runningTask;
        };
        var task = new DownloadTask(aid, option.Url, DateTimeOffset.Now.ToUnixTimeSeconds());
        runningTasks.Add(task);
        try
        {
            var (encodingPriority, dfnPriority, firstEncoding, downloadDanmaku, downloadDanmakuFormats, input, savePathFormat, lang, aidOri, delay) = Program.SetUpWork(option);
            var (fetchedAid, vInfo, apiType) = await Program.GetVideoInfoAsync(option, aidOri, input);
            task.Title = vInfo.Title;
            task.Pic = vInfo.Pic;
            task.VideoPubTime = vInfo.PubTime;
            await Program.DownloadPagesAsync(option, vInfo, encodingPriority, dfnPriority, firstEncoding, downloadDanmaku, downloadDanmakuFormats,
                        input, savePathFormat, lang, fetchedAid, delay, apiType, task);
            task.IsSuccessful = true;
        }
        catch (Exception e)
        {
            Console.BackgroundColor = ConsoleColor.Red;
            Console.ForegroundColor = ConsoleColor.White;
            Console.WriteLine($"{aid}下载失败");
            var msg = Config.DEBUG_LOG ? e.ToString() : e.Message;
            Console.Write($"{msg}{Environment.NewLine}请尝试升级到最新版本后重试!");
            Console.ResetColor();
            Console.WriteLine();
        }
        task.TaskFinishTime = DateTimeOffset.Now.ToUnixTimeSeconds();
        if (task.IsSuccessful)
        {
            task.Progress = 1f;
            task.DownloadSpeed = (double)(task.TotalDownloadedBytes / (task.TaskFinishTime - task.TaskCreateTime));
        }
        runningTasks.Remove(task);
        finishedTasks.Add(task);
        return task;
    }
}

public record DownloadTask(string Aid, string Url, long TaskCreateTime)
{
    [JsonInclude]
    public string? Title = null;
    [JsonInclude]
    public string? Pic = null;
    [JsonInclude]
    public long? VideoPubTime = null;
    [JsonInclude]
    public long? TaskFinishTime = null;
    [JsonInclude]
    public double Progress = 0f;
    [JsonInclude]
    public double DownloadSpeed = 0f;
    [JsonInclude]
    public double TotalDownloadedBytes = 0f;
    [JsonInclude]
    public bool IsSuccessful = false;

    [JsonInclude]
    public List<string> SavePaths = new();
};
public record DownloadTaskCollection(List<DownloadTask> Running, List<DownloadTask> Finished);

record struct MyOptionBindingResult<T>(T? Result, Exception? Exception)
{
    public bool IsValid => Exception is null;

    public static async ValueTask<MyOptionBindingResult<T>> BindAsync(HttpContext httpContext)
    {
        try
        {
            JsonTypeInfo? jsonTypeInfo = SourceGenerationContext.Default.GetTypeInfo(typeof(T));
            if (jsonTypeInfo is null)
            {
                return new(default, new InvalidOperationException($"Cannot find TypeInfo for type {typeof(T)}"));
            }
            var item = await httpContext.Request.ReadFromJsonAsync(jsonTypeInfo);

            if (item is null) return new(default, new NoNullAllowedException());

            return new((T)item, null);
        }
        catch (Exception ex)
        {
            return new(default, ex);
        }
    }
}

[JsonSerializable(typeof(ProblemDetails))]
[JsonSerializable(typeof(ValidationProblemDetails))]
[JsonSerializable(typeof(HttpValidationProblemDetails))]
[JsonSerializable(typeof(DownloadTask))]
[JsonSerializable(typeof(List<DownloadTask>))]
[JsonSerializable(typeof(DownloadTaskCollection))]
public partial class AppJsonSerializerContext : JsonSerializerContext
{

}

[JsonSerializable(typeof(MyOption))]
[JsonSerializable(typeof(ServeRequestOptions))]
internal partial class SourceGenerationContext : JsonSerializerContext
{

}
