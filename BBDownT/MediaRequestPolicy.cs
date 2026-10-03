using System.Net.Http;
using BBDownT.Core.Util;

namespace BBDownT;

internal static class MediaRequestPolicy
{
    internal static string? GetReferer(string url, bool international)
    {
        // APP and TV media requests keep their existing lack of a WEB referer.
        if (url.Contains("platform=android_tv_yst") || url.Contains("platform=android")) return null;
        return international ? "https://www.bilibili.tv/" : "https://www.bilibili.com";
    }

    internal static HttpRequestMessage CreateRequest(string url, bool international,
        long? fromPosition = null, long? toPosition = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        var referer = GetReferer(url, international);
        if (referer is not null) request.Headers.TryAddWithoutValidation("Referer", referer);
        request.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0");
        if (!international) HTTPUtil.TryAddCookieHeader(request, url);
        if (fromPosition is not null) request.Headers.Range = new(fromPosition, toPosition);
        return request;
    }
}
