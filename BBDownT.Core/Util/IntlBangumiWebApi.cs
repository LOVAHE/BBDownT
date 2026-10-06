using System.Globalization;
using System.Text.Json;
using BBDownT.Core.Entity;
using static BBDownT.Core.Entity.Entity;

namespace BBDownT.Core.Util;

internal static class IntlBangumiWebApi
{
    internal static async Task<VInfo> FetchInfoAsync(
        string seasonId, string? episodeId, Func<string, Task<string>> fetch)
    {
        var seasonData = ReadData(await fetch(BuildUrl(
            "v2/ogv/play/season_info", $"season_id={Uri.EscapeDataString(seasonId)}")));
        if (!seasonData.TryGetProperty("season", out var season) || season.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("国际站未返回番剧资料");

        var episodeData = ReadData(await fetch(BuildUrl(
            "v2/ogv/play/episodes", $"season_id={Uri.EscapeDataString(seasonId)}")));
        if (!episodeData.TryGetProperty("sections", out var sections) || sections.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("国际站未返回分集列表");

        List<Page> pages = [];
        HashSet<string> seen = [];
        foreach (var section in sections.EnumerateArray())
        {
            if (!section.TryGetProperty("episodes", out var episodes) || episodes.ValueKind != JsonValueKind.Array)
                continue;
            foreach (var episode in episodes.EnumerateArray())
            {
                var epId = ReadText(episode, "episode_id");
                if (epId.Length == 0 || !epId.All(char.IsAsciiDigit) || !seen.Add(epId)) continue;
                var title = ReadText(episode, "title_display");
                if (title.Length == 0)
                    title = (ReadText(episode, "short_title_display") + " " + ReadText(episode, "long_title_display")).Trim();
                var pubTime = DateTimeOffset.TryParse(ReadText(episode, "publish_time"),
                    CultureInfo.InvariantCulture, DateTimeStyles.None, out var published)
                    ? published.ToUnixTimeSeconds() : 0;

                // WEB episodes have no domestic AID/CID. Keep those absent;
                // Page.DownloadId supplies a separate, namespaced cache key.
                pages.Add(new Page(pages.Count + 1, "", "", epId, title, 0, "", pubTime,
                    ReadText(episode, "cover")));
            }
        }
        if (pages.Count == 0) throw new InvalidDataException("国际站没有可用分集");

        if (string.IsNullOrEmpty(episodeId))
        {
            episodeId = season.TryGetProperty("first_episode", out var first)
                && first.ValueKind == JsonValueKind.Object ? ReadText(first, "episode_id") : "";
            if (!pages.Any(page => page.epid == episodeId)) episodeId = pages[0].epid;
        }
        var selected = pages.Find(page => page.epid == episodeId)
            ?? throw new InvalidDataException("指定的国际站分集不在此番剧中");

        var cover = ReadText(season, "horizontal_cover");
        if (cover.Length == 0) cover = ReadText(season, "vertical_cover");
        return new VInfo
        {
            IntlSeasonId = seasonId,
            Title = ReadText(season, "title").Trim(),
            Desc = ReadText(season, "description").Trim(),
            Pic = cover,
            PubTime = 0,
            IsBangumi = true,
            IsBangumiEnd = season.TryGetProperty("is_finished", out var finished) && finished.ValueKind == JsonValueKind.True,
            Index = selected.index.ToString(CultureInfo.InvariantCulture),
            PagesInfo = pages
        };
    }

    internal static Task<string> GetPlayJsonAsync(string episodeId, string quality, Func<string, Task<string>> fetch)
        => fetch(BuildUrl("playurl", $"ep_id={Uri.EscapeDataString(episodeId)}&qn={(quality == "0" ? "64" : Uri.EscapeDataString(quality))}&type=0&device=wap&tf=0"));

    private static string BuildUrl(string route, string query)
    {
        var host = Config.HOST == "api.bilibili.com" ? "api.bilibili.tv" : Config.HOST;
        return $"https://{host}/intl/gateway/web/{route}?{query}&s_locale=en_US&platform=web"
            + (Config.TOKEN.Length == 0 ? "" : $"&access_key={Uri.EscapeDataString(Config.TOKEN)}");
    }

    private static JsonElement ReadData(string json)
    {
        using var document = JsonDocument.Parse(json);
        EnsureSuccess(document.RootElement);
        if (!document.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("国际站接口未返回有效数据");
        return data.Clone();
    }

    internal static void EnsureSuccess(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("国际站接口返回了无效数据");
        if (!root.TryGetProperty("code", out var codeNode)) return;
        if (!int.TryParse(codeNode.ToString(), out var code))
            throw new InvalidDataException("国际站接口返回了无效状态码");
        if (code == 0) return;

        var knownMessage = ErrorDescription(code);
        if (knownMessage is not null) throw IntlApiException.FromCode(code);
        var error = "国际站请求失败" + (ReadText(root, "message") is { Length: > 0 } detail && detail != code.ToString()
            ? $"：{detail}" : "") + $"（错误码 {code}）";
        throw new InvalidOperationException(error);
    }

    internal static void EnsurePlaybackSuccess(JsonElement root)
    {
        EnsureSuccess(root);
        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object
            || !data.TryGetProperty("video_info", out var video) || video.ValueKind == JsonValueKind.Object)
            return;
        if (data.TryGetProperty("dialog", out var dialog) && dialog.ValueKind == JsonValueKind.Object
            && ReadText(dialog, "action") == "2" && ReadText(dialog, "type") == "3")
            throw IntlApiException.RegionRestricted();
        throw new InvalidDataException("国际站 App 未返回可用音视频流，请检查内容权限");
    }

    internal static string? ErrorDescription(int code)
        => code switch
        {
            10004001 or 10015001 => "国际站版权地区限制，当前网络出口无法播放该内容",
            10004004 or 10004006 => "该国际站视频需要 Premium 会员权限",
            10004005 or -101 => "该国际站视频需要登录，请通过 -c 提供国际站 Cookie",
            10023006 => "国际站缺少设备凭证，请通过 -c 提供完整的国际站 Cookie",
            10023014 => "当前内容仅在国际版 App 提供",
            10015002 => "国际站请求失败：访问权限不足",
            -404 or 10004003 or 10015404 => "国际站番剧或分集不存在",
            _ => null
        };

    private static string ReadText(JsonElement node, string property)
        => node.TryGetProperty(property, out var value) && value.ValueKind != JsonValueKind.Null ? value.ToString() : "";
}

internal sealed class IntlApiException : InvalidOperationException
{
    internal string? UserDescription { get; }
    internal int? ApiCode { get; }

    internal IntlApiException(string message) : base(message) { }

    private IntlApiException(string description, int? code)
        : base(description + (code is null ? "" : $"（错误码 {code}）"))
    {
        UserDescription = Message;
        ApiCode = code;
    }

    internal static IntlApiException FromCode(int code)
        => new(IntlBangumiWebApi.ErrorDescription(code) ?? "国际站请求失败", code);

    internal static IntlApiException RegionRestricted()
        => new("国际站版权地区限制，当前网络出口无法播放该内容", null);
}
