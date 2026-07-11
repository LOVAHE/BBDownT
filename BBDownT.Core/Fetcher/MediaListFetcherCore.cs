using System.Text.Json;
using BBDownT.Core.Entity;
using static BBDownT.Core.Entity.Entity;

namespace BBDownT.Core.Fetcher;

internal static class MediaListFetcherCore
{
    internal static async Task<VInfo> FetchAsync(
        string bizId,
        int type,
        bool descending,
        bool includeBvid,
        string displayName,
        Func<string, Task<string>> fetch,
        Func<Task<VInfo>>? fallback = null)
    {
        var infoApi = $"https://api.bilibili.com/x/v1/medialist/info?type={type}&biz_id={bizId}&tid=0";
        var infoJsonText = await fetch(infoApi);
        using var infoJson = JsonDocument.Parse(infoJsonText);
        var infoRoot = infoJson.RootElement;
        if (!TryGetData(infoRoot, out var infoData))
        {
            if (fallback is not null)
            {
                try
                {
                    return await fallback();
                }
                catch
                {
                    // Report the original collection lookup failure below.
                }
            }
            throw CreateApiException($"获取{displayName}信息失败", infoRoot);
        }

        var title = infoData.GetProperty("title").GetString()!;
        var intro = infoData.GetProperty("intro").GetString()!;
        var publishTime = infoData.GetProperty("ctime").GetInt64();
        var pages = new List<Page>();
        var hasMore = true;
        var oid = string.Empty;
        var index = 1;

        while (hasMore)
        {
            var previousOid = oid;
            var bvidParameter = includeBvid ? "&bvid=" : string.Empty;
            var listApi = $"https://api.bilibili.com/x/v2/medialist/resource/list?type={type}&oid={oid}&otype=2&biz_id={bizId}{bvidParameter}&with_current=true&mobi_app=web&ps=20&direction=false&sort_field=1&tid=0&desc={descending.ToString().ToLowerInvariant()}";
            var listJsonText = await fetch(listApi);
            using var listJson = JsonDocument.Parse(listJsonText);
            var listRoot = listJson.RootElement;
            if (!TryGetData(listRoot, out var listData))
            {
                throw CreateApiException($"获取{displayName}视频列表失败", listRoot);
            }

            hasMore = listData.GetProperty("has_more").GetBoolean();
            foreach (var media in listData.GetProperty("media_list").EnumerateArray())
            {
                oid = media.GetProperty("id").ToString();
                if (media.TryGetProperty("attr", out var attr) && attr.GetInt32() != 0)
                {
                    continue;
                }
                AddMediaPages(media, pages, ref index);
            }
            MediaListPagination.EnsureAdvanced(previousOid, oid, hasMore);
        }

        return new VInfo
        {
            Title = title.Trim(),
            Desc = intro.Trim(),
            Pic = string.Empty,
            PubTime = publishTime,
            PagesInfo = pages,
            IsBangumi = false
        };
    }

    private static void AddMediaPages(JsonElement media, List<Page> pages, ref int index)
    {
        var pageCount = media.GetProperty("page").GetInt32();
        var description = media.GetProperty("intro").GetString()!;
        var ownerName = media.GetProperty("upper").GetProperty("name").ToString();
        var ownerMid = media.GetProperty("upper").GetProperty("mid").ToString();
        foreach (var page in media.GetProperty("pages").EnumerateArray())
        {
            var mappedPage = new Page(
                index++,
                media.GetProperty("id").ToString(),
                page.GetProperty("id").ToString(),
                string.Empty,
                pageCount == 1
                    ? media.GetProperty("title").ToString()
                    : $"{media.GetProperty("title")}_P{page.GetProperty("page")}_{page.GetProperty("title")}",
                page.GetProperty("duration").GetInt32(),
                page.GetProperty("dimension").GetProperty("width") + "x" + page.GetProperty("dimension").GetProperty("height"),
                media.GetProperty("pubtime").GetInt64(),
                media.GetProperty("cover").ToString(),
                description,
                ownerName,
                ownerMid);
            if (!pages.Contains(mappedPage))
            {
                pages.Add(mappedPage);
            }
            else
            {
                index--;
            }
        }
    }

    private static bool TryGetData(JsonElement root, out JsonElement data)
    {
        data = default;
        return root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty("data", out data)
            && data.ValueKind == JsonValueKind.Object;
    }

    private static Exception CreateApiException(string prefix, JsonElement root)
    {
        var code = root.TryGetProperty("code", out var codeElement)
            && codeElement.ValueKind == JsonValueKind.Number
            ? codeElement.GetInt32()
            : 0;
        var message = root.TryGetProperty("message", out var messageElement)
            && messageElement.ValueKind == JsonValueKind.String
            ? messageElement.GetString()
            : "未知错误";
        return new Exception($"{prefix}(code={code}): {message}");
    }
}
