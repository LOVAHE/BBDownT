using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using BBDownT.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.StaticFiles;
using QRCoder;
using static BBDownT.Core.Logger;

namespace BBDownT;

/// <summary>
/// 服务器模式的网页前端，以及前端用到的会话、文件和B站扫码登录接口
/// </summary>
public partial class BBDownTApiServer
{
    internal const string SessionCookieName = "bbdownt_token";
    private const int MaxListedFiles = 5000;
    private static readonly FileExtensionContentTypeProvider ContentTypeProvider = new();
    private static readonly Lazy<byte[]> IndexHtml = new(LoadIndexHtml);

    private static byte[] LoadIndexHtml()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("BBDownT.WebUi.index.html")
            ?? throw new InvalidOperationException("缺少内置网页资源");
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return memory.ToArray();
    }

    /// <summary>
    /// 无需API Token即可访问的路径：网页本身和用于提交Token的会话接口
    /// </summary>
    internal static bool IsPublicPath(HttpRequest request)
    {
        var path = request.Path.Value ?? "";
        if (HttpMethods.IsGet(request.Method) && path is "/" or "/index.html") return true;
        return path == "/ui/session" && (HttpMethods.IsPost(request.Method) || HttpMethods.IsDelete(request.Method));
    }

    private void MapWebUi(WebApplication app)
    {
        app.MapGet("/", ServeIndex);
        app.MapGet("/index.html", ServeIndex);

        app.MapPost("/ui/session", async (HttpContext context) =>
        {
            if (!requireApiToken) return Results.Ok();
            UiSessionRequest? request = null;
            try
            {
                request = await context.Request.ReadFromJsonAsync(AppJsonSerializerContext.Default.UiSessionRequest);
            }
            catch { }
            if (string.IsNullOrWhiteSpace(request?.Token) || !IsApiTokenMatch(request.Token.Trim()))
            {
                await Task.Delay(1000);
                return Results.Text("Token错误", statusCode: StatusCodes.Status401Unauthorized);
            }
            context.Response.Cookies.Append(SessionCookieName, apiToken, CreateSessionCookieOptions(context, TimeSpan.FromDays(30)));
            return Results.Ok();
        });
        app.MapDelete("/ui/session", (HttpContext context) =>
        {
            context.Response.Cookies.Delete(SessionCookieName, CreateSessionCookieOptions(context, null));
            return Results.Ok();
        });

        app.MapGet("/ui/status", async () =>
        {
            var ver = Assembly.GetExecutingAssembly().GetName().Version!;
            // 与 /parse 使用同一个来源(数据目录里保存的网页登录)，不受正在运行的任务自带的Cookie影响
            var cookieFile = Path.Combine(Program.APP_DIR, "BBDownT.data");
            var cookie = File.Exists(cookieFile) ? File.ReadAllText(cookieFile).Trim() : "";
            var cookieSaved = cookie.Length > 0;
            var account = WebAccount.Anonymous;
            if (cookieSaved)
            {
                try { account = await FetchWebAccountAsync(cookie); }
                catch (Exception e) { LogDebug("获取登录状态失败: {0}", e.Message); }
            }
            return Results.Json(
                new UiStatus($"{ver.Major}.{ver.Minor}.{ver.Build}", requireApiToken, cookieSaved, account.UserName,
                    account.IsVip, account.VipLabel,
                    File.Exists(Path.Combine(Program.APP_DIR, "BBDownTTV.data")),
                    File.Exists(Path.Combine(Program.APP_DIR, "BBDownTApp.data"))),
                AppJsonSerializerContext.Default.UiStatus);
        });

        app.MapPost("/ui/bili-login", async () =>
        {
            try
            {
                // 国内站登录：不受上一个国际站任务留下的全局 COOKIE_IS_INTL、HOST 等影响
                WebLoginQrCode qrCode;
                using (Config.UseCredentials("", "")) qrCode = await BBDownTLoginUtil.CreateWebLoginQrCodeAsync();
                using var qrCodeData = new QRCodeGenerator().CreateQrCode(qrCode.Url, QRCodeGenerator.ECCLevel.Q);
                var png = new PngByteQRCode(qrCodeData).GetGraphic(8);
                return Results.Json(
                    new BiliLoginStart(qrCode.QrcodeKey, "data:image/png;base64," + Convert.ToBase64String(png)),
                    AppJsonSerializerContext.Default.BiliLoginStart);
            }
            catch (Exception e)
            {
                return Results.Text($"获取登录二维码失败: {Logger.RedactSensitiveText(e.Message)}", statusCode: StatusCodes.Status502BadGateway);
            }
        });
        app.MapGet("/ui/bili-login/{key}", async (string key) =>
        {
            if (!QrcodeKeyRegex().IsMatch(key)) return Results.BadRequest("无效的二维码Key");
            try
            {
                var (state, cookie) = await BBDownTLoginUtil.PollWebLoginAsync(key);
                string? userName = null;
                if (cookie is not null)
                {
                    try { userName = (await FetchWebAccountAsync(cookie)).UserName; }
                    catch (Exception e) { LogDebug("获取登录状态失败: {0}", e.Message); }
                }
                return Results.Json(
                    new BiliLoginPoll(state.ToString().ToLowerInvariant(), userName),
                    AppJsonSerializerContext.Default.BiliLoginPoll);
            }
            catch (Exception e)
            {
                return Results.Text($"查询登录状态失败: {Logger.RedactSensitiveText(e.Message)}", statusCode: StatusCodes.Status502BadGateway);
            }
        });
        app.MapDelete("/ui/bili-login", () =>
        {
            File.Delete(Path.Combine(Program.APP_DIR, "BBDownT.data"));
            return Results.Ok();
        });

        var filesApi = app.MapGroup("/files");
        filesApi.MapGet("/", () => Results.Json(ListDownloadedFiles(), AppJsonSerializerContext.Default.ListDownloadedFile));
        MapFileGroupsApi(filesApi);
        filesApi.MapGet("/download", (HttpContext context) =>
        {
            var query = context.Request.Query;
            string? fullPath = query.ContainsKey("task")
                ? ResolveTaskFile(query["task"].ToString(), query["index"].ToString())
                : ResolveDownloadPath(query["path"].ToString());
            if (fullPath is null || !File.Exists(fullPath)) return Results.NotFound();
            if (!ContentTypeProvider.TryGetContentType(fullPath, out var contentType))
            {
                contentType = "application/octet-stream";
            }
            var inline = query["inline"].ToString() == "1";
            return Results.File(fullPath, contentType, inline ? null : Path.GetFileName(fullPath), enableRangeProcessing: true);
        });
        filesApi.MapDelete("/", (HttpContext context) =>
        {
            var fullPath = ResolveDownloadPath(context.Request.Query["path"].ToString());
            if (fullPath is null || !File.Exists(fullPath)) return Results.NotFound();
            // 正在下载的临时文件夹里的文件(合并好的轨道、单线程下载的 .tmp、封面、字幕)删掉会让混流或任务失败
            if (IsInActiveDownload(fullPath)) return Results.Text("这个文件所在的下载正在进行，下载结束后才能删除", statusCode: StatusCodes.Status409Conflict);
            File.Delete(fullPath);
            RemoveEmptyParentDirectories(fullPath);
            return Results.Ok();
        });
    }

    private static IResult ServeIndex(HttpContext context)
    {
        var headers = context.Response.Headers;
        headers.CacheControl = "no-cache";
        headers["X-Frame-Options"] = "DENY";
        headers["X-Content-Type-Options"] = "nosniff";
        headers["Referrer-Policy"] = "no-referrer";
        headers.ContentSecurityPolicy = "default-src 'self'; script-src 'self' 'unsafe-inline'; style-src 'self' 'unsafe-inline'; "
            + "img-src 'self' data: https://*.hdslb.com; media-src 'self'; connect-src 'self'; frame-ancestors 'none'";
        return Results.Bytes(IndexHtml.Value, "text/html; charset=utf-8");
    }

    private static CookieOptions CreateSessionCookieOptions(HttpContext context, TimeSpan? maxAge) => new()
    {
        HttpOnly = true,
        SameSite = SameSiteMode.Strict,
        Secure = context.Request.IsHttps,
        Path = "/",
        MaxAge = maxAge
    };

    /// <summary>
    /// 用保存的网页登录Cookie查询账号(国内站)；只作用于本次请求，不改写正在运行的任务使用的全局Cookie
    /// </summary>
    private static async Task<WebAccount> FetchWebAccountAsync(string cookie)
    {
        using var _ = Config.UseCredentials(cookie, "");
        AuthenticatedWebProfileStore.Configure(Program.APP_DIR);
        return await WebAccount.FetchAsync();
    }

    private string DownloadRootFullPath => Path.GetFullPath(serverOptions.DownloadRoot);

    internal List<DownloadedFile> ListDownloadedFiles()
    {
        var root = DownloadRootFullPath;
        if (!Directory.Exists(root)) return [];
        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true };
        var files = new DirectoryInfo(root).EnumerateFiles("*", options)
            .Where(file => !IsProtectedFile(file.FullName))
            .OrderByDescending(file => file.LastWriteTimeUtc)
            .Take(MaxListedFiles)
            .Select(file => (Info: file, Path: Path.GetRelativePath(root, file.FullName).Replace('\\', '/')))
            .ToList();
        // 属于某次下载的文件显示视频标题(来自下载历史，最新的记录优先)
        var titles = files.Count == 0 ? [] : HistoryTitlesByFile();
        return files
            .Select(file => new DownloadedFile(
                file.Path,
                file.Info.Length,
                new DateTimeOffset(file.Info.LastWriteTimeUtc).ToUnixTimeSeconds(),
                titles.GetValueOrDefault(file.Path)))
            .ToList();
    }

    /// <summary>
    /// 将相对下载根目录的路径解析为绝对路径；越出下载根目录或指向配置/登录文件时返回null
    /// </summary>
    internal string? ResolveDownloadPath(string? relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath)) return null;
        var root = DownloadRootFullPath;
        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(relativePath, root);
        }
        catch (Exception)
        {
            return null;
        }
        var rootWithSeparator = Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar;
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!fullPath.StartsWith(rootWithSeparator, comparison)) return null;
        return IsProtectedFile(fullPath) ? null : fullPath;
    }

    private string? ResolveTaskFile(string taskId, string indexText)
    {
        var task = taskStore.FindSnapshot(taskId);
        if (task is null || !int.TryParse(indexText, out var index) || index < 0 || index >= task.SavePaths.Count)
        {
            return null;
        }
        // 任务输出路径以下载根目录作为工作目录写入，可能是相对路径
        return ResolveDownloadPath(Path.GetFullPath(task.SavePaths[index], DownloadRootFullPath));
    }

    private void RemoveEmptyParentDirectories(string fullPath)
    {
        var root = DownloadRootFullPath.TrimEnd(Path.DirectorySeparatorChar);
        var dir = Path.GetDirectoryName(fullPath);
        while (!string.IsNullOrEmpty(dir) && dir.Length > root.Length && Directory.Exists(dir)
               && !Directory.EnumerateFileSystemEntries(dir).Any())
        {
            Directory.Delete(dir);
            dir = Path.GetDirectoryName(dir);
        }
    }

    /// <summary>
    /// 下载根目录与程序目录相同时，避免通过文件接口读取或删除登录、配置和归档文件；
    /// 另外任何位置的未完成下载说明文件(.bbdownt-task.json)也受保护
    /// </summary>
    internal static bool IsProtectedFile(string fullPath)
    {
        // 未完成下载的说明文件(在任意子文件夹里)：不列出、不允许单独读取或删除，删除整组时才一并删除
        if (DownloadWorkFolder.IsMetadataFileName(Path.GetFileName(fullPath))) return true;
        var dir = Path.GetDirectoryName(fullPath);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!string.Equals(dir, Program.APP_DIR, comparison) && !string.Equals(dir, Program.EXE_DIR, comparison))
        {
            return false;
        }
        return ProtectedFileNameRegex().IsMatch(Path.GetFileName(fullPath));
    }

    // 也包括下载历史 history.json 及其临时文件、损坏备份，以及 Mac 客户端用来管理引擎进程的 engine.pid
    // 登录文件含国际站 BBDownTIntl.data，以及保存登录时写到一半的临时文件 *.data.writing-<guid>
    [GeneratedRegex(@"^(BBDownT?(TV|App|Intl)?\.(data(\.writing-[0-9a-f]{32})?|config|archives|web\.json)|history\.json(\..*)?|engine\.pid)$", RegexOptions.IgnoreCase)]
    private static partial Regex ProtectedFileNameRegex();

    [GeneratedRegex("^[A-Za-z0-9]{1,64}$")]
    private static partial Regex QrcodeKeyRegex();
}

public sealed record UiSessionRequest(string? Token);

public sealed record UiStatus(string Version, bool AuthRequired, bool BiliCookieSaved, string? BiliUserName,
    bool BiliVip, string? BiliVipLabel, bool TvTokenSaved, bool AppTokenSaved);

public sealed record BiliLoginStart(string Key, string QrCode);

public sealed record BiliLoginPoll(string State, string? UserName);

/// <param name="Title">下载历史里记录的视频标题；不属于任何记录时为null</param>
public sealed record DownloadedFile(string Path, long Size, long ModifiedTime, string? Title = null);
