using BBDownT.Core.Entity;
using BBDownT.Core.Fetcher;

namespace BBDownT.Tests;

public class MediaListFetcherCoreTests
{
    [Theory]
    [InlineData(8, false, false, "desc=false", false)]
    [InlineData(5, true, true, "desc=true", true)]
    public async Task Fixture_MapsPagesAndPreservesProtocolDifferences(
        int type,
        bool descending,
        bool includeBvid,
        string expectedDescending,
        bool expectedBvid)
    {
        var urls = new List<string>();
        var responses = new Queue<string>([InfoFixture, ListFixture]);

        var result = await MediaListFetcherCore.FetchAsync(
            "42",
            type,
            descending,
            includeBvid,
            type == 8 ? "合集" : "系列",
            url =>
            {
                urls.Add(url);
                return Task.FromResult(responses.Dequeue());
            });

        Assert.Contains($"type={type}", urls[0]);
        Assert.Contains(expectedDescending, urls[1]);
        Assert.Equal(expectedBvid, urls[1].Contains("&bvid="));
        Assert.Equal("List title", result.Title);
        Assert.Equal("Description", result.Desc);
        Assert.Equal(123, result.PubTime);
        Assert.Collection(
            result.PagesInfo,
            page =>
            {
                Assert.Equal(1, page.index);
                Assert.Equal("Video_P1_First", page.title);
                Assert.Equal("1920x1080", page.res);
            },
            page =>
            {
                Assert.Equal(2, page.index);
                Assert.Equal("Video_P2_Second", page.title);
                Assert.Equal("1280x720", page.res);
            });
    }

    [Fact]
    public async Task InvalidCollectionInfo_UsesSeriesFallback()
    {
        var fallback = new VInfo
        {
            Title = "fallback",
            Desc = "",
            Pic = "",
            PubTime = 0,
            PagesInfo = []
        };

        var result = await MediaListFetcherCore.FetchAsync(
            "42",
            8,
            false,
            false,
            "合集",
            _ => Task.FromResult("{\"code\":-404,\"message\":\"not found\",\"data\":null}"),
            () => Task.FromResult(fallback));

        Assert.Same(fallback, result);
    }

    [Fact]
    public async Task InvalidListResponse_ReportsApiError()
    {
        var responses = new Queue<string>(
        [
            InfoFixture,
            "{\"code\":-403,\"message\":\"forbidden\",\"data\":null}"
        ]);

        var exception = await Assert.ThrowsAsync<Exception>(() =>
            MediaListFetcherCore.FetchAsync(
                "42",
                5,
                true,
                true,
                "系列",
                _ => Task.FromResult(responses.Dequeue())));

        Assert.Contains("获取系列视频列表失败(code=-403): forbidden", exception.Message);
    }

    [Fact]
    public async Task Pagination_AdvancesPastInvalidItemAndDeduplicatesAcrossPages()
    {
        var urls = new List<string>();
        var responses = new Queue<string>([InfoFixture, FirstPagedListFixture, SecondPagedListFixture]);

        var result = await MediaListFetcherCore.FetchAsync(
            "42",
            8,
            false,
            false,
            "合集",
            url =>
            {
                urls.Add(url);
                return Task.FromResult(responses.Dequeue());
            });

        Assert.Contains("oid=9", urls[2]);
        Assert.Collection(
            result.PagesInfo,
            page =>
            {
                Assert.Equal(1, page.index);
                Assert.Equal("2", page.aid);
            },
            page =>
            {
                Assert.Equal(2, page.index);
                Assert.Equal("3", page.aid);
            });
    }

    [Fact]
    public async Task Pagination_RejectsHasMoreWithoutCursorAdvance()
    {
        var responses = new Queue<string>(
        [
            InfoFixture,
            "{\"code\":0,\"data\":{\"has_more\":true,\"media_list\":[]}}"
        ]);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            MediaListFetcherCore.FetchAsync(
                "42",
                8,
                false,
                false,
                "合集",
                _ => Task.FromResult(responses.Dequeue())));
    }

    private const string InfoFixture = """
        {
          "code": 0,
          "data": {
            "title": "  List title  ",
            "intro": "  Description  ",
            "ctime": 123
          }
        }
        """;

    private const string ListFixture = """
        {
          "code": 0,
          "data": {
            "has_more": false,
            "media_list": [{
              "id": 2,
              "attr": 0,
              "page": 2,
              "title": "Video",
              "intro": "Video description",
              "pubtime": 456,
              "cover": "https://cdn.test/cover.jpg",
              "upper": { "name": "Uploader", "mid": 7 },
              "pages": [
                {
                  "id": 3,
                  "page": 1,
                  "title": "First",
                  "duration": 10,
                  "dimension": { "width": 1920, "height": 1080 }
                },
                {
                  "id": 4,
                  "page": 2,
                  "title": "Second",
                  "duration": 20,
                  "dimension": { "width": 1280, "height": 720 }
                }
              ]
            }]
          }
        }
        """;

    private const string FirstPagedListFixture = """
        {
          "code": 0,
          "data": {
            "has_more": true,
            "media_list": [
              {
                "id": 2,
                "attr": 0,
                "page": 1,
                "title": "First video",
                "intro": "Description",
                "pubtime": 456,
                "cover": "cover",
                "upper": { "name": "Uploader", "mid": 7 },
                "pages": [{
                  "id": 20,
                  "page": 1,
                  "title": "Page",
                  "duration": 10,
                  "dimension": { "width": 1920, "height": 1080 }
                }]
              },
              { "id": 9, "attr": 1 }
            ]
          }
        }
        """;

    private const string SecondPagedListFixture = """
        {
          "code": 0,
          "data": {
            "has_more": false,
            "media_list": [
              {
                "id": 2,
                "attr": 0,
                "page": 1,
                "title": "First video",
                "intro": "Description",
                "pubtime": 456,
                "cover": "cover",
                "upper": { "name": "Uploader", "mid": 7 },
                "pages": [{
                  "id": 20,
                  "page": 1,
                  "title": "Page",
                  "duration": 10,
                  "dimension": { "width": 1920, "height": 1080 }
                }]
              },
              {
                "id": 3,
                "attr": 0,
                "page": 1,
                "title": "Second video",
                "intro": "Description",
                "pubtime": 789,
                "cover": "cover",
                "upper": { "name": "Uploader", "mid": 7 },
                "pages": [{
                  "id": 30,
                  "page": 1,
                  "title": "Page",
                  "duration": 10,
                  "dimension": { "width": 1280, "height": 720 }
                }]
              }
            ]
          }
        }
        """;
}
