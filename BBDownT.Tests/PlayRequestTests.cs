using BBDownT.Core;

namespace BBDownT.Tests;

public class PlayRequestTests
{
    [Theory]
    [InlineData("0", null)]
    public async Task BangumiWeb_KeepsV2AndOmitsBrowserHint(string qn, string? language)
    {
        var urls = new List<Uri>();
        const string response = "{\"code\":0,\"result\":{\"play_check\":{\"limit_play_reason\":\"DRM_UNSUPPORTED\"}}}";

        var actual = await Parser.GetPlayJsonAsync("", "ep:808545", "538011486", "1387128145", "808545",
            false, false, false, qn, language, url =>
            {
                urls.Add(new Uri(url));
                return Task.FromResult(response);
            });

        var request = Assert.Single(urls);
        Assert.Equal("/pgc/player/web/v2/playurl", request.AbsolutePath);
        Assert.DoesNotContain("from_client", request.Query);
        Assert.DoesNotContain("drm_tech_type", request.Query);
        Assert.Contains("fnval=4048", request.Query);
        Assert.Contains("qn=" + qn, request.Query);
        Assert.Contains("support_multi_audio=true", request.Query);
        if (language is not null) Assert.Contains("cur_language=" + language, request.Query);
        Assert.Equal(response, actual);
    }

    [Theory]
    [InlineData("BV", "/x/player/wbi/playurl")]
    [InlineData("cheese:1", "/pugv/player/web/v2/playurl")]
    public async Task NonPgcWeb_PreservesBrowserHint(string aidOri, string path)
    {
        Uri? request = null;
        await Parser.GetPlayJsonAsync("", aidOri, "1", "2", "3", false, false, false,
            fetchWeb: url => { request = new Uri(url); return Task.FromResult("{}"); });

        Assert.Equal(path, request!.AbsolutePath);
        Assert.Contains("from_client=BROWSER", request.Query);
    }

    [Fact]
    public async Task BangumiTv_KeepsItsEndpointAndSigning()
    {
        Uri? request = null;
        await Parser.GetPlayJsonAsync("", "ep:1", "1", "2", "3", true, false, false,
            fetchWeb: url => { request = new Uri(url); return Task.FromResult("{}"); });

        Assert.Equal("/pgc/player/api/playurltv", request!.AbsolutePath);
        Assert.Contains("sign=", request.Query);
        Assert.Contains("ep_id=3", request.Query);
    }
}
