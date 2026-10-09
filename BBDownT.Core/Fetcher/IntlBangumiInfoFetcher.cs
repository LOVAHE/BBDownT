using BBDownT.Core.Entity;
using System.Text.Json;
using BBDownT.Core.Util;
using static BBDownT.Core.Util.HTTPUtil;

namespace BBDownT.Core.Fetcher;

public class IntlBangumiInfoFetcher : IFetcher
{
    public Task<VInfo> FetchAsync(string id) => FetchAsync(id, url => GetWebSourceAsync(url));

    internal static async Task<VInfo> FetchAsync(string id, Func<string, Task<string>> fetch)
    {
        if (id.StartsWith("intl:"))
        {
            var parts = id.Split(':');
            return await IntlBangumiWebApi.FetchInfoAsync(parts[1], parts.Length > 2 ? parts[2] : null, fetch);
        }
        id = id[3..];
        string api = "https://" + (Config.HOST == "api.bilibili.com" ? "api.bilibili.tv" : Config.HOST) +
                     $"/intl/gateway/v2/ogv/view/app/season?ep_id={id}&platform=android&s_locale=zh_SG&mobi_app=bstar_a" + (Config.TOKEN != "" ? $"&access_key={Config.TOKEN}" : "");
        string json = (await fetch(api)).Replace("\\/", "/");
        using var infoJson = JsonDocument.Parse(json);
        IntlBangumiWebApi.EnsureSuccess(infoJson.RootElement);
        var result = infoJson.RootElement.GetProperty("result");
        string seasonId = result.GetProperty("season_id").ToString();
        string cover = result.GetProperty("cover").ToString();
        string title = result.GetProperty("title").ToString();
        string desc = result.GetProperty("evaluate").ToString();
        long pubTime = PublishTime.Parse(result.GetProperty("publish").GetProperty("pub_time").ToString());
        var pages = new List<JsonElement>();
        if (result.TryGetProperty("episodes", out JsonElement episodes))
        {
            pages = episodes.EnumerateArray().ToList();
        }

        if (result.TryGetProperty("modules", out JsonElement modules))
        {
            foreach (var section in modules.EnumerateArray())
            {
                if (section.TryGetProperty("data", out var data) && data.TryGetProperty("episodes", out var sectionEpisodes)
                    && BangumiPageMapper.ContainsEpisode(sectionEpisodes, id))
                {
                    pages = section.GetProperty("data").GetProperty("episodes").EnumerateArray().ToList();
                    break;
                }
            }
        }

        var (pagesInfo, index) = BangumiPageMapper.Map(pages, id, allowMissingPublicationTime: true);
        if (pagesInfo.Count == 0 && result.TryGetProperty("limit", out var limit)
            && limit.ValueKind == JsonValueKind.Object && limit.TryGetProperty("content", out var content))
        {
            if (seasonId.Length > 0) return await IntlBangumiWebApi.FetchInfoAsync(seasonId, id, fetch);
            throw new InvalidOperationException($"国际站无法获取分集：{content}");
        }
        var info = new VInfo
        {
            Title = title.Trim(),
            Desc = desc.Trim(),
            Pic = cover,
            PubTime = pubTime,
            PagesInfo = pagesInfo,
            IsBangumi = true,
            IsCheese = true,
            Index = index
        };

        return info;
    }
}
