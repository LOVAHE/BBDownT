using BBDownT.Core.Util;

namespace BBDownT.Tests;

public class SubtitleUrlResolverTests
{
    // Issue #16's encoded path, with the expiring auth_key replaced in tests.
    internal const string EncodedPath = "%01%1B%5C=_%04%12%12%049f%2F%07H%08%29~%16$5%0D.%0B%0AL%03%2C%01%1A%00M:%1Ce%00%0F%1FF%11%1C%0E%1D%2A%01%12%29%0EL%16GTj%14%14%22%16%14%5E%0E@j%10%18W%5D%5EPIAvQo%5B%14%16%2CU_ZX%00%0A%5E_r%13ObK%0B%1C%10";
    private const string OtherEncoding = "S%13%1BP.%1D%28%29X%2CR%5Ej%1F%25w%0E%02H%5EHO4%14%7B4%08K%40%3C%7B%00M%0B%0A%1AM%1A%19%0BI%2A2%14%3C%7DZ%1E%17KHG0%1B~FC%5B%17D%08%08BP%0FyM%5B_p%1EP%06%5C%0A%5ET_%5D%0B%5EqpI%3Fc%40%11%5D%16%12";
    internal const string CdnUrl = "https://aisubtitle.hdslb.com/bfs/subtitle/4e9242fb1b6317ac2807c23610d5f7661bc261f1.json";

    // BV18U6yYJE1t's AI track uses an extensionless production object.
    internal const string AiProductionEncodedPath = "%01%1B%5C=_%04%12%12%049f%2F%07H%08%29~%16$5%0D.%0B%0AL%03%2C%01%1A%00M:%1Ce%00%0F%1FF%03%003%1A6%17%0A%25U%14%16Q%16%2CI%16o%16G%5B%0AGoEBVWYS%1EKqUk%5CD%13rVZ%5C%5DT%08XQ%20D%18~C%1E%15M%02n%10JuAG_XCn%10%1FV%07%5CP%13Fq%03";
    private const string AiProductionOtherEncoding = "S%13%1BP.%1D%28%29X%2CR%5Ej%1F%25w%0E%02H%5EHO4%14%7B4%08K%40%3C%7B%00M%0B%0A%1AM%08%056N6%24%0C0%26%02%1E%01%09%0E%1A2V~%15F_%10A%5DRCZ%08z%1AQXt%1AWVYT%5DQYX_%5Cw~%1Bh4%5C%19H%1FO%11fOw%11%13Y%11%16%5BX%11%0C%0C%2C%1C%5EUs%1B%04";
    internal const string AiProductionCdnUrl = "https://aisubtitle.hdslb.com/bfs/ai_subtitle/prod/11376249327448425740843046a48c1f2bff3d0685f17e23ad3b27954d";

    [Theory]
    [InlineData(EncodedPath)]
    [InlineData(OtherEncoding)]
    public void Normalize_DecodesBothKnownFormatsAndPreservesSignedQuery(string path)
    {
        const string query = "?auth_key=test-123&signature=a%2Bb%2Fc%3D&empty=";
        var result = SubtitleUrlResolver.Normalize("//subtitle.bilibili.com/" + path + query);

        Assert.Equal(CdnUrl + query, result);
        Assert.Equal(result, SubtitleUrlResolver.Normalize(result));
    }

    [Theory]
    [InlineData(AiProductionEncodedPath)]
    [InlineData(AiProductionOtherEncoding)]
    public void Normalize_DecodesAiProductionObjectsAndPreservesSignedQuery(string path)
    {
        const string query = "?auth_key=test-123&signature=a%2Bb%2Fc%3D&empty=";

        var result = SubtitleUrlResolver.Normalize("//subtitle.bilibili.com/" + path + query);

        Assert.Equal(AiProductionCdnUrl + query, result);
        Assert.Equal(result, SubtitleUrlResolver.Normalize(result));
    }

    [Theory]
    [InlineData("http://")]
    [InlineData("https://")]
    public void Normalize_AcceptsAbsolutePseudoHostUrls(string scheme)
    {
        Assert.Equal(CdnUrl, SubtitleUrlResolver.Normalize(scheme + "SUBTITLE.BILIBILI.COM/" + EncodedPath));
    }

    [Theory]
    [InlineData("//i0.hdslb.com/bfs/subtitle/a.json", "https://i0.hdslb.com/bfs/subtitle/a.json")]
    [InlineData(" https://cdn.example/a.ass?x=%2B ", "https://cdn.example/a.ass?x=%2B")]
    [InlineData("http://aisubtitle.hdslb.com/a.json", "http://aisubtitle.hdslb.com/a.json")]
    [InlineData("https://subtitle.bilibili.com.example/a.json", "https://subtitle.bilibili.com.example/a.json")]
    public void Normalize_PreservesOrdinaryAndInternationalUrls(string input, string expected)
    {
        Assert.Equal(expected, SubtitleUrlResolver.Normalize(input));
    }

    [Theory]
    [InlineData("")]
    [InlineData("unknown-encoding")]
    [InlineData("%GG")]
    [InlineData("%01%1B%5C")]
    public void Normalize_RejectsUnsupportedOrTruncatedEncoding(string path)
    {
        Assert.Throws<FormatException>(() => SubtitleUrlResolver.Normalize("https://subtitle.bilibili.com/" + path));
    }

    [Theory]
    [InlineData("/bfs/subtitle/")]
    [InlineData("/bfs/ai_subtitle/prod/")]
    [InlineData("/bfs/ai_subtitle/prod-other/object")]
    [InlineData("/bfs/ai_subtitle/other/object")]
    [InlineData("/bfs/other/object")]
    [InlineData("/bfs/subtitle/../object.json")]
    [InlineData("/bfs/ai_subtitle/prod/../object")]
    [InlineData("/bfs/ai_subtitle/prod/./object")]
    [InlineData("/bfs/ai_subtitle/prod/object?redirect=other")]
    [InlineData("/bfs/ai_subtitle/prod/object#fragment")]
    [InlineData("/bfs/ai_subtitle/prod/object\\other")]
    [InlineData("/bfs/ai_subtitle/prod/object\u0000")]
    [InlineData("/bfs/subtitle/%2e%2e/object.json")]
    [InlineData("/bfs/ai_subtitle/prod/%2e%2e/object")]
    [InlineData("/bfs/ai_subtitle/prod/object%2f..%2fother")]
    [InlineData("/bfs/ai_subtitle/prod/object%5cother")]
    [InlineData("/bfs/ai_subtitle/prod/object%00")]
    [InlineData("/bfs/ai_subtitle/prod/%252e%252e/object")]
    public void Normalize_RejectsInvalidDecodedPaths(string path)
    {
        const string prefix = """nP](wOFRvU.+<fjS{jn-!$D|Dz&",zT`""";
        const string key = """=CFxYRn{.y|uVyO$uh&sikph?N.ilF/`bilibili""";
        var plain = prefix + path;
        var cipher = new string(plain.Select((value, index) => (char)(value ^ key[index % key.Length])).ToArray());
        var url = "https://subtitle.bilibili.com/" + Uri.EscapeDataString(cipher);

        var exception = Assert.Throws<FormatException>(() => SubtitleUrlResolver.Normalize(url));

        Assert.Equal("字幕地址解码后的路径无效", exception.Message);
    }
}
