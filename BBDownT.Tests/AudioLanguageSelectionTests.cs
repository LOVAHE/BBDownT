using BBDownT.Core.Entity;
using static BBDownT.Core.Entity.Entity;

namespace BBDownT.Tests;

public class AudioLanguageSelectionTests
{
    [Fact]
    public async Task DefaultPlayback_DoesNotAddRequests()
    {
        var count = 0;
        var original = new ParsedResult();
        var actual = await AudioLanguageSelection.FetchAsync(null, language =>
        {
            Assert.Null(language);
            count++;
            return Task.FromResult(original);
        });

        Assert.Same(original, actual);
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task ExplicitLanguage_UsesCanonicalCodeAndReplacesBothVideoAndAudio()
    {
        var original = Languages();
        original.VideoTracks.Add(VideoTrack("original-video"));
        original.AudioTracks.Add(AudioTrack("original-audio"));
        var translated = new ParsedResult { CurrentAudioLanguage = "en-US" };
        translated.VideoTracks.Add(VideoTrack("translated-video"));
        translated.AudioTracks.Add(AudioTrack("translated-audio"));
        var requested = new List<string?>();

        var result = await AudioLanguageSelection.FetchAsync(" EN-us ", language =>
        {
            requested.Add(language);
            return Task.FromResult(language is null ? original : translated);
        });

        Assert.Equal(new string?[] { null, "en-US" }, requested);
        Assert.Same(translated, result);
        Assert.Equal("translated-video", Assert.Single(result.VideoTracks).baseUrl);
        Assert.Equal("translated-audio", Assert.Single(result.AudioTracks).baseUrl);
        Assert.Equal("zh-CN", result.DefaultAudioLanguage);
    }

    [Theory]
    [InlineData("en")]
    [InlineData("fr-FR")]
    public async Task MissingExactCode_FailsWithoutRequestingAnotherLanguage(string requested)
    {
        var calls = 0;
        var error = await Assert.ThrowsAsync<AudioLanguageUnavailableException>(() =>
            AudioLanguageSelection.FetchAsync(requested, _ =>
            {
                calls++;
                return Task.FromResult(Languages());
            }));

        Assert.Equal(1, calls);
        Assert.Contains("en-US", error.Message);
    }

    [Fact]
    public async Task MissingLanguageList_FailsClearly()
    {
        var error = await Assert.ThrowsAsync<AudioLanguageUnavailableException>(() =>
            AudioLanguageSelection.FetchAsync("en-US", _ => Task.FromResult(new ParsedResult())));

        Assert.Contains("接口未提供可选配音语言", error.Message);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("zh-CN")]
    public async Task UnconfirmedSelection_DoesNotFallBackToOriginal(string? returnedLanguage)
    {
        await Assert.ThrowsAsync<AudioLanguageUnavailableException>(() =>
            AudioLanguageSelection.FetchAsync("en-US", language => Task.FromResult(
                language is null ? Languages() : new ParsedResult { CurrentAudioLanguage = returnedLanguage })));
    }

    [Fact]
    public void Info_ListsCodesAiAndCurrentLanguage()
    {
        using var output = new StringWriter();
        AudioLanguageSelection.PrintAvailable(Languages(), output);

        Assert.Contains("可选配音语言：", output.ToString());
        Assert.Contains("zh-CN: 中文原声 [默认]", output.ToString());
        Assert.Contains("en-US: English [AI]", output.ToString());
        Assert.DoesNotContain("--audio-language", output.ToString());
        Assert.DoesNotContain("[当前]", output.ToString());
    }

    [Fact]
    public void Info_SelectedTranslationKeepsDefaultAndCurrentDistinct()
    {
        var result = Languages();
        result.CurrentAudioLanguage = "en-US";
        using var output = new StringWriter();

        AudioLanguageSelection.PrintAvailable(result, output);

        Assert.Contains("zh-CN: 中文原声 [默认]", output.ToString());
        Assert.Contains("en-US: English [AI] [当前]", output.ToString());
    }

    [Fact]
    public async Task ExplicitDefaultLanguage_IsStillRequestedAndConfirmed()
    {
        var requests = new List<string?>();
        await AudioLanguageSelection.FetchAsync("zh-CN", language =>
        {
            requests.Add(language);
            return Task.FromResult(Languages());
        });

        Assert.Equal(new string?[] { null, "zh-CN" }, requests);
    }

    [Fact]
    public async Task ProgressiveLanguageVersion_IsRejectedBeforeArtifactDownload()
    {
        var progressive = new ParsedResult { CurrentAudioLanguage = "en-US", Clips = ["https://cdn.test/video.flv"] };
        var error = await Assert.ThrowsAsync<AudioLanguageUnavailableException>(() =>
            AudioLanguageSelection.FetchAsync("en-US", language => Task.FromResult(language is null ? Languages() : progressive)));

        Assert.Contains("DASH", error.Message);
    }

    [Theory]
    [InlineData(null, "video.mp4")]
    [InlineData(" ", "video.mp4")]
    [InlineData("en-US", "video.audio-en-us.mp4")]
    [InlineData(" EN-us ", "video.audio-en-us.mp4")]
    [InlineData("zh-CN", "video.audio-zh-cn.mp4")]
    public void LanguageSuffix_SeparatesVariantsAndPreservesDefault(string? language, string expected)
    {
        Assert.Equal(Path.Combine("videos", expected),
            AudioLanguageSelection.WithLanguageSuffix(Path.Combine("videos", "video.mp4"), language));
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData("en-US", false)]
    public void AidOnlyArchive_DoesNotSkipOrRecordExplicitLanguage(string? language, bool expected)
    {
        Assert.Equal(expected, AudioLanguageSelection.UseAidArchive(
            new MyOption { SaveArchivesToFile = true, AudioLanguage = language }));
        Assert.False(AudioLanguageSelection.UseAidArchive(new MyOption { AudioLanguage = language }));
    }

    [Theory]
    [InlineData(null, true, "video.mp4")]
    [InlineData("en-US", false, "video.audio-en-us.mp4")]
    [InlineData("en-US", true, "video.audio-en-us.m4a")]
    public void OutputPath_UsesActualVariantExtensionBeforeCacheCheck(string? language, bool audioOnly, string expected)
    {
        Assert.Equal(expected, AudioLanguageSelection.OutputPath("video.mp4", language, audioOnly));
    }

    [Theory]
    [InlineData("en,zh")]
    [InlineData("../en")]
    [InlineData("en&qn=0")]
    [InlineData("中文")]
    public void InvalidCode_IsRejectedBeforeItCanBeUsedInPaths(string language)
    {
        Assert.NotNull(AudioLanguageSelection.ValidateCode(language));
        Assert.Throws<ArgumentException>(() => AudioLanguageSelection.WithLanguageSuffix("video.mp4", language));
    }

    private static ParsedResult Languages() => new()
    {
        CurrentAudioLanguage = "zh-CN",
        DefaultAudioLanguage = "zh-CN",
        AudioLanguages = [new("zh-CN", "中文原声", false), new("en-US", "English", true)]
    };

    private static Video VideoTrack(string url) => new() { id = "80", dfn = "1080P", codecs = "HEVC", baseUrl = url };
    private static Audio AudioTrack(string url) => new() { id = "30280", dfn = "M4A", codecs = "M4A", baseUrl = url, bandwith = 192, dur = 1 };
}
