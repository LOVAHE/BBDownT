using System.Text.Json;

namespace BBDownT.Core.Util;

public sealed class BilibiliApiException(string message, int code) : InvalidOperationException(message)
{
    public int Code { get; } = code;
}

internal static class BilibiliApi
{
    internal static bool IsRateLimited(int code) => code is -412 or -352 or -799 or -509;

    internal static JsonElement ReadPayload(JsonElement root, string operation, string property = "data")
    {
        ThrowIfError(root, operation);
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty(property, out var payload)
            || payload.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            throw new BilibiliApiException($"{operation}失败：接口没有返回数据", ReadCode(root) ?? 0);
        return payload;
    }

    internal static void ThrowIfError(JsonElement root, string operation)
    {
        var error = GetError(root, operation);
        if (error is not null) throw error;
    }

    internal static BilibiliApiException? GetError(JsonElement root, string operation)
    {
        var code = ReadCode(root);
        if (code is null or 0) return null;
        var reason = root.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(message.GetString()) && message.GetString()!.Trim() != code.ToString()
            ? message.GetString()!.Trim() : "接口拒绝了请求";
        return new BilibiliApiException($"{operation}失败：{reason}（错误码 {code}）", code.Value);
    }

    internal static BilibiliApiException? GetError(string json, string operation)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            return GetError(document.RootElement, operation);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static int? ReadCode(JsonElement root)
        => root.ValueKind == JsonValueKind.Object && root.TryGetProperty("code", out var code)
            && int.TryParse(code.ToString(), out var value) ? value : null;
}
