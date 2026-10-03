using System;
using System.Linq;
using System.Text.RegularExpressions;
using System.Web;

namespace BBDownT;

internal static partial class IntlBangumiUrl
{
    internal static bool TryParse(string input, out string id)
    {
        id = "";
        if (!Uri.TryCreate(input, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            || uri.Host.ToLowerInvariant() is not ("bilibili.tv" or "www.bilibili.tv" or "biliintl.com" or "www.biliintl.com"))
            return false;

        var match = PlayPathRegex().Match(uri.AbsolutePath);
        if (!match.Success) return false;

        var seasonId = match.Groups[1].Value;
        var episodeId = match.Groups[2].Value;
        if (episodeId.Length == 0)
        {
            var query = HttpUtility.ParseQueryString(uri.Query);
            episodeId = query["ep_id"] ?? query["episode_id"] ?? "";
            if (episodeId.Length > 0 && !episodeId.All(char.IsAsciiDigit)) return false;
        }

        // Keep the season ID: a single /play/<id> identifies a whole season.
        id = $"intl:{seasonId}" + (episodeId.Length == 0 ? "" : $":{episodeId}");
        return true;
    }

    [GeneratedRegex(@"^/(?:[a-zA-Z]{2}/)?(?:play|media)/([0-9]+)(?:/([0-9]+))?/?$")]
    private static partial Regex PlayPathRegex();
}
