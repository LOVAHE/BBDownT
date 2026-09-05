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

    [Fact]
    public void JsonParser_PreservesMetadataAndRejectsWrongLegacyPage()
    {
        const string json = """
            {"data":{"cid":456,"subtitle":{"list":[
              {"id":9007199254740993,"lan":"ai-zh","lan_doc":"Chinese","type":"1","ai_type":1,"subtitle_url":"//cdn.test/ai.json"},
              {"lan":"en","subtitle_url":"//cdn.test/en.json"}
            ]}}}
            """;
        var tracks = SubUtil.ParseJsonSubtitles(json, "list", "456");
        Assert.Equal("9007199254740993", tracks[0].id);
        Assert.True(tracks[0].IsAi);
        Assert.Equal(1, tracks[0].aiType);
        Assert.Null(tracks[1].type);
        Assert.Empty(SubUtil.ParseJsonSubtitles(json, "list", "789"));
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
