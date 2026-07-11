using System;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Tasks;

namespace BBDownT;

internal sealed record DownloadResumeValidator(string? EntityTag, DateTimeOffset? LastModified)
{
    public bool IsUsable => !string.IsNullOrEmpty(EntityTag) || LastModified is not null;

    public static DownloadResumeValidator FromResponse(HttpResponseMessage response)
    {
        return new(
            response.Headers.ETag is { IsWeak: false } entityTag ? entityTag.ToString() : null,
            response.Content.Headers.LastModified);
    }

    public void Apply(HttpRequestMessage request)
    {
        if (!string.IsNullOrEmpty(EntityTag)
            && EntityTagHeaderValue.TryParse(EntityTag, out var entityTag))
        {
            request.Headers.IfRange = new RangeConditionHeaderValue(entityTag);
        }
        else if (LastModified is not null)
        {
            request.Headers.IfRange = new RangeConditionHeaderValue(LastModified.Value);
        }
    }

    public bool Matches(HttpResponseMessage response)
    {
        if (!string.IsNullOrEmpty(EntityTag))
        {
            return string.Equals(
                EntityTag,
                response.Headers.ETag?.ToString(),
                StringComparison.Ordinal);
        }

        return LastModified is not null
            && response.Content.Headers.LastModified == LastModified;
    }

    public static async Task<DownloadResumeValidator?> LoadAsync(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        var lines = await File.ReadAllLinesAsync(path);
        if (lines.Length != 2)
        {
            return null;
        }

        var entityTag = Decode(lines[0]);
        DateTimeOffset? lastModified = DateTimeOffset.TryParse(lines[1], out var parsed)
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
