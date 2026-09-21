namespace BBDownT.Core.Util;

internal static class SubtitleUrlResolver
{
    // Public player URL-obfuscation constants, not account credentials. The web
    // subtitle API uses a pseudo host; only the path is encoded, not auth_key.
    // Player reference: https://s1.hdslb.com/bfs/static/player/main/core.cac4d56e.js
    // (At / wt / Tt). Both prefix/key pairs are still returned by the web API.
    private static readonly (string Prefix, string Key)[] Encodings =
    [
        ("""nP](wOFRvU.+<fjS{jn-!$D|Dz&",zT`""", """=CFxYRn{.y|uVyO$uh&sikph?N.ilF/`bilibili"""),
        ("""Bn"q~|albg@]Go~ACgyDvKnd+)_D}^&J?""", """Cu~L!xs~f^&r@'vh=q]q{eeng*sEg^kp#Jbilibili""")
    ];

    internal static string Normalize(string url)
    {
        url = url.Trim();
        if (url.StartsWith("//", StringComparison.Ordinal)) url = "https:" + url;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https")
            || !uri.Host.Equals("subtitle.bilibili.com", StringComparison.OrdinalIgnoreCase))
            return url;

        string encodedPath = uri.AbsolutePath[1..];
        for (int i = 0; i < encodedPath.Length; i++)
        {
            if (encodedPath[i] != '%') continue;
            if (i + 2 >= encodedPath.Length || !Uri.IsHexDigit(encodedPath[i + 1]) || !Uri.IsHexDigit(encodedPath[i + 2]))
                throw new FormatException("字幕地址包含无效的百分号编码");
            i += 2;
        }

        string cipher = Uri.UnescapeDataString(encodedPath);
        foreach (var (prefix, key) in Encodings)
        {
            var decoded = new char[cipher.Length];
            for (int i = 0; i < cipher.Length; i++)
                decoded[i] = (char)(cipher[i] ^ key[i % key.Length]);
            string plain = new(decoded);
            if (!plain.StartsWith(prefix, StringComparison.Ordinal)) continue;

            string path = plain[prefix.Length..];
            if (!path.StartsWith("/bfs/subtitle/", StringComparison.Ordinal)
                || path.Length == "/bfs/subtitle/".Length
                || path.Any(char.IsControl)
                || path.IndexOfAny(['?', '#', '\\']) >= 0
                || path.Split('/').Any(segment => segment is "." or ".."))
                throw new FormatException("字幕地址解码后的路径无效");

            return "https://aisubtitle.hdslb.com" + path + uri.Query;
        }

        throw new FormatException("暂不支持此字幕地址编码，请更新程序后重试");
    }
}
