using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Tasks;

namespace BBDownT;

internal sealed record DownloadResumeValidator(string? EntityTag, DateTimeOffset? LastModified)
{
    public bool HasStrongEntityTag => EntityTagHeaderValue.TryParse(EntityTag, out var tag) && !tag.IsWeak && tag.Tag != "*";
    public bool IsUsable => HasStrongEntityTag || (string.IsNullOrEmpty(EntityTag) && LastModified is not null);

    public static DownloadResumeValidator FromResponse(HttpResponseMessage response, bool allowBareCdnTag = false)
    {
        var tag = response.Headers.ETag is { IsWeak: false, Tag: not "*" } entityTag ? entityTag.ToString() : null;
        // Some Bili CDNs send an unquoted hexadecimal ETag. Accept this known
        // spelling only for a recognized media source, and send a quoted If-Range.
        if (tag is null && allowBareCdnTag && response.Headers.TryGetValues("ETag", out var raw))
        {
            var values = raw.ToArray();
            if (values.Length == 1 && values[0].Length == 32 && values[0].All(Uri.IsHexDigit))
                tag = $"\"{values[0]}\"";
        }
        return new(tag, response.Content.Headers.LastModified);
    }

    public void Apply(HttpRequestMessage request)
    {
        if (HasStrongEntityTag && EntityTagHeaderValue.TryParse(EntityTag, out var entityTag))
        {
            request.Headers.IfRange = new RangeConditionHeaderValue(entityTag);
        }
        else if (LastModified is not null)
        {
            request.Headers.IfRange = new RangeConditionHeaderValue(LastModified.Value);
        }
    }

    public bool Matches(HttpResponseMessage response)
        => Matches(FromResponse(response));

    public static async Task<DownloadResumeValidator?> LoadAsync(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        return Parse(await File.ReadAllTextAsync(path));
    }

    internal static DownloadResumeValidator? Parse(string text)
    {
        if (text.TrimStart().StartsWith('{')) return DownloadResumeState.Parse(text)?.Validator;
        using var reader = new StringReader(text);
        var tagLine = reader.ReadLine();
        var dateLine = reader.ReadLine();
        if (tagLine is null || dateLine is null || reader.ReadLine() is not null)
        {
            return null;
        }

        var entityTag = Decode(tagLine);
        DateTimeOffset? lastModified = DateTimeOffset.TryParse(dateLine, out var parsed)
            ? parsed
            : null;
        var validator = new DownloadResumeValidator(entityTag, lastModified);
        return validator.IsUsable ? validator : null;
    }

    public async Task SaveAsync(string path)
    {
        await File.WriteAllLinesAsync(
            path,
            [Encode(EntityTag), LastModified?.ToString("O") ?? ""]);
    }

    internal bool Matches(DownloadResumeValidator other)
        => !string.IsNullOrEmpty(EntityTag) ? EntityTag == other.EntityTag
            : string.IsNullOrEmpty(other.EntityTag) && LastModified is not null && LastModified == other.LastModified;

    internal bool KnownChanged(DownloadResumeValidator other)
        => !string.IsNullOrEmpty(EntityTag) && !string.IsNullOrEmpty(other.EntityTag) ? EntityTag != other.EntityTag
            : string.IsNullOrEmpty(EntityTag) && string.IsNullOrEmpty(other.EntityTag)
                && LastModified is not null && other.LastModified is not null && LastModified != other.LastModified;

    private static string Encode(string? value)
    {
        return string.IsNullOrEmpty(value)
            ? ""
            : Convert.ToBase64String(Encoding.UTF8.GetBytes(value));
    }

    private static string? Decode(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return null;
        }

        try
        {
            return Encoding.UTF8.GetString(Convert.FromBase64String(value));
        }
        catch (FormatException)
        {
            return null;
        }
    }
}
