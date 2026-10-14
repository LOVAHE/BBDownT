using System;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;

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

    internal bool Matches(DownloadResumeValidator other)
        => !string.IsNullOrEmpty(EntityTag) ? EntityTag == other.EntityTag
            : string.IsNullOrEmpty(other.EntityTag) && LastModified is not null && LastModified == other.LastModified;

    internal bool SameVersionAs(DownloadResumeValidator other)
        => this == other || (HasStrongEntityTag && other.HasStrongEntityTag && Matches(other));

    internal bool KnownChanged(DownloadResumeValidator other)
        => !string.IsNullOrEmpty(EntityTag) && !string.IsNullOrEmpty(other.EntityTag) ? EntityTag != other.EntityTag
            : string.IsNullOrEmpty(EntityTag) && string.IsNullOrEmpty(other.EntityTag)
                && LastModified is not null && other.LastModified is not null && LastModified != other.LastModified;
}
