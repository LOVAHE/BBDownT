using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace BBDownT;

internal static class DownloadMediaSource
{
    // A provider-scoped object key, not a digest of the downloaded content.
    internal static string? CreateObjectHash(string url, bool allowCustomHost = false)
    {
        try { return CreateObjectHashCore(url, allowCustomHost); }
        catch (UriFormatException) { return null; }
        catch (ArgumentException) { return null; }
    }

    private static string? CreateObjectHashCore(string url, bool allowCustomHost)
    {
        if (string.IsNullOrEmpty(url) || !HasValidEncoding(url)
            || !Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp)
            || !string.IsNullOrEmpty(uri.UserInfo)
            || (!allowCustomHost && !IsKnownMediaHost(uri.IdnHost))) return null;

        var authorityStart = url.IndexOf("://", StringComparison.Ordinal);
        if (authorityStart < 0) return null;
        authorityStart += 3;
        var pathStart = url.IndexOfAny(['/', '?', '#'], authorityStart);
        if (pathStart < 0 || url[pathStart] != '/') return null;
        var queryStart = url.IndexOf('?', pathStart);
        var fragmentStart = url.IndexOf('#', pathStart);
        if (queryStart < 0 || (fragmentStart >= 0 && queryStart > fragmentStart)) return null;
        var path = url[pathStart..queryStart];
        if (!IsMediaPath(path)) return null;
        var queryEnd = fragmentStart < 0 ? url.Length : fragmentStart;
        var query = url[(queryStart + 1)..queryEnd];
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var part in query.Split('&'))
        {
            var separator = part.IndexOf('=');
            if (separator <= 0) return null;
            var name = part[..separator];
            var value = part[(separator + 1)..];
            if (!IsFieldName(name) || !fields.TryAdd(name, value)) return null;
        }

        foreach (var required in new[] { "uparams", "upsig", "deadline", "trid" })
            if (!fields.TryGetValue(required, out var value) || string.IsNullOrEmpty(value)) return null;

        var signedFields = new HashSet<string>(StringComparer.Ordinal);
        foreach (var name in Uri.UnescapeDataString(fields["uparams"]).Split(','))
        {
            if (!IsFieldName(name) || !signedFields.Add(name)
                || !fields.TryGetValue(name, out var value) || string.IsNullOrEmpty(value)) return null;
        }
        fields["uparams"] = string.Join(",", signedFields.OrderBy(name => name, StringComparer.Ordinal));

        // These fields rotated in repeated provider responses for the same selected object.
        foreach (var name in new[] { "deadline", "trid", "upsig", "hdnts" }) fields.Remove(name);
        if (!signedFields.Contains("qn_dyeid") && fields.TryGetValue("qn_dyeid", out var trace)
            && trace.Length == 32 && trace.All(IsHex)) fields.Remove("qn_dyeid");
        // buvid identifies the client. These two storage routes were verified
        // against the same target CDN object; other routing values stay scoped.
        if (!signedFields.Contains("buvid")) fields.Remove("buvid");
        if (fields.TryGetValue("os", out var storage) && storage is "cosovbv" or "akam") fields.Remove("os");

        var canonical = new StringBuilder("bili-media-object-v1\n");
        Append(canonical, uri.Scheme + "://" + uri.IdnHost.ToLowerInvariant() + ":"
            + uri.Port.ToString(CultureInfo.InvariantCulture));
        Append(canonical, path);
        foreach (var field in fields.OrderBy(field => field.Key, StringComparer.Ordinal))
        {
            Append(canonical, field.Key);
            Append(canonical, field.Value);
        }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString())));
    }

    private static bool IsKnownMediaHost(string host)
    {
        if (host.Equals("upos-bstar1-mirrorakam.akamaized.net", StringComparison.OrdinalIgnoreCase)
            || host.Equals("upos-hz-mirrorakam.akamaized.net", StringComparison.OrdinalIgnoreCase)) return true;
        foreach (var suffix in new[] { ".bilivideo.com", ".bilivideo.cn" })
        {
            if (!host.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) continue;
            var label = host[..^suffix.Length];
            return label.Length > 5 && label.All(c => c is >= 'a' and <= 'z' or >= 'A' and <= 'Z'
                or >= '0' and <= '9' or '-') && (label.StartsWith("upos-", StringComparison.OrdinalIgnoreCase)
                || label.StartsWith("cn-", StringComparison.OrdinalIgnoreCase));
        }
        return false;
    }

    private static bool IsMediaPath(string path)
    {
        if (!path.StartsWith("/upgcxcode/", StringComparison.Ordinal)
            && !path.StartsWith("/iupxcodeboss/", StringComparison.Ordinal)) return false;
        if (!path.EndsWith(".m4s", StringComparison.Ordinal) && !path.EndsWith(".mp4", StringComparison.Ordinal)
            && !path.EndsWith(".flv", StringComparison.Ordinal)) return false;
        foreach (var segment in path[1..].Split('/'))
        {
            var decoded = Uri.UnescapeDataString(segment);
            if (decoded.Length == 0 || decoded is "." or ".." || decoded.Contains('/') || decoded.Contains('\\')
                || decoded.Any(c => char.IsControl(c) || char.IsWhiteSpace(c))) return false;
        }
        return true;
    }

    private static bool IsFieldName(string name)
        => name.Length > 0 && name[0] is >= 'a' and <= 'z'
            && name.All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '_');

    private static bool HasValidEncoding(string value)
    {
        for (var i = 0; i < value.Length; i++)
        {
            if (char.IsWhiteSpace(value[i]) || char.IsControl(value[i]) || value[i] == '\\') return false;
            if (value[i] != '%') continue;
            if (i + 2 >= value.Length || !IsHex(value[i + 1]) || !IsHex(value[i + 2])) return false;
            i += 2;
        }
        return true;
    }

    private static bool IsHex(char c) => c is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F';

    private static void Append(StringBuilder builder, string value)
        => builder.Append(value.Length.ToString(CultureInfo.InvariantCulture)).Append(':').Append(value).Append('\n');
}
