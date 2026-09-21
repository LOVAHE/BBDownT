using BBDownT.Core.Util;
using static BBDownT.Core.Entity.Entity;

namespace BBDownT.Tests;

public class SubtitleSourceTests
{
    [Fact]
    public void ParseSubtitleWebResponse_ReadsIdsAndOptionalMetadataFromWireFields()
    {
        var aiTrack = Track(
            VarintField(1, 123),
            StringField(2, "123"),
            StringField(3, "ai-zh"),
            StringField(4, "中文"),
            StringField(5, "https://subtitle.example/ai.json"),
            VarintField(7, 1),
            StringField(8, "中文"),
            VarintField(9, 0));
        var ccTrack = Track(
            StringField(2, "456"),
            StringField(3, "en-US"),
            StringField(5, "https://subtitle.example/en.json"),
            VarintField(7, 0),
            VarintField(9, 0));
        var videoSubtitle = Field(3, aiTrack, ccTrack);
        var parsed = SubUtil.ParseSubtitleWebResponse(Field(1, videoSubtitle));

        Assert.Equal(2, parsed.Count);
        Assert.Equal("123", parsed[0].id);
        Assert.Equal("中文", parsed[0].lanDoc);
        Assert.Equal(1, parsed[0].type);
        Assert.Equal(0, parsed[0].aiType);
        Assert.True(parsed[0].IsAi);
        Assert.Equal("456", parsed[1].id);
        Assert.Equal(0, parsed[1].type);
        Assert.Equal(0, parsed[1].aiType);
        Assert.False(parsed[1].IsAi);
    }

    [Fact]
    public void ParseSubtitleWebResponse_UsesProtoDefaultCcTypeWhenTypeIsOmitted()
    {
        var track = Track(StringField(2, "789"), StringField(3, "en-US"), StringField(5, "https://subtitle.example/en.json"));
        var parsed = SubUtil.ParseSubtitleWebResponse(Field(1, Field(3, track)));

        Assert.Equal(0, parsed[0].type);
        Assert.Null(parsed[0].aiType);
        Assert.False(parsed[0].IsAi);
    }

    [Fact]
    public void MergeSubtitleSources_DeduplicatesAndKeepsUsableTracks()
    {
        var first = new Subtitle { id = "7", lan = "zh", url = "//subtitle.example/a.json", path = "" };
        var duplicate = new Subtitle { id = "7", lan = "zh", url = "https://subtitle.example/other.json", path = "" };
        var empty = new Subtitle { id = "8", lan = "en", url = "", path = "" };
        var second = new Subtitle { lan = "en", url = "https://subtitle.example/en.json", path = "" };

        var merged = SubUtil.MergeSubtitleSources([[first, empty], [duplicate, second]], "123", "456");

        Assert.Equal(2, merged.Count);
        Assert.Equal("https://subtitle.example/a.json", merged[0].url);
        Assert.Equal("123/123.456.zh.srt", merged[0].path);
        Assert.Equal("123/123.456.en.srt", merged[1].path);
    }

    private static byte[] Track(params byte[][] fields) => fields.SelectMany(field => field).ToArray();

    [Fact]
    public async Task DomesticSource_DecodesNewApiAndPreservesTrackMetadata()
    {
        var track = Track(StringField(2, "2111863458441397760"), StringField(3, "zh"),
            StringField(4, "中文"), StringField(5, "//subtitle.bilibili.com/" + SubtitleUrlResolverTests.EncodedPath + "?auth_key=test"));
        var handler = new SubtitleHandler(Field(1, Field(3, track)));
        using var client = new HttpClient(handler);

        var subtitle = Assert.Single(await SubUtil.GetDomesticSubtitlesAsync("123", "456", client));

        handler.AssertOnlyNewEndpoint();
        Assert.Equal("2111863458441397760", subtitle.id);
        Assert.Equal("zh", subtitle.lan);
        Assert.Equal("中文", subtitle.lanDoc);
        Assert.Equal(0, subtitle.type);
        Assert.False(subtitle.IsAi);
        Assert.Equal(SubtitleUrlResolverTests.CdnUrl + "?auth_key=test", subtitle.url);
        Assert.Equal("123/123.456.zh.srt", subtitle.path);
        Assert.Equal(("chi", "中文"), SubUtil.GetSubtitleCode(subtitle.lan));
    }

    [Fact]
    public void Merge_UnsupportedEncodingDoesNotHideAnotherUsableTrackWithSameId()
    {
        var broken = new Subtitle { id = "7", lan = "zh", url = "//subtitle.bilibili.com/unknown", path = "" };
        var valid = new Subtitle { id = "7", lan = "zh", url = SubtitleUrlResolverTests.CdnUrl, path = "" };

        var subtitle = Assert.Single(SubUtil.MergeSubtitleSources([[broken, valid]], "123", "456"));

        Assert.Same(valid, subtitle);
    }

    [Fact]
    public void Merge_PreservesAssAndAvoidsIdOrLanguageCollisions()
    {
        var subtitles = Enumerable.Range(0, 4).Select(i => new Subtitle
        {
            lan = i == 3 ? "EN" : "en", url = $"https://subtitle.example/{i}.ass", path = "old.ass",
            id = i == 0 ? "2" : i == 2 ? "2.2" : null
        }).ToList();
        var merged = SubUtil.MergeSubtitleSources([subtitles], "123", "456");

        Assert.Equal(4, merged.Select(s => s.path).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(merged, s => Assert.EndsWith(".ass", s.path));
    }

    [Theory]
    [InlineData("0A00")]
    [InlineData("")]
    public async Task DomesticSource_EmptyResponseDoesNotCallLegacyApis(string hex)
    {
        var handler = new SubtitleHandler(Convert.FromHexString(hex));
        using var client = new HttpClient(handler);

        var subtitles = await SubUtil.GetDomesticSubtitlesAsync("123", "456", client);

        Assert.Empty(subtitles);
        handler.AssertOnlyNewEndpoint();
    }

    [Theory]
    [InlineData(200, "{\"code\":-101,\"message\":\"not logged in\"}")]
    [InlineData(200, "invalid protobuf")]
    [InlineData(412, "request was banned")]
    public async Task DomesticSource_FailedResponseDoesNotCallLegacyApis(int status, string body)
    {
        var handler = new SubtitleHandler(System.Text.Encoding.UTF8.GetBytes(body), status);
        using var client = new HttpClient(handler);

        Assert.Empty(await SubUtil.GetDomesticSubtitlesAsync("123", "456", client));
        handler.AssertOnlyNewEndpoint();
    }

    private sealed class SubtitleHandler(byte[] payload, int status = 200) : HttpMessageHandler
    {
        private readonly List<(HttpMethod Method, Uri Uri, string Accept)> requests = [];

        public void AssertOnlyNewEndpoint()
        {
            var request = Assert.Single(requests);
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("api.bilibili.com", request.Uri.Host);
            Assert.Equal("/x/v2/subtitle/web/view", request.Uri.AbsolutePath);
            Assert.Contains("oid=456&pid=123", request.Uri.Query);
            Assert.Contains("application/octet-stream", request.Accept);
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            requests.Add((request.Method, request.RequestUri!, request.Headers.Accept.ToString()));
            return Task.FromResult(new HttpResponseMessage((System.Net.HttpStatusCode)status)
            {
                Content = new ByteArrayContent(payload)
            });
        }
    }

    private static byte[] Field(int number, params byte[][] values)
    {
        return values.SelectMany(value => LengthDelimitedField(number, value)).ToArray();
    }

    private static byte[] StringField(int number, string value) => Field(number, System.Text.Encoding.UTF8.GetBytes(value));

    private static byte[] VarintField(int number, long value) => [(byte)(number << 3), (byte)value];

    private static byte[] LengthPrefix(int length)
    {
        var prefix = new List<byte>();
        do
        {
            byte next = (byte)(length & 0x7f);
            length >>= 7;
            prefix.Add(length == 0 ? next : (byte)(next | 0x80));
        } while (length != 0);
        return prefix.ToArray();
    }

    private static byte[] LengthDelimitedField(int number, byte[] value)
    {
        var field = new List<byte> { (byte)(number << 3 | 2) };
        field.AddRange(LengthPrefix(value.Length));
        field.AddRange(value);
        return field.ToArray();
    }
}
