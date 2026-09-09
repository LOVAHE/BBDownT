using System.Text.Json;
using BBDownT.Core;
using BBDownT.Core.Entity;

namespace BBDownT.Tests;

public class AudioLanguageProtocolTests
{
    [Fact]
    public void Metadata_MapsLanguagesAndAiClassificationWithoutGuessingOriginal()
    {
        using var json = JsonDocument.Parse("""
            {"cur_language":"zh-CN","language":{"items":[
              {"lang":"zh-CN","title":"中文原声","production_type":1},
              {"lang":"en-US","title":"English","production_type":2},
              {"lang":"EN-US","title":"duplicate"},
              {"lang":"ja-JP"}, {}, null
            ]}}
            """);
        var result = new ParsedResult();

        AudioLanguageMapper.Map(json.RootElement, result);

        Assert.Equal("zh-CN", result.CurrentAudioLanguage);
        Assert.Equal("zh-CN", result.DefaultAudioLanguage);
        Assert.Equal(new[] { "zh-CN", "en-US", "ja-JP" }, result.AudioLanguages.Select(language => language.Code));
        Assert.False(result.AudioLanguages[0].IsAi);
        Assert.True(result.AudioLanguages[1].IsAi);
        Assert.Equal("ja-JP", result.AudioLanguages[2].Title);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"language\":null}")]
    [InlineData("{\"language\":{\"items\":null}}")]
    public void MissingMetadata_DoesNotBreakDefaultPlayback(string source)
    {
        using var json = JsonDocument.Parse(source);
        var result = new ParsedResult();

        AudioLanguageMapper.Map(json.RootElement, result);

        Assert.Null(result.CurrentAudioLanguage);
        Assert.Empty(result.AudioLanguages);
    }

    [Theory]
    [InlineData("{\"cur_language\":\"zh-CN\"}")]
    [InlineData("{}")]
    public void SelectedLanguage_RejectsFallbackOrUnconfirmedResponse(string source)
    {
        using var json = JsonDocument.Parse(source);

        Assert.Throws<AudioLanguageUnavailableException>(() =>
            AudioLanguageMapper.Map(json.RootElement, new ParsedResult(), "en-US"));
    }

    [Theory]
    [InlineData("BV", "/x/player/wbi/playurl", true)]
    [InlineData("ep:1", "/pgc/player/web/v2/playurl", false)]
    [InlineData("cheese:1", "/pugv/player/web/v2/playurl", false)]
    public async Task WebRequest_SendsEncodedLanguageBeforeSigning(string input, string path, bool signed)
    {
        string? requestedUrl = null;
        await Parser.GetPlayJsonAsync("", input, "1", "2", "3", false, false, false,
            "129", "en-US&qn=0", url =>
            {
                requestedUrl = url;
                return Task.FromResult("{}");
            });

        Assert.NotNull(requestedUrl);
        Assert.Equal(path, new Uri(requestedUrl).AbsolutePath);
        Assert.Contains("&qn=129", requestedUrl);
        Assert.Contains("&cur_language=en-US%26qn%3D0&wts=", requestedUrl);
        Assert.Equal(signed, requestedUrl.Contains("&w_rid="));
    }

    [Fact]
    public async Task DefaultWebRequest_DoesNotSendLanguageParameter()
    {
        await Parser.GetPlayJsonAsync("", "BV", "1", "2", "", false, false, false,
            fetchWeb: url =>
            {
                Assert.DoesNotContain("cur_language", url);
                return Task.FromResult("{}");
            });
    }

    [Theory]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    public async Task NonWebSelection_FailsBeforeRequest(bool tv, bool intl, bool app)
    {
        await Assert.ThrowsAsync<ArgumentException>(() => Parser.GetPlayJsonAsync(
            "", "BV", "1", "2", "", tv, intl, app, "0", "en-US",
            _ => throw new Exception("No request should be sent")));
    }

    [Fact]
    public async Task SelectedLanguage_ValidatesHighestQualityRefetchBeforeMerging()
    {
        var responses = new Queue<string>([Fixture("en-US"), Fixture("zh-CN")]);

        await Assert.ThrowsAsync<AudioLanguageUnavailableException>(() => Parser.ExtractTracksWithFetcherAsync(
            "BV", "1", "2", "", false, false, false, "0",
            _ => Task.FromResult(responses.Dequeue()),
            (_, _) => throw new Exception("No intl request expected"), "en-US"));

        Assert.Empty(responses);
    }

    [Fact]
    public async Task SelectedLanguage_MapsAudioAndVideoAcrossQualityRefetch()
    {
        var qualities = new List<string>();
        var result = await Parser.ExtractTracksWithFetcherAsync(
            "BV", "1", "2", "", false, false, false, "0",
            qn => { qualities.Add(qn); return Task.FromResult(Fixture("en-US")); },
            (_, _) => throw new Exception("No intl request expected"), "en-US");

        Assert.Equal(new[] { "0", "129" }, qualities);
        Assert.Equal("en-US", result.CurrentAudioLanguage);
        Assert.Equal("en-US", Assert.Single(result.AudioLanguages).Code);
        Assert.Equal("https://cdn.test/en-US-video.m4s", Assert.Single(result.VideoTracks).baseUrl);
        Assert.Equal("https://cdn.test/en-US-audio.m4s", Assert.Single(result.AudioTracks).baseUrl);
    }

    private static string Fixture(string language) => $$$"""
        {"data":{
          "cur_language":"{{{language}}}","language":{"items":[{"lang":"{{{language}}}","title":"Voice","production_type":2}]},
          "dash":{"duration":1,
            "video":[{"id":80,"base_url":"https://cdn.test/{{{language}}}-video.m4s","backup_url":[],
                      "bandwidth":3000000,"codecid":12,"width":1920,"height":1080,"frame_rate":"30"}],
            "audio":[{"id":30280,"base_url":"https://cdn.test/{{{language}}}-audio.m4s","backup_url":[],
                      "bandwidth":192000,"codecs":"mp4a.40.2"}]}
        }}
        """;
}
