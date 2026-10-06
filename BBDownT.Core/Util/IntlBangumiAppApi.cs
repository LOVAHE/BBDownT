using System.Globalization;

namespace BBDownT.Core.Util;

internal static class IntlBangumiAppApi
{
    internal static string BuildPlayUrl(
        string seasonId, string episodeId, string quality, string code = "0",
        long? timestamp = null, string? accessToken = null)
    {
        ValidateId(seasonId, nameof(seasonId));
        ValidateId(episodeId, nameof(episodeId));
        if (quality != "0" && !Config.qualitys.ContainsKey(quality))
            throw new ArgumentException("国际站 App 请求清晰度无效", nameof(quality));
        if (code is not ("0" or "1"))
            throw new ArgumentException("国际站 App 请求编码无效", nameof(code));
        var seconds = timestamp ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (seconds < 0) throw new ArgumentOutOfRangeException(nameof(timestamp));

        var parameters = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["appkey"] = "7d089525d3611b1c",
            ["build"] = "3830100",
            ["ep_id"] = episodeId,
            ["fnval"] = "16",
            ["fnver"] = "0",
            ["fourk"] = "1",
            ["mobi_app"] = "bstar_a",
            ["platform"] = "android",
            ["prefer_code_type"] = code,
            ["qn"] = quality,
            ["sid"] = seasonId,
            ["ts"] = seconds.ToString(CultureInfo.InvariantCulture)
        };
        var token = accessToken ?? Config.TOKEN;
        if (token.Length != 0) parameters["access_key"] = token;
        var query = string.Join("&", parameters.Select(pair =>
            Uri.EscapeDataString(pair.Key) + "=" + Uri.EscapeDataString(pair.Value)));
        return BuildOrigin() + "/intl/gateway/v2/app/playurl/player?" + query
            + "&sign=" + Parser.GetSign(query, true);
    }

    internal static Task<string> GetPlayJsonAsync(
        string seasonId, string episodeId, string quality, string code = "0",
        Func<string, Task<string>>? fetch = null)
    {
        var url = BuildPlayUrl(seasonId, episodeId, quality, code);
        return fetch is null ? HTTPUtil.GetIntlAppSourceAsync(url) : fetch(url);
    }

    private static void ValidateId(string id, string parameter)
    {
        if (string.IsNullOrEmpty(id) || !id.All(char.IsAsciiDigit)
            || !ulong.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) || parsed == 0)
            throw new ArgumentException("国际站 App 请求必须包含有效的番剧和分集 ID", parameter);
    }

    private static string BuildOrigin()
    {
        var host = Config.HOST == "api.bilibili.com" ? "app.biliintl.com" : Config.HOST;
        if (!Uri.TryCreate(host.Contains("://", StringComparison.Ordinal) ? host : "https://" + host,
            UriKind.Absolute, out var origin) || origin.Scheme != Uri.UriSchemeHttps
            || origin.UserInfo.Length != 0 || origin.AbsolutePath != "/"
            || origin.Query.Length != 0 || origin.Fragment.Length != 0)
            throw new ArgumentException("国际站 App 请求主机必须是 HTTPS 主机地址", nameof(Config.HOST));
        return origin.GetLeftPart(UriPartial.Authority);
    }
}
