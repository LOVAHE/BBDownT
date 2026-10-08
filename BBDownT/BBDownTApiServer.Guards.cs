using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using BBDownT.Core;
using Microsoft.AspNetCore.Http;

namespace BBDownT;

/// <summary>
/// 跨源访问限制和请求被拒绝时的诊断日志。
/// 服务器对任意来源开放CORS，便于其他网页或工具在请求头里带API Token调用 /add-task 等接口。
/// 但未启用Token时(只监听本机)，用户浏览器里打开的任何网页都能访问本服务，所以：
/// 1. 只接受用本机地址(回环地址、localhost)访问，挡住 DNS rebinding(把攻击者的域名解析到127.0.0.1后，网页与本服务“同源”)；
/// 2. 网页前端专用的接口(/ui/*、/parse、/files、/history、/remove-pending)只接受同源请求：
///    防止别的网站读取B站账号和大会员状态、借用登录Cookie解析视频、读取或删除已下载的文件和下载历史、清空下载队列。
/// 启用Token时，只靠网页登录Cookie(而不是请求头里的Token)鉴权的请求同样只接受同源。
/// </summary>
public partial class BBDownTApiServer
{
    /// <summary>
    /// 网页前端专用、未启用Token时只接受同源访问的接口。
    /// /remove-pending(取消排队)是新接口，没有油猴脚本等旧工具依赖它，所以不像 /add-task、/remove-finished 那样对跨源开放
    /// </summary>
    internal static bool IsBrowserUiPath(PathString path) =>
        path.StartsWithSegments("/ui") || path.StartsWithSegments("/parse") || path.StartsWithSegments("/files")
        || path.StartsWithSegments("/history") || path.StartsWithSegments("/remove-pending");

    /// <summary>
    /// 浏览器发起的跨源请求。浏览器附带的 Sec-Fetch-Site 网页无法伪造，有它时只按它判断：
    /// same-origin、none(地址栏直接打开)是同源，same-site(如同一主机的其他端口)、cross-site 是跨源。
    /// 这样经过会改写 Host 的反向代理(如 nginx 默认的 proxy_set_header Host $proxy_host)访问时不会误判。
    /// 没有这个请求头时(较老的浏览器)比较 Origin 与 Host，或与反向代理传来的 X-Forwarded-Host。
    /// 两个请求头都没有的(curl、脚本等非浏览器客户端)不算跨源。
    /// </summary>
    internal static bool IsCrossOriginBrowserRequest(HttpRequest request)
    {
        var site = request.Headers["Sec-Fetch-Site"].ToString().Trim();
        if (site.Length > 0)
        {
            return !site.Equals("same-origin", StringComparison.OrdinalIgnoreCase)
                && !site.Equals("none", StringComparison.OrdinalIgnoreCase);
        }

        var origin = request.Headers.Origin.ToString();
        if (string.IsNullOrEmpty(origin)) return false;
        if (!Uri.TryCreate(origin, UriKind.Absolute, out var originUri)
            || (originUri.Scheme != Uri.UriSchemeHttp && originUri.Scheme != Uri.UriSchemeHttps))
        {
            // "null"(沙箱iframe、本地文件)等不透明来源
            return true;
        }
        if (OriginMatchesHost(originUri, request.Host)) return false;
        var forwardedHost = request.Headers["X-Forwarded-Host"].ToString().Split(',')[0].Trim();
        return forwardedHost.Length == 0 || !OriginMatchesHost(originUri, HostString.FromUriComponent(forwardedHost));
    }

    private static bool OriginMatchesHost(Uri origin, HostString host)
    {
        if (!host.HasValue || !string.Equals(origin.Host, host.Host, StringComparison.OrdinalIgnoreCase)) return false;
        // 经反向代理(HTTPS终止)访问时Host通常不带端口，此时Origin应为默认端口
        return host.Port is { } port ? origin.Port == port : origin.IsDefaultPort;
    }

    /// <summary>
    /// 未启用Token时允许的 Host：回环地址(127.0.0.0/8、[::1])、localhost、*.localhost，以及 --server-allowed-hosts 指定的域名。
    /// 没有 Host 的请求(HTTP/1.0)不是浏览器发出的，放行。
    /// </summary>
    internal static bool IsAllowedHostWithoutToken(HostString host, IReadOnlyCollection<string>? allowedHosts)
    {
        if (!host.HasValue) return true;
        var name = host.Host.Trim().TrimStart('[').TrimEnd(']').TrimEnd('.');
        if (name.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        if (IPAddress.TryParse(name, out var address) && IPAddress.IsLoopback(address)) return true;
        return allowedHosts?.Any(allowed => string.Equals(allowed.Trim().TrimEnd('.'), name, StringComparison.OrdinalIgnoreCase)) == true;
    }

    /// <summary>
    /// --server-allowed-hosts 的值：逗号分隔的域名，可带端口或协议(只取主机名)
    /// </summary>
    internal static IReadOnlyList<string> ParseAllowedHosts(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return [];
        var hosts = new List<string>();
        foreach (var item in value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = item.Contains("://", StringComparison.Ordinal) ? item : "http://" + item;
            if (Uri.TryCreate(candidate, UriKind.Absolute, out var uri) && uri.Host.Length > 0)
                hosts.Add(uri.Host.TrimStart('[').TrimEnd(']').TrimEnd('.'));
        }
        return hosts;
    }

    /// <summary>
    /// 未启用Token时：Host 不是本机地址的请求一律拒绝(防 DNS rebinding)，跨源访问网页前端接口也拒绝。
    /// 返回拒绝原因，允许时返回null。预检(OPTIONS)请求同样拒绝，浏览器因此不会发出真正的请求。
    /// </summary>
    internal string? CheckLocalAccess(HttpRequest request)
    {
        if (requireApiToken) return null;
        if (!IsAllowedHostWithoutToken(request.Host, serverOptions.AllowedHosts))
        {
            return "未启用API Token时只接受通过本机地址（127.0.0.1、localhost）访问。"
                + "经反向代理或域名访问请设置 --api-token；确需不带Token访问，可用 --server-allowed-hosts 指定允许的域名";
        }
        return IsBrowserUiPath(request.Path) && IsCrossOriginBrowserRequest(request)
            ? "未启用API Token时，网页前端接口(/ui、/parse、/files、/history、/remove-pending)只接受同源请求"
            : null;
    }

    /// <summary>
    /// 不阻塞地在指定地址启动(供测试检查完整的中间件顺序)；不启动下载队列
    /// </summary>
    internal async Task StartWithoutQueueAsync(string url, string? configuredApiToken)
    {
        if (app is null) throw new InvalidOperationException("请先调用 SetUpServer");
        ConfigureApiToken(new Uri(url), configuredApiToken);
        app.Urls.Add(url);
        await app.StartAsync();
    }

    internal Task StopAsync() => app?.StopAsync() ?? Task.CompletedTask;

    /// <summary>
    /// 请求被400拒绝时记录原因，便于排查；只记录字段名和去掉查询参数的链接，不记录Cookie、Token等字段的值
    /// </summary>
    internal static void LogRejectedRequest(string endpoint, ServeRequestOptions req, string message, List<string>? sentFields = null)
    {
        Logger.LogWarn($"已拒绝 {endpoint} 请求：{message}（{DescribeRequestForLog(req, sentFields)}）");
    }

    internal static void LogUnreadableRequest(string endpoint, Exception? exception)
    {
        var path = exception is JsonException { Path: { Length: > 0 } jsonPath } ? $"，位置 {jsonPath}" : "";
        Logger.LogWarn($"已拒绝 {endpoint} 请求：请求体不是有效的JSON或字段类型不符（{exception?.GetType().Name ?? "无请求体"}{path}）");
    }

    /// <param name="sentFields">规范化(如 /parse 补上 OnlyShowInfo、WorkDir)之前记下的字段名；为null时按当前请求计算</param>
    internal static string DescribeRequestForLog(ServeRequestOptions req, List<string>? sentFields = null)
    {
        var fields = sentFields ?? ChangedFieldNames(req);
        return $"请求字段：{(fields.Count == 0 ? "无" : string.Join(", ", fields))}；链接：{DescribeUrlForLog(req.Url)}";
    }

    /// <summary>
    /// 与默认值不同的请求字段名(按JSON序列化结果比较，NativeAOT下可用)
    /// </summary>
    internal static List<string> ChangedFieldNames(ServeRequestOptions req)
    {
        var typeInfo = SourceGenerationContext.Default.ServeRequestOptions;
        using var actual = JsonSerializer.SerializeToDocument(req, typeInfo);
        using var defaults = JsonSerializer.SerializeToDocument(new ServeRequestOptions(), typeInfo);
        var names = new List<string>();
        foreach (var property in actual.RootElement.EnumerateObject())
        {
            if (!defaults.RootElement.TryGetProperty(property.Name, out var defaultValue)
                || !JsonElement.DeepEquals(property.Value, defaultValue))
            {
                names.Add(property.Name);
            }
        }
        return names;
    }

    /// <summary>
    /// 链接只保留协议、域名和路径(查询参数里可能有分享者信息)；BV/av等编号原样保留，其他输入不记录内容
    /// </summary>
    internal static string DescribeUrlForLog(string? url)
    {
        var value = url?.Trim() ?? "";
        if (value.Length == 0) return "空";
        if (Uri.TryCreate(value, UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
        {
            return $"{uri.Scheme}://{uri.Host}{uri.AbsolutePath}";
        }
        return SafeIdRegex().IsMatch(value) ? value : $"非链接输入（{value.Length} 个字符）";
    }

    [GeneratedRegex("^[A-Za-z0-9:_-]{1,64}$")]
    private static partial Regex SafeIdRegex();
}
