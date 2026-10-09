using BBDownT.Core.Entity;
using BBDownT.Core.Util;
using System.Text.Json;
using static BBDownT.Core.Util.HTTPUtil;

namespace BBDownT.Core.Fetcher;

public class BangumiInfoFetcher : IFetcher
{
    public Task<VInfo> FetchAsync(string id) => FetchAsync(id, url => GetWebSourceAsync(url));

    internal static async Task<VInfo> FetchAsync(string id, Func<string, Task<string>> fetch)
    {
        id = id[3..];
        string api = $"https://{Config.EPHOST}/pgc/view/web/season?ep_id={id}";
        string json = await fetch(api);
        using var infoJson = JsonDocument.Parse(json);
        var result = BilibiliApi.ReadPayload(infoJson.RootElement, "获取番剧信息", "result");
        string cover = result.GetProperty("cover").ToString();
        string title = result.GetProperty("title").ToString();
        string desc = result.GetProperty("evaluate").ToString();
        long pubTime = PublishTime.Parse(result.GetProperty("publish").GetProperty("pub_time").ToString());
        var pages = result.GetProperty("episodes").EnumerateArray();

        //episodes为空; 或者未包含对应epid，番外/花絮什么的
        if (!BangumiPageMapper.ContainsEpisode(result.GetProperty("episodes"), id))
        {
            if (result.TryGetProperty("section", out JsonElement sections))
            {
                foreach (var section in sections.EnumerateArray())
                {
                    if (section.TryGetProperty("episodes", out var sectionEpisodes)
                        && BangumiPageMapper.ContainsEpisode(sectionEpisodes, id))
                    {
                        title += "[" + section.GetProperty("title").ToString() + "]";
                        pages = section.GetProperty("episodes").EnumerateArray();
                        break;
                    }
                }
            }
        }

        var (pagesInfo, index) = BangumiPageMapper.Map(pages, id, allowMissingPublicationTime: false);
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
