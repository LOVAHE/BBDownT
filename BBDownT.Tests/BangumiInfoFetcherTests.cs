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
            Episode("10", "1", "First", badge: "预告"),
            Episode("20", "2", "Second"),
            Episode("30", "3", ""));
        var requests = new List<string>();

        var result = await Fetch(international, "ep:30", response, requests);

        Assert.Single(requests);
        Assert.Equal("Season", result.Title);
        Assert.Equal("Description", result.Desc);
        Assert.Equal("https://example.test/cover.jpg", result.Pic);
        Assert.Equal(0, result.PubTime);
        Assert.True(result.IsBangumi);
        // Preserve this existing flag until its download semantics are decided separately.
        Assert.True(result.IsCheese);
        Assert.Equal("2", result.Index);
        Assert.Equal(international ? "7" : null, result.IntlSeasonId);
        Assert.Collection(result.PagesInfo,
            page =>
            {
                Assert.Equal(1, page.index);
                Assert.Equal("20", page.epid);
                Assert.Equal("120", page.aid);
                Assert.Equal("220", page.cid);
                Assert.Equal("2 Second", page.title);
                Assert.Equal(123, page.pubTime);
                Assert.Equal(0, page.dur);
            },
            page =>
            {
                Assert.Equal(2, page.index);
                Assert.Equal("30", page.epid);
                Assert.Equal("3", page.title);
            });
    }

    [Theory]
    [InlineData(false, "api.bilibili.com", "api.bilibili.com", "",
        "https://api.bilibili.com/pgc/view/web/season?ep_id=30")]
    [InlineData(false, "proxy.example.test", "ep.example.test", "tok",
        "https://ep.example.test/pgc/view/web/season?ep_id=30")]
    [InlineData(true, "api.bilibili.com", "api.bilibili.com", "",
        "https://api.bilibili.tv/intl/gateway/v2/ogv/view/app/season?ep_id=30&platform=android&s_locale=zh_SG&mobi_app=bstar_a")]
    [InlineData(true, "proxy.example.test", "ep.example.test", "tok",
        "https://proxy.example.test/intl/gateway/v2/ogv/view/app/season?ep_id=30&platform=android&s_locale=zh_SG&mobi_app=bstar_a&access_key=tok")]
    public async Task SeasonRequest_UsesConfiguredHostsAndToken(
        bool international, string host, string epHost, string token, string expected)
    {
        var original = (Config.HOST, Config.EPHOST, Config.TOKEN);
        var requests = new List<string>();
        try
        {
            (Config.HOST, Config.EPHOST, Config.TOKEN) = (host, epHost, token);
            await Fetch(international, "ep:30", Season(Episode("30", "1", "Title")), requests);
        }
        finally
        {
            (Config.HOST, Config.EPHOST, Config.TOKEN) = original;
        }

        Assert.Equal(expected, Assert.Single(requests));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Sections_PreserveDifferentSelectionAndTitleRules(bool international)
    {
        var response = Season(Episode("10", "1", "Main"));
        var episodes = new JsonArray(Episode("30", "SP", "Bonus"));
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
    public async Task MalformedDimensions_DoNotBreakEpisodeParsing(string dimension)
    {
        var episode = Episode("30", "1", "Title");
        episode["dimension"] = JsonNode.Parse(dimension);

        var result = await Fetch(false, "ep:30", Season(episode));

        Assert.Equal("30", Assert.Single(result.PagesInfo).epid);
    }

    [Fact]
    public async Task MissingPublicationTime_IsOptionalOnlyForInternationalEpisodes()
    {
        var episode = Episode("30", "1", "Title");
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
        var episode = Episode("30", "1", "Title");
        episode["pub_time"] = "invalid";

        await Assert.ThrowsAsync<InvalidOperationException>(() => Fetch(international, "ep:30", Season(episode)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NoDownloadableEpisodes_IsReportedInsteadOfAnEmptyDownload(bool international)
    {
        await Assert.ThrowsAsync<InvalidDataException>(() => Fetch(international, "ep:30", Season()));
        await Assert.ThrowsAsync<InvalidDataException>(() => Fetch(international, "ep:30",
            Season(Episode("30", "PV", "Trailer", badge: "预告"))));
    }

    [Fact]
    public async Task MissingInternationalCover_KeepsTheSeasonMetadataWithoutExtraRequests()
    {
        var response = Season(Episode("30", "1", "Title"));
        response["result"]!["cover"] = "";
        var requests = new List<string>();

        var result = await Fetch(true, "ep:30", response, requests);

        Assert.Single(requests);
        Assert.Equal("Season", result.Title);
        Assert.Equal("", result.Pic);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EpisodeLookup_DoesNotMatchALongerEpisodeId(bool international)
    {
        var response = Season(Episode("301", "1", "Main"));
        var section = new JsonObject { ["title"] = "Extras" };
        var episodes = new JsonArray(Episode("30", "SP", "Bonus"));
        if (international)
        {
            var other = new JsonObject { ["data"] = new JsonObject { ["episodes"] = new JsonArray(Episode("301", "1", "Main")) } };
            section["data"] = new JsonObject { ["episodes"] = episodes };
            response["result"]!["modules"] = new JsonArray(other, section);
        }
        else
        {
            section["episodes"] = episodes;
            response["result"]!["section"] = new JsonArray(section);
        }

        var result = await Fetch(international, "ep:30", response);

        Assert.Equal("30", Assert.Single(result.PagesInfo).epid);
        Assert.Equal("1", result.Index);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SeasonPublicationTime_IsReadAsBeijingTime(bool international)
    {
        var response = Season(Episode("30", "1", "Title"));
        response["result"]!["publish"]!["pub_time"] = "2024-01-01 08:00:00";

        var result = await Fetch(international, "ep:30", response);

        Assert.Equal(DateTimeOffset.Parse("2024-01-01T00:00:00Z").ToUnixTimeSeconds(), result.PubTime);
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

    private static JsonObject Episode(string id, string title, string longTitle, string badge = "")
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
        return episode;
    }
}
