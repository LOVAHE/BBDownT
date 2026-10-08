using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Threading;
using System.Threading.Tasks;
using BBDownT.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
namespace BBDownT;

public partial class BBDownTApiServer
{
    private WebApplication? app;
    private readonly DownloadTaskStore taskStore = new();
    /// <summary>
    /// 排队中任务的请求(按TaskId)。排队顺序和任务状态以 taskStore 为准，取消的任务从这里一并移除
    /// </summary>
    private readonly ConcurrentDictionary<string, ServeRequestOptions> queuedRequests = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim pendingTaskSignal = new(0);
    private int workerStarted;
    private BBDownTServerOptions serverOptions = new();
    private string apiToken = "";
    private bool requireApiToken;
    /// <summary>
    /// 测试替换点：执行一个服务器任务；默认与命令行使用同一个下载流程
    /// </summary>
    internal Func<ServeRequestOptions, DownloadTask, Task> WorkRunner { get; set; } = (option, task) => Program.ExecuteWorkAsync(option, task);

    public BBDownTApiServer()
    {
    }

    internal BBDownTApiServer(BBDownTServerOptions options)
    {
        serverOptions = options;
    }

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
        // 放在CORS之前：未启用Token时，非本机Host的请求和跨源的网页前端接口请求(含预检)直接拒绝，不返回CORS许可头
        app.Use(async (context, next) =>
        {
            if (CheckLocalAccess(context.Request) is { } originError)
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                await context.Response.WriteAsync(originError);
                return;
            }
            await next();
        });
        app.UseCors("AllowAnyOrigin");
        app.Use(async (context, next) =>
        {
            if (!IsPublicPath(context.Request) && !HasValidApiToken(context))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                await context.Response.WriteAsync("Unauthorized");
                return;
            }
            await next();
        });
        MapWebUi(app);
        MapParseApi(app);
        MapHistoryApi(app);
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
                LogUnreadableRequest("/add-task", bindingResult.Exception);
                return Results.BadRequest("输入有误");
            }
            var req = bindingResult.Result!;
            var sentFields = ChangedFieldNames(req);
            var validationMessage = ValidateAndNormalizeServerRequest(req);
            if (validationMessage is not null)
            {
                LogRejectedRequest("/add-task", req, validationMessage, sentFields);
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
        // 取消排队中(还没开始)的任务：不下载、不写下载历史、不改动文件；已开始或已结束的任务返回409
        var pendingRemovalApi = app.MapGroup("remove-pending");
        pendingRemovalApi.MapDelete("/", () => Results.Json(new PendingRemovalResult(RemoveAllPendingTasks()), AppJsonSerializerContext.Default.PendingRemovalResult));
        pendingRemovalApi.MapDelete("/{id}", (string id) => RemovePendingTask(id) switch
        {
            PendingRemoval.Removed => Results.Ok(),
            PendingRemoval.AlreadyStarted => Results.Text("任务已开始，无法取消", statusCode: StatusCodes.Status409Conflict),
            PendingRemoval.AlreadyFinished => Results.Text("任务已结束，无法取消", statusCode: StatusCodes.Status409Conflict),
            _ => Results.Text("找不到这个排队中的任务", statusCode: StatusCodes.Status404NotFound)
        });
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
        History.EnsureLoaded();
        EnsureQueueWorkerStarted();
        app.Run(url);
    }

    private bool TryEnqueueDownloadTask(ServeRequestOptions request, DownloadTask task)
    {
        // 先记下请求再加入队列：执行队列取到任务时一定能找到它的请求
        queuedRequests[task.TaskId] = request;
        if (!taskStore.TryAddPending(task, serverOptions.MaxQueueLength))
        {
            queuedRequests.TryRemove(task.TaskId, out _);
            return false;
        }

        pendingTaskSignal.Release();
        return true;
    }

    /// <summary>
    /// 取消一个排队中的任务。与执行队列取任务互斥(DownloadTaskStore 的同一把锁)：
    /// 同一时刻开始执行的任务返回 <see cref="PendingRemoval.AlreadyStarted"/>，取消成功的任务不会再执行
    /// </summary>
    internal PendingRemoval RemovePendingTask(string id)
    {
        var result = taskStore.TryRemovePending(id, out var removed);
        if (removed is not null) queuedRequests.TryRemove(removed.TaskId, out _);
        return result;
    }

    internal int RemoveAllPendingTasks()
    {
        var removed = taskStore.RemoveAllPending();
        foreach (var task in removed) queuedRequests.TryRemove(task.TaskId, out _);
        return removed.Count;
    }

    internal void EnsureQueueWorkerStarted()
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
            // 取出任务与取消排队在同一把锁下进行：已取消的任务不会被取出；信号多于任务(有任务被取消)时这里取不到任务
            var task = taskStore.StartNext();
            if (task is null)
            {
                continue;
            }
            if (!queuedRequests.TryRemove(task.TaskId, out var request))
            {
                // 不应发生：没有请求的任务按失败结束，避免一直显示为下载中
                task.SetError("找不到任务的请求");
                taskStore.Complete(task, DateTimeOffset.Now.ToUnixTimeSeconds(), false, serverOptions.MaxFinishedTasks, serverOptions.FinishedTaskRetentionSeconds);
                continue;
            }

            var queuedTask = new QueuedDownloadTask(request, task);
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
        req.RestrictedOutputRoot = null;
        if (string.IsNullOrWhiteSpace(req.Url))
        {
            return "Url不能为空";
        }

        var streamValidation = StreamPinSelection.ValidateOptions(req);
        if (streamValidation is not null) return streamValidation;
        if (!string.IsNullOrWhiteSpace(req.SelectPage) && PageSelectionParser.ValidateSyntax(req.SelectPage) is { } pageValidation)
            return pageValidation;
        var batchValidation = SpaceBatchDownload.ValidateServerOptions(req);
        if (batchValidation is not null) return batchValidation;
        var subtitleValidation = SubtitleSelection.ValidateOptions(req);
        if (subtitleValidation is not null) return subtitleValidation;
        var coverValidation = Program.ValidateCoverOptions(req);
        if (coverValidation is not null) return coverValidation;
        var audioValidation = AudioLanguageSelection.ValidateOptions(req);
        if (audioValidation is not null) return audioValidation;
        if (req.Interactive && !req.OnlyShowInfo)
            return "服务器任务不支持交互选择，请使用语言、AI策略及音视频筛选参数。";

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
        req.RestrictedOutputRoot = req.WorkDir;
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

        if (context.Request.Headers.TryGetValue("X-BBDownT-Token", out var token)
            && IsApiTokenMatch(token.ToString().Trim()))
        {
            return true;
        }

        // 网页前端登录后使用HttpOnly Cookie携带Token；Cookie会随浏览器请求自动发送，所以只接受同源请求
        return context.Request.Cookies.TryGetValue(SessionCookieName, out var sessionToken)
            && IsApiTokenMatch(sessionToken)
            && !IsCrossOriginBrowserRequest(context.Request);
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

    internal async Task<DownloadTask> AddDownloadTaskAsync(QueuedDownloadTask queuedTask)
    {
        var option = queuedTask.Request;
        var task = queuedTask.Task;
        var succeeded = false;
        // 下载历史：重新下载用的选项在执行前记下；只看信息的任务不记录
        var historyRequest = BeginHistory(option, task);
        try
        {
            var aid = await BBDownTUtil.GetAvIdAsync(option.Url);
            task.SetAid(aid);
            await WorkRunner(option, task);
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
        // 在任务标为已完成之前写入，网页看到任务完成后刷新历史就能看到
        if (historyRequest is not null) RecordTaskHistory(task, historyRequest, succeeded);
        taskStore.Complete(
            task,
            DateTimeOffset.Now.ToUnixTimeSeconds(),
            succeeded,
            serverOptions.MaxFinishedTasks,
            serverOptions.FinishedTaskRetentionSeconds);
        return task;
    }
}

public sealed record TaskSubmissionResult(string TaskId);

public sealed record PendingRemovalResult(int Removed);

internal sealed record QueuedDownloadTask(ServeRequestOptions Request, DownloadTask Task);
