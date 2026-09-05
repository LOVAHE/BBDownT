using BBDownT.Core.Entity;
using System.Text.Json;
using System.Text.RegularExpressions;
using static BBDownT.Core.Util.HTTPUtil;

namespace BBDownT.Core.Fetcher;

public partial class IntlBangumiInfoFetcher : IFetcher
{
    public Task<VInfo> FetchAsync(string id) => FetchAsync(id, url => GetWebSourceAsync(url));

    internal static async Task<VInfo> FetchAsync(string id, Func<string, Task<string>> fetch)
    {
        id = id[3..];
        string api = "https://" + (Config.HOST == "api.bilibili.com" ? "api.bilibili.tv" : Config.HOST) +
                     $"/intl/gateway/v2/ogv/view/app/season?ep_id={id}&platform=android&s_locale=zh_SG&mobi_app=bstar_a" + (Config.TOKEN != "" ? $"&access_key={Config.TOKEN}" : "");
        string json = (await fetch(api)).Replace("\\/", "/");
        using var infoJson = JsonDocument.Parse(json);
        var result = infoJson.RootElement.GetProperty("result");
        string seasonId = result.GetProperty("season_id").ToString();
        string cover = result.GetProperty("cover").ToString();
        string title = result.GetProperty("title").ToString();
        string desc = result.GetProperty("evaluate").ToString();


        if (cover == "")
        {
            string animeUrl = $"https://bangumi.bilibili.com/anime/{seasonId}";
            var web = await fetch(animeUrl);
            if (web != "")
            {
                Regex regex = StateRegex();
                string _json = regex.Match(web).Groups[1].Value;
                using var _tempJson = JsonDocument.Parse(_json);
                cover = _tempJson.RootElement.GetProperty("mediaInfo").GetProperty("cover").ToString();
                title = _tempJson.RootElement.GetProperty("mediaInfo").GetProperty("title").ToString();
                desc = _tempJson.RootElement.GetProperty("mediaInfo").GetProperty("evaluate").ToString();
            }
        }

        string pubTimeStr = result.GetProperty("publish").GetProperty("pub_time").ToString();
        long pubTime = string.IsNullOrEmpty(pubTimeStr) ? 0 : DateTimeOffset.ParseExact(pubTimeStr, "yyyy-MM-dd HH:mm:ss", null).ToUnixTimeSeconds();
        var pages = new List<JsonElement>();
        if (result.TryGetProperty("episodes", out JsonElement episodes))
        {
            pages = episodes.EnumerateArray().ToList();
        }

        if (result.TryGetProperty("modules", out JsonElement modules))
        {
            foreach (var section in modules.EnumerateArray())
            {
                if (section.ToString().Contains($"/{id}"))
                {
                    pages = section.GetProperty("data").GetProperty("episodes").EnumerateArray().ToList();
                    break;
                }
            }
        }

        var (pagesInfo, index) = BangumiPageMapper.Map(pages, id, allowMissingPublicationTime: true);
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

    [GeneratedRegex("window.__INITIAL_STATE__=([\\s\\S].*?);\\(function\\(\\)")]
    private static partial Regex StateRegex();
}
