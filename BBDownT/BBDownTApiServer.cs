using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Net.Sockets;
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
    private readonly DownloadTaskStore taskStore = new();
    private readonly ConcurrentQueue<QueuedDownloadTask> pendingTasks = new();
    private readonly SemaphoreSlim pendingTaskSignal = new(0);
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
        taskStatusApi.MapGet("/", handler: () => Results.Json(taskStore.GetSnapshot(), AppJsonSerializerContext.Default.DownloadTaskCollection));
        taskStatusApi.MapGet("/running", handler: () => Results.Json(taskStore.GetRunningSnapshot(), AppJsonSerializerContext.Default.ListDownloadTask));
        taskStatusApi.MapGet("/finished", handler: () => Results.Json(taskStore.GetFinishedSnapshot(), AppJsonSerializerContext.Default.ListDownloadTask));
        taskStatusApi.MapGet("/pending", handler: () => Results.Json(taskStore.GetPendingSnapshot(), AppJsonSerializerContext.Default.ListDownloadTask));
        taskStatusApi.MapGet("/{id}", (string id) =>
        {
            var task = taskStore.FindSnapshot(id);
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
            var task = new DownloadTask("", req.Url, DateTimeOffset.Now.ToUnixTimeSeconds());
            if (!TryEnqueueDownloadTask(req, task))
            {
                return Results.Text("任务队列已满", statusCode: StatusCodes.Status429TooManyRequests);
            }
            return Results.Accepted(value: new TaskSubmissionResult(task.TaskId));
        });
        var finishedRemovalApi = app.MapGroup("remove-finished");
        finishedRemovalApi.MapDelete("/", () => { taskStore.RemoveFinished(_ => true); return Results.Ok(); });
        finishedRemovalApi.MapDelete("/failed", () => { taskStore.RemoveFinished(t => !t.IsSuccessful); return Results.Ok(); });
        finishedRemovalApi.MapDelete("/{id}", (string id) => { taskStore.RemoveFinished(t => t.MatchesId(id)); return Results.Ok(); });
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

    private bool TryEnqueueDownloadTask(ServeRequestOptions request, DownloadTask task)
    {
        if (!taskStore.TryAddPending(task, serverOptions.MaxQueueLength))
        {
            return false;
        }

        pendingTasks.Enqueue(new QueuedDownloadTask(request, task));
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
            if (!pendingTasks.TryDequeue(out var queuedTask))
            {
                continue;
            }

            taskStore.Start(queuedTask.Task);
            try
            {
                var downloadTask = await AddDownloadTaskAsync(queuedTask);
                _ = SendCallbackAsync(queuedTask.Request, downloadTask);
            }
            catch (Exception e)
            {
                Logger.LogDebug("服务器任务处理异常: {0}", e);
            }
        }
    }

    private async Task SendCallbackAsync(ServeRequestOptions req, DownloadTask downloadTask)
    {
        if (string.IsNullOrEmpty(req.CallBackWebHook))
        {
            return;
        }

        try
        {
            var callbackUri = new Uri(req.CallBackWebHook, UriKind.Absolute);
            using var dnsTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var addresses = await Dns.GetHostAddressesAsync(callbackUri.DnsSafeHost, dnsTimeout.Token);
            if (addresses.Length == 0)
            {
                Logger.LogDebug("回调已拒绝: 目标没有可用地址");
                return;
            }
            if (!serverOptions.AllowPrivateCallbacks && addresses.Any(IsPrivateOrReservedAddress))
            {
                Logger.LogDebug("回调已拒绝: 目标解析到本机、内网或保留地址");
                return;
            }

            string? jsonContent = JsonSerializer.Serialize(downloadTask.CreateSnapshot(), AppJsonSerializerContext.Default.DownloadTask);
            using var content = new StringContent(jsonContent, Encoding.UTF8, "application/json");
            using var callbackHttpClient = CreatePinnedCallbackHttpClient(addresses[0]);
            using var response = await callbackHttpClient.PostAsync(callbackUri, content);
            response.EnsureSuccessStatusCode();
        }
        catch (Exception e)
        {
            Logger.LogDebug("回调失败: {0}", e.Message);
        }
    }

    internal string? ValidateAndNormalizeServerRequest(ServeRequestOptions req)
    {
        if (string.IsNullOrWhiteSpace(req.Url))
        {
            return "Url不能为空";
        }

        if (!serverOptions.AllowAria2cArgs && !string.IsNullOrWhiteSpace(req.Aria2cArgs))
        {
            return "服务器默认不允许传入Aria2cArgs，如确需使用请启动时配置 --server-allow-aria2c-args";
        }

        if (!serverOptions.AllowCustomNetworkHosts && HasCustomNetworkHost(req))
        {
            return "服务器默认不允许任务自定义解析或下载Host，如确需使用请启动时配置 --server-allow-custom-network-hosts";
        }

        if (!string.IsNullOrWhiteSpace(req.CallBackWebHook))
        {
            if (!Uri.TryCreate(req.CallBackWebHook, UriKind.Absolute, out var callbackUri)
                || (callbackUri.Scheme != Uri.UriSchemeHttp && callbackUri.Scheme != Uri.UriSchemeHttps))
            {
                return "CallBackWebHook必须是绝对HTTP或HTTPS URL";
            }

            if (!serverOptions.AllowPrivateCallbacks
                && IPAddress.TryParse(callbackUri.DnsSafeHost, out var callbackAddress)
                && IsPrivateOrReservedAddress(callbackAddress))
            {
                return "服务器默认不允许回调本机、内网或保留地址，如确需使用请启动时配置 --server-allow-private-callbacks";
            }
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

    internal static HttpClient CreatePinnedCallbackHttpClient(IPAddress address)
    {
        return new HttpClient(CreatePinnedCallbackHandler(address), disposeHandler: true)
        {
            Timeout = TimeSpan.FromSeconds(10)
        };
    }

    internal static SocketsHttpHandler CreatePinnedCallbackHandler(IPAddress address)
    {
        return new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            ConnectCallback = async (context, cancellationToken) =>
            {
                var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
                try
                {
                    await socket.ConnectAsync(
                        new IPEndPoint(address, context.DnsEndPoint.Port),
                        cancellationToken);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            }
        };
    }

    private static bool HasCustomNetworkHost(ServeRequestOptions req)
    {
        return !IsDefaultHost(req.Host, "api.bilibili.com")
            || !IsDefaultHost(req.EpHost, "api.bilibili.com")
            || !IsDefaultHost(req.TvHost, "api.snm0516.aisee.tv")
            || !string.IsNullOrWhiteSpace(req.UposHost)
            || req.AllowPcdn;
    }

    private static bool IsDefaultHost(string? actual, string expected)
    {
        return string.Equals(actual?.Trim(), expected, StringComparison.OrdinalIgnoreCase);
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

    internal static bool IsPrivateOrReservedAddress(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (IPAddress.IsLoopback(address))
        {
            return true;
        }

        if (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
        {
            var ipv6Bytes = address.GetAddressBytes();
            return (ipv6Bytes[0] & 0xFE) == 0xFC
                || address.IsIPv6LinkLocal
                || address.IsIPv6SiteLocal
                || address.IsIPv6Multicast
                || address.Equals(IPAddress.IPv6Any)
                || address.Equals(IPAddress.IPv6None);
        }

        var bytes = address.GetAddressBytes();
        return bytes[0] == 0
            || bytes[0] == 10
            || bytes[0] == 127
            || (bytes[0] == 100 && bytes[1] is >= 64 and <= 127)
            || (bytes[0] == 169 && bytes[1] == 254)
            || (bytes[0] == 172 && bytes[1] is >= 16 and <= 31)
            || (bytes[0] == 192 && bytes[1] == 168)
            || bytes[0] >= 224;
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

    private async Task<DownloadTask> AddDownloadTaskAsync(QueuedDownloadTask queuedTask)
    {
        var option = queuedTask.Request;
        var task = queuedTask.Task;
        var succeeded = false;
        try
        {
            var aid = await BBDownTUtil.GetAvIdAsync(option.Url);
            task.SetAid(aid);
            var (encodingPriority, dfnPriority, firstEncoding, downloadDanmaku, downloadDanmakuFormats, input, savePathFormat, lang, aidOri, delay) = Program.SetUpWork(option);
            var (fetchedAid, vInfo, apiType) = await Program.GetVideoInfoAsync(option, aidOri, input);
            task.SetMetadata(vInfo.Title, vInfo.Pic, vInfo.PubTime);
            await Program.DownloadPagesAsync(option, vInfo, encodingPriority, dfnPriority, firstEncoding, downloadDanmaku, downloadDanmakuFormats,
                        input, savePathFormat, lang, fetchedAid, delay, apiType, task);
            succeeded = true;
        }
        catch (Exception e)
        {
            Console.BackgroundColor = ConsoleColor.Red;
            Console.ForegroundColor = ConsoleColor.White;
            Console.WriteLine($"{(string.IsNullOrEmpty(task.Aid) ? task.TaskId : task.Aid)}下载失败");
            var msg = Config.DEBUG_LOG ? e.ToString() : e.Message;
            task.SetError(Logger.RedactSensitiveText(e.Message));
            Console.Write($"{msg}{Environment.NewLine}请尝试升级到最新版本后重试!");
            Console.ResetColor();
            Console.WriteLine();
        }
        taskStore.Complete(
            task,
            DateTimeOffset.Now.ToUnixTimeSeconds(),
            succeeded,
            serverOptions.MaxFinishedTasks,
            serverOptions.FinishedTaskRetentionSeconds);
        return task;
    }
}

public sealed class DownloadTask
{
    private readonly object stateLock = new();

    public DownloadTask(string aid, string url, long taskCreateTime)
        : this(Guid.NewGuid().ToString("N"), aid, url, taskCreateTime)
    {
    }

    internal DownloadTask(string taskId, string aid, string url, long taskCreateTime)
    {
        TaskId = taskId;
        Aid = aid;
        Url = url;
        TaskCreateTime = taskCreateTime;
    }

    public string TaskId { get; }
    public string Aid { get; private set; }
    public string Url { get; }
    public long TaskCreateTime { get; }

    internal static double CalculateDownloadSpeed(double totalDownloadedBytes, long startedAt, long finishedAt)
    {
        var elapsedSeconds = finishedAt - startedAt;
        return elapsedSeconds <= 0 ? 0 : totalDownloadedBytes / elapsedSeconds;
    }

    internal void SetMetadata(string? title, string? pic, long? videoPubTime)
    {
        lock (stateLock)
        {
            Title = title;
            Pic = pic;
            VideoPubTime = videoPubTime;
        }
    }

    internal void SetAid(string aid)
    {
        lock (stateLock)
        {
            Aid = aid;
        }
    }

    internal bool MatchesId(string id)
    {
        lock (stateLock)
        {
            return string.Equals(TaskId, id, StringComparison.Ordinal)
                || (!string.IsNullOrEmpty(Aid) && string.Equals(Aid, id, StringComparison.Ordinal));
        }
    }

    internal void ReportProgress(double progress)
    {
        lock (stateLock)
        {
            Progress = ProgressBar.NormalizeProgress(progress);
        }
    }

    internal void ReportDownloadedBytes(double bytesPerSecond)
    {
        lock (stateLock)
        {
            DownloadSpeed = bytesPerSecond;
            TotalDownloadedBytes += bytesPerSecond;
        }
    }

    internal void AddSavePath(string path)
    {
        lock (stateLock)
        {
            SavePaths.Add(path);
        }
    }

    internal void SetError(string error)
    {
        lock (stateLock)
        {
            Error = error;
        }
    }

    internal void Finish(long finishedAt, bool succeeded)
    {
        lock (stateLock)
        {
            TaskFinishTime = finishedAt;
            IsSuccessful = succeeded;
            if (succeeded)
            {
                Progress = 1f;
                DownloadSpeed = CalculateDownloadSpeed(TotalDownloadedBytes, TaskCreateTime, finishedAt);
            }
        }
    }

    internal DownloadTask CreateSnapshot()
    {
        lock (stateLock)
        {
            return new DownloadTask(TaskId, Aid, Url, TaskCreateTime)
            {
                Title = Title,
                Pic = Pic,
                VideoPubTime = VideoPubTime,
                TaskFinishTime = TaskFinishTime,
                Progress = Progress,
                DownloadSpeed = DownloadSpeed,
                TotalDownloadedBytes = TotalDownloadedBytes,
                IsSuccessful = IsSuccessful,
                Error = Error,
                SavePaths = [.. SavePaths]
            };
        }
    }

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
    public string? Error = null;

    [JsonInclude]
    public List<string> SavePaths = new();
};
public record DownloadTaskCollection(
    List<DownloadTask> Pending,
    List<DownloadTask> Running,
    List<DownloadTask> Finished);

public sealed record TaskSubmissionResult(string TaskId);

internal sealed record QueuedDownloadTask(ServeRequestOptions Request, DownloadTask Task);

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
[JsonSerializable(typeof(TaskSubmissionResult))]
public partial class AppJsonSerializerContext : JsonSerializerContext
{

}

[JsonSerializable(typeof(MyOption))]
[JsonSerializable(typeof(ServeRequestOptions))]
internal partial class SourceGenerationContext : JsonSerializerContext
{

}
