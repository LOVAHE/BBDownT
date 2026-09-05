using System.Text.Json.Nodes;
using BBDownT.Core.Entity;
using BBDownT.Core.Fetcher;
using static BBDownT.Core.Entity.Entity;

namespace BBDownT.Tests;

public class FavListFetcherTests
{
    [Fact]
    public async Task Pagination_PreservesOrderFilteringDuplicatesAndMultiPageMetadata()
    {
        var responses = new Queue<string>([
            Response(41, Media(1), Media(99, attr: 1)),
            Response(41, Media(1), Media(2, pageCount: 2)),
            Response(41, Media(3))
        ]);
        var calls = new List<string>();
        var result = await FavListFetcher.FetchAsync("favId:42:7", url =>
        {
            calls.Add(url);
            return Task.FromResult(responses.Dequeue());
        }, aid =>
        {
            calls.Add("video:" + aid);
            return Task.FromResult(new VInfo
            {
                Title = "Video",
                Desc = "Video description",
                Pic = "multi-cover.jpg",
                PubTime = 120,
                PagesInfo = [
                    new Page(1, aid, "201", "", "First", 11, "1920x1080", 120, "", "", "Author", "7"),
                    new Page(2, aid, "202", "", "Second", 12, "1280x720", 120, "", "", "Author", "7")
                ]
            });
        });

        Assert.Empty(responses);
        Assert.Equal(new[] { Url(1), Url(2), Url(3), "video:2" }, calls);
        Assert.Equal("Favorites", result.Title);
        Assert.Equal("Introduction", result.Desc);
        Assert.Equal("", result.Pic);
        Assert.Equal(100, result.PubTime);
        Assert.False(result.IsBangumi);
        Assert.Equal(new[] { "1", "2", "2", "3" }, result.PagesInfo.Select(page => page.aid));
        // The old loop increments before deduplication; preserve its index gaps.
        Assert.Equal(new[] { 1, 3, 4, 5 }, result.PagesInfo.Select(page => page.index));
        Assert.Equal(new[] { "Video 1", "Video 2_P1_First", "Video 2_P2_Second", "Video 3" },
            result.PagesInfo.Select(page => page.title));
        var multi = result.PagesInfo[1];
        Assert.Equal("201", multi.cid);
        Assert.Equal("multi-cover.jpg", multi.cover);
        Assert.Equal("Media description 2", multi.desc);
        Assert.Equal("Author", multi.ownerName);
        Assert.Equal("7", multi.ownerMid);
        Assert.Equal("1920x1080", multi.res);
        Assert.Equal(11, multi.dur);
        Assert.Equal(120, multi.pubTime);
        var last = result.PagesInfo[^1];
        Assert.Equal("cover3.jpg", last.cover);
        Assert.Equal("Media description 3", last.desc);
        Assert.Equal("Uploader", last.ownerName);
        Assert.Equal("8", last.ownerMid);
        Assert.Equal(130, last.pubTime);
    }

    [Fact]
    public async Task DefaultFolder_IsResolvedBeforeReadingItsMedia()
    {
        var responses = new Queue<string>([
            """{"data":{"list":[{"id":42},{"id":43}]}}""",
            Response(0)
        ]);
        var calls = new List<string>();

        var result = await FavListFetcher.FetchAsync("favId::7", url =>
        {
            calls.Add(url);
            return Task.FromResult(responses.Dequeue());
        }, _ => throw new InvalidOperationException("No video expansion expected"));

        Assert.Equal(new[] {
            "https://api.bilibili.com/x/v3/fav/folder/created/list-all?up_mid=7", Url(1)
        }, calls);
        Assert.Empty(result.PagesInfo);
    }

    [Fact]
    public async Task LaterPageFailure_DoesNotStartVideoExpansion()
    {
        var requests = 0;
        var expected = new InvalidOperationException("fixture failure");

        var actual = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            FavListFetcher.FetchAsync("favId:42:7", _ =>
            {
                if (++requests == 2) throw expected;
                return Task.FromResult(Response(21, Media(2, pageCount: 2)));
            }, _ => throw new InvalidOperationException("Video expansion must wait for pagination")));

        Assert.Same(expected, actual);
        Assert.Equal(2, requests);
    }

    private static string Url(int page) =>
        $"https://api.bilibili.com/x/v3/fav/resource/list?media_id=42&pn={page}&ps=20&order=mtime&type=2&tid=0&platform=web";

    private static string Response(int count, params JsonNode[] medias) => new JsonObject
    {
        ["data"] = new JsonObject
        {
            ["info"] = new JsonObject
            {
                ["media_count"] = count,
                ["title"] = " Favorites ",
                ["intro"] = " Introduction ",
                ["ctime"] = 100,
                ["upper"] = new JsonObject { ["name"] = "Owner" }
            },
            ["medias"] = new JsonArray(medias)
        }
    }.ToJsonString();

    private static JsonObject Media(int id, int pageCount = 1, int attr = 0) => new()
    {
        ["id"] = id,
        ["attr"] = attr,
        ["page"] = pageCount,
        ["title"] = $"Video {id}",
        ["intro"] = $"Media description {id}",
        ["ugc"] = new JsonObject { ["first_cid"] = id * 100 },
        ["duration"] = 10,
        ["pubtime"] = 130,
        ["cover"] = $"cover{id}.jpg",
        ["upper"] = new JsonObject { ["name"] = "Uploader", ["mid"] = 8 }
    };
}
