using System;
using System.IO;
using System.Linq;
using System.Text;

namespace BBDownT;

internal static class OutputNameLimit
{
    internal const int MaxDirectoryNameBytes = 255;
    internal const int MaxFileNameBytes = 220;
    private const int MinimumValueBytes = 24;

    internal static string Fit(Func<int, string> expand, params string[] cappedValues)
    {
        var full = expand(int.MaxValue);
        if (Fits(full)) return full;
        string? best = null;
        var low = MinimumValueBytes;
        var high = cappedValues.Max(value => Encoding.UTF8.GetByteCount(value)) - 1;
        while (low <= high)
        {
            var cap = low + (high - low) / 2;
            var candidate = expand(cap);
            if (Fits(candidate))
            {
                best = candidate;
                low = cap + 1;
            }
            else high = cap - 1;
        }
        return best ?? TruncateSegments(expand(MinimumValueBytes));
    }

    internal static string Shorten(string value, int maxBytes)
    {
        var shortened = Truncate(value, maxBytes);
        return shortened.Length == value.Length ? value : shortened.TrimEnd().TrimEnd('.').TrimEnd();
    }

    internal static bool Fits(string path)
    {
        var segments = path.Split('/');
        for (var i = 0; i < segments.Length; i++)
        {
            if (Encoding.UTF8.GetByteCount(segments[i]) > Limit(i == segments.Length - 1)) return false;
        }
        return true;
    }

    internal static string Truncate(string value, int maxBytes)
    {
        if (Encoding.UTF8.GetByteCount(value) <= maxBytes) return value;
        var used = 0;
        var end = 0;
        foreach (var rune in value.EnumerateRunes())
        {
            if (used + rune.Utf8SequenceLength > maxBytes) break;
            used += rune.Utf8SequenceLength;
            end += rune.Utf16SequenceLength;
        }
        return value[..end];
    }

    private static int Limit(bool fileName) => fileName ? MaxFileNameBytes : MaxDirectoryNameBytes;

    private static string TruncateSegments(string path)
    {
        var segments = path.Split('/');
        for (var i = 0; i < segments.Length; i++)
        {
            var fileName = i == segments.Length - 1;
            if (Encoding.UTF8.GetByteCount(segments[i]) <= Limit(fileName)) continue;
            var extension = fileName ? Path.GetExtension(segments[i]) : "";
            var stem = Shorten(segments[i][..^extension.Length], Limit(fileName) - Encoding.UTF8.GetByteCount(extension));
            segments[i] = (stem.Length == 0 ? "_" : stem) + extension;
        }
        return string.Join('/', segments);
    }
}
