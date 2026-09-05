using System.Text.Json.Nodes;
using BBDownT.Core;
using BBDownT.Core.Entity;
using BBDownT.Core.Fetcher;

namespace BBDownT.Tests;

public class BangumiInfoFetcherTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Episodes_PreserveMetadataPreviewFilteringAndSelectedIndex(bool international)
    {
        var response = Season(
            Episode("10", "1", "First", "preview", badge: "预告"),
            Episode("20", "2", "Second", "1920x1080"),
            Episode("30", "3", "", "missing"));
        var requests = new List<string>();

        var result = await Fetch(international, "ep:30", response, requests);

        Assert.Single(requests);
        var expectedUrl = international
            ? $"https://{(Config.HOST == "api.bilibili.com" ? "api.bilibili.tv" : Config.HOST)}/intl/gateway/v2/ogv/view/app/season?ep_id=30&platform=android&s_locale=zh_SG&mobi_app=bstar_a"
                + (Config.TOKEN != "" ? $"&access_key={Config.TOKEN}" : "")
            : $"https://{Config.EPHOST}/pgc/view/web/season?ep_id=30";
        Assert.Equal(expectedUrl, requests[0]);
        Assert.Equal("Season", result.Title);
        Assert.Equal("Description", result.Desc);
        Assert.Equal("https://example.test/cover.jpg", result.Pic);
        Assert.Equal(0, result.PubTime);
        Assert.True(result.IsBangumi);
        // Preserve this existing flag until its download semantics are decided separately.
        Assert.True(result.IsCheese);
        Assert.Equal("2", result.Index);
        Assert.Collection(result.PagesInfo,
            page =>
            {
                Assert.Equal(1, page.index);
                Assert.Equal("20", page.epid);
                Assert.Equal("120", page.aid);
                Assert.Equal("220", page.cid);
                Assert.Equal("2 Second", page.title);
                Assert.Equal("1920x1080", page.res);
                Assert.Equal(123, page.pubTime);
                Assert.Equal(0, page.dur);
            },
            page =>
            {
                Assert.Equal(2, page.index);
                Assert.Equal("30", page.epid);
                Assert.Equal("3", page.title);
                Assert.Equal("", page.res);
            });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Sections_PreserveDifferentSelectionAndTitleRules(bool international)
    {
        var response = Season(Episode("10", "1", "Main", "missing"));
        var episodes = new JsonArray(Episode("30", "SP", "Bonus", "missing"));
        var section = new JsonObject { ["title"] = "Extras" };
        if (international)
        {
            section["data"] = new JsonObject { ["episodes"] = episodes };
            response["result"]!["modules"] = new JsonArray(section);
        }
        else
        {
            section["episodes"] = episodes;
            response["result"]!["section"] = new JsonArray(section);
        }

        var result = await Fetch(international, "ep:30", response);

        Assert.Equal(international ? "Season" : "Season [Extras]", result.Title);
        Assert.Equal("30", Assert.Single(result.PagesInfo).epid);
        Assert.Equal("1", result.Index);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("123")]
    [InlineData("{}")]
    [InlineData("{\"width\":1920}")]
    public async Task MalformedDimensions_PreserveEmptyResolutionFallback(string dimension)
    {
        foreach (var international in new[] { false, true })
        {
            var episode = Episode("30", "1", "Title", "missing");
            episode["dimension"] = JsonNode.Parse(dimension);

            var result = await Fetch(international, "ep:30", Season(episode));

            Assert.Equal("", Assert.Single(result.PagesInfo).res);
        }
    }

    [Fact]
    public async Task MissingPublicationTime_IsOptionalOnlyForInternationalEpisodes()
    {
        var episode = Episode("30", "1", "Title", "missing");
        episode.Remove("pub_time");
        var response = Season(episode);

        var result = await Fetch(true, "ep:30", response);

        Assert.Equal(0, Assert.Single(result.PagesInfo).pubTime);
        await Assert.ThrowsAsync<KeyNotFoundException>(() => Fetch(false, "ep:30", response));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InvalidPublicationTime_RemainsAnError(bool international)
    {
        var episode = Episode("30", "1", "Title", "missing");
        episode["pub_time"] = "invalid";

        await Assert.ThrowsAsync<InvalidOperationException>(() => Fetch(international, "ep:30", Season(episode)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EmptyEpisodes_PreserveEmptySelection(bool international)
    {
        var result = await Fetch(international, "ep:30", Season());

        Assert.Empty(result.PagesInfo);
        Assert.Equal("", result.Index);
    }

    [Fact]
    public async Task MissingInternationalCover_FetchesFallbackAfterSeason()
    {
        var response = Season(Episode("30", "1", "Title", "missing"));
        response["result"]!["cover"] = "";
        var requests = new List<string>();
        var responses = new Queue<string>([
            response.ToJsonString(),
            """
            window.__INITIAL_STATE__={"mediaInfo":{"cover":"fallback.jpg","title":"Fallback title","evaluate":"Fallback description"}};(function()
            """
        ]);

        var result = await IntlBangumiInfoFetcher.FetchAsync("ep:30", url =>
        {
            requests.Add(url);
            return Task.FromResult(responses.Dequeue());
        });

        Assert.Equal(2, requests.Count);
        Assert.Equal("https://bangumi.bilibili.com/anime/7", requests[1]);
        Assert.Equal("Fallback title", result.Title);
        Assert.Equal("Fallback description", result.Desc);
        Assert.Equal("fallback.jpg", result.Pic);
    }

    private static Task<VInfo> Fetch(bool international, string id, JsonObject response, List<string>? requests = null)
    {
        Task<string> FetchResponse(string url)
        {
            requests?.Add(url);
            return Task.FromResult(response.ToJsonString());
        }

        return international
            ? IntlBangumiInfoFetcher.FetchAsync(id, FetchResponse)
            : BangumiInfoFetcher.FetchAsync(id, FetchResponse);
    }

    private static JsonObject Season(params JsonNode[] episodes) => new()
    {
        ["result"] = new JsonObject
        {
            ["season_id"] = "7",
            ["title"] = " Season ",
            ["evaluate"] = " Description ",
            ["cover"] = "https://example.test/cover.jpg",
            ["publish"] = new JsonObject { ["pub_time"] = "" },
            ["episodes"] = new JsonArray(episodes)
        }
    };

    private static JsonObject Episode(string id, string title, string longTitle, string resolution, string badge = "")
    {
        var episode = new JsonObject
        {
            ["id"] = id,
            ["aid"] = "1" + id,
            ["cid"] = "2" + id,
            ["title"] = title,
            ["long_title"] = longTitle,
            ["badge"] = badge,
            ["pub_time"] = 123,
            ["link"] = $"https://example.test/ep{id}",
            ["international_link"] = $"https://example.test/play/{id}"
        };
        if (resolution == "1920x1080")
            episode["dimension"] = new JsonObject { ["width"] = 1920, ["height"] = 1080 };
        return episode;
    }
}
