using System.Security.Cryptography;
using System.Text;
using BBDownT.Core;
using BBDownT.Core.Util;

namespace BBDownT.Tests;

public class WbiKeyTests : IDisposable
{
    internal const string Nav = """
        {"code":-101,"data":{"isLogin":false,"wbi_img":{
          "img_url":"https://i0.hdslb.com/bfs/wbi/7cd084941338484aae1ad9425b84077c.png",
          "sub_url":"https://i0.hdslb.com/bfs/wbi/4932caff0ff746eab6f01bf08b70ac45.png"}}}
        """;
    internal const string MixinKey = "ea1db124af3c7062474693fa704f4ff8";

    private readonly string originalKey = Config.WBI;

    public void Dispose() => Config.WBI = originalKey;

    [Fact]
    public async Task MissingKey_IsDerivedFromTheNavImagesEvenWhenLoggedOut()
    {
        Config.WBI = "";
        var calls = new List<string>();

        await WbiKey.EnsureAsync(url => { calls.Add(url); return Task.FromResult(Nav); });

        Assert.Equal(WbiKey.NavApi, Assert.Single(calls));
        Assert.Equal(MixinKey, Config.WBI);
        Assert.Equal("bar=514&foo=114&wts=1702204169&zab=1919810&w_rid=8f6f2b5b3d485fe1886cec6a0be8c5d4",
            Parser.WbiSign("bar=514&foo=114&wts=1702204169&zab=1919810"));
    }

    [Fact]
    public async Task ExistingKey_IsNotFetchedAgain()
    {
        Config.WBI = "existing";

        await WbiKey.EnsureAsync(_ => throw new Xunit.Sdk.XunitException("No request should be sent"));

        Assert.Equal("existing", Config.WBI);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"data":{"wbi_img":{"img_url":"a.png","sub_url":"b.png"}}}""")]
    [InlineData("not json")]
    public async Task UnusableNavResponse_LeavesTheKeyEmptyWithoutThrowing(string response)
    {
        Config.WBI = "";

        await WbiKey.EnsureAsync(_ => Task.FromResult(response));

        Assert.Equal("", Config.WBI);
    }

    [Fact]
    public async Task FailedNavRequest_LeavesTheKeyEmptyWithoutThrowing()
    {
        Config.WBI = "";

        await WbiKey.EnsureAsync(_ => throw new HttpRequestException("offline"));

        Assert.Equal("", Config.WBI);
    }

    [Fact]
    public async Task WebVideoPlayRequest_FetchesTheKeyBeforeSigning()
    {
        Config.WBI = "";
        var calls = new List<string>();

        await Parser.GetPlayJsonAsync("", "BV", "1", "2", "", false, false, false,
            fetchWeb: url => { calls.Add(url); return Task.FromResult(calls.Count == 1 ? Nav : "{}"); });

        Assert.Equal(2, calls.Count);
        Assert.Equal(WbiKey.NavApi, calls[0]);
        Assert.Contains("/x/player/wbi/playurl?", calls[1]);
        AssertSignedWithMixinKey(calls[1]);
    }

    [Fact]
    public async Task BangumiWebPlayRequest_StaysUnsignedWithoutFetchingTheKey()
    {
        Config.WBI = "";
        var calls = new List<string>();

        await Parser.GetPlayJsonAsync("", "ep:1", "1", "2", "3", false, false, false,
            fetchWeb: url => { calls.Add(url); return Task.FromResult("{}"); });

        Assert.DoesNotContain("&w_rid=", Assert.Single(calls));
        Assert.Equal("", Config.WBI);
    }

    internal static void AssertSignedWithMixinKey(string url)
    {
        var start = url.IndexOf('?') + 1;
        var signature = url.IndexOf("&w_rid=", StringComparison.Ordinal);
        Assert.True(signature > start);
        var expected = Convert.ToHexStringLower(MD5.HashData(Encoding.UTF8.GetBytes(url[start..signature] + MixinKey)));
        Assert.Equal(expected, url[(signature + "&w_rid=".Length)..]);
    }
}
