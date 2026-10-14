using System.Net;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace BBDownT.Core.Util;

public static partial class ErrorText
{
    private const string DataShapeMessage = "接口返回的数据格式与预期不同";

    public static string Describe(Exception error)
    {
        if (error is AggregateException aggregate) return Sanitize(aggregate.Message);
        var causes = Causes(error).ToArray();
        if (causes.OfType<IntlApiException>().FirstOrDefault() is { } international) return international.Message;
        var api = causes.OfType<BilibiliApiException>().FirstOrDefault();
        if (api is not null) return Sanitize(api.Message);

        var messages = causes.Select(ReadableMessage).OfType<string>().Distinct().ToList();
        var text = messages.Count == 0 ? null
            : messages.Count == 1 || messages[0].Contains(messages[^1], StringComparison.Ordinal) ? messages[0]
            : $"{messages[0]}：{messages[^1]}";
        var transport = DescribeTransport(causes);
        if (text is null) return transport ?? error.GetType().Name;
        return transport is null || text.Contains(transport, StringComparison.Ordinal) ? text : $"{text}（{transport}）";
    }

    public static bool SuggestsUpdate(Exception error)
    {
        var causes = Causes(error).ToArray();
        if (causes.Any(cause => cause is BilibiliApiException or IntlApiException)) return false;
        return causes.Any(IsDataShapeError)
            || (causes.All(cause => ReadableMessage(cause) is null) && DescribeTransport(causes) is null);
    }

    public static string Sanitize(string message)
        => Logger.RedactSensitiveText(UrlPattern().Replace(message, match =>
            Uri.TryCreate(match.Value, UriKind.Absolute, out var uri)
                ? $"{uri.Scheme}://{uri.Host}{(uri.IsDefaultPort ? "" : ":" + uri.Port)}{uri.AbsolutePath}"
                : "<url>"));

    private static string? ReadableMessage(Exception error)
    {
        if (error is HttpRequestException or HttpIOException or SocketException or OperationCanceledException
            or TimeoutException or AuthenticationException)
            return null;
        var message = MessageText(error);
        var match = ResourceKeyPattern().Match(message);
        if (match.Success && message.StartsWith("net_", StringComparison.Ordinal)) return null;
        var argument = match.Success && match.Groups["argument"].Success ? match.Groups["argument"].Value : null;
        if (IsDataShapeError(error)) return DataShapeMessage;
        var label = error switch
        {
            UnauthorizedAccessException => "没有权限读写文件或目录",
            PathTooLongException => "文件路径过长",
            DirectoryNotFoundException => "找不到目录",
            FileNotFoundException => "找不到文件",
            IOException when match.Success => "读写文件失败",
            _ => null
        };
        if (label is not null)
        {
            var detail = match.Success ? argument : message;
            return string.IsNullOrWhiteSpace(detail) ? label : $"{label}：{Sanitize(detail)}";
        }
        return match.Success || string.IsNullOrWhiteSpace(message) ? null : Sanitize(message);
    }

    private static string MessageText(Exception error)
    {
        var message = error.Message;
        if (error is not ArgumentException { ParamName: { Length: > 0 } name }) return message;
        foreach (var suffix in (string[])[$" (Parameter '{name}')", $" Arg_ParamName_Name, {name}"])
            if (message.EndsWith(suffix, StringComparison.Ordinal)) return message[..^suffix.Length];
        return message;
    }

    private static bool IsDataShapeError(Exception error)
        => error is KeyNotFoundException or JsonException or FormatException or InvalidCastException
            or NullReferenceException or IndexOutOfRangeException or Google.Protobuf.InvalidProtocolBufferException;

    private static string? DescribeTransport(Exception[] causes)
    {
        var status = causes.OfType<HttpRequestException>().FirstOrDefault(cause => cause.StatusCode.HasValue)?.StatusCode;
        if (status is { } code) return $"服务器返回 HTTP {(int)code}";
        var requestError = causes.OfType<HttpRequestException>().Select(cause => cause.HttpRequestError)
            .Concat(causes.OfType<HttpIOException>().Select(cause => cause.HttpRequestError))
            .FirstOrDefault(kind => kind != HttpRequestError.Unknown);
        if (causes.Any(cause => cause is AuthenticationException) || requestError == HttpRequestError.SecureConnectionError)
            return "TLS 安全连接失败";
        if (causes.Any(cause => cause is TimeoutException)) return "请求超时";
        var socket = causes.OfType<SocketException>().FirstOrDefault()?.SocketErrorCode;
        if (requestError == HttpRequestError.NameResolutionError
            || socket is SocketError.HostNotFound or SocketError.TryAgain or SocketError.NoData)
            return "域名解析失败";
        if (socket is { } socketError) return $"网络连接失败（{socketError}）";
        if (requestError == HttpRequestError.ConnectionError) return "网络连接失败";
        if (requestError == HttpRequestError.ResponseEnded || causes.Any(cause => cause is HttpIOException))
            return "连接中断";
        if (causes.Any(cause => cause is HttpRequestException)) return "网络请求失败";
        return causes.Any(cause => cause is OperationCanceledException) ? "操作已取消" : null;
    }

    private static IEnumerable<Exception> Causes(Exception error)
    {
        for (Exception? cause = error; cause is not null; cause = cause.InnerException) yield return cause;
    }

    [GeneratedRegex(@"^[A-Za-z][A-Za-z0-9]*_[A-Za-z0-9_]*(?:, (?<argument>.*))?$", RegexOptions.Singleline)]
    private static partial Regex ResourceKeyPattern();

    [GeneratedRegex(@"https?://[^\s""'<>，。；、）]+", RegexOptions.IgnoreCase)]
    private static partial Regex UrlPattern();
}
