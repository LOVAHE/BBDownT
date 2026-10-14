namespace BBDownT.Core.Util;

internal static class IntlGateway
{
    internal static string Host(string fallback) => Config.HOST == "api.bilibili.com" ? fallback : Config.HOST;

    internal static string LegacySeasonUrl(string episodeId, bool withMobiApp)
        => $"https://{Host("api.bilibili.tv")}/intl/gateway/v2/ogv/view/app/season?ep_id={episodeId}&platform=android&s_locale=zh_SG"
            + (withMobiApp ? "&mobi_app=bstar_a" : "")
            + (Config.TOKEN != "" ? $"&access_key={Config.TOKEN}" : "");
}
