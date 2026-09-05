using System.CommandLine;
using System.Text.Json;
using static BBDownT.Core.Entity.Entity;

namespace BBDownT.Tests;

public class SubtitleSelectionTests
{
    [Fact]
    public void DefaultPolicy_ExcludesAiIncludingUnknownAiPrefix()
    {
        var selected = SubtitleSelection.Filter(Tracks(), new MyOption());

        Assert.Equal(["zh-Hans", "en", "en-GB"], selected.Select(s => s.lan));
    }

    [Theory]
    [InlineData("exclude", "zh-Hans,en,en-GB")]
    [InlineData("include", "zh-Hans,ai-zh,en,ai-en,en-GB,ai-fr")]
    [InlineData("prefer-human", "zh-Hans,en,en-GB,ai-fr")]
    [InlineData("only", "ai-zh,ai-en,ai-fr")]
    public void ExplicitAiPolicy_OverridesLegacyFlagAndFiltersAllPolicies(string policy, string expected)
    {
        var selected = SubtitleSelection.Filter(Tracks(), new MyOption
        {
            SkipAi = false,
            AiSubtitlePolicy = policy
        });

        Assert.Equal(expected.Split(','), selected.Select(s => s.lan));
    }

    [Fact]
    public void PreferHuman_OnlyConfirmedCcInSameFamilyDisplacesAiAfterLanguageFilter()
    {
        var tracks = new[]
        {
            Track("en-GB", type: null),
            Track("ai-en", type: 1),
            Track("ai-fr", type: 1)
        };

        var selected = SubtitleSelection.Filter(tracks, new MyOption
        {
            SubtitleLanguage = "en,fr",
            AiSubtitlePolicy = "prefer-human"
        });

        Assert.Equal(["en-GB", "ai-en", "ai-fr"], selected.Select(s => s.lan));
    }

    [Fact]
    public void PreferHuman_ExactAiLanguageDoesNotConsiderNonSelectedCc()
    {
        var selected = SubtitleSelection.Filter(Tracks(), new MyOption
        {
            SubtitleLanguage = "ai-zh",
            AiSubtitlePolicy = "prefer-human"
        });

        Assert.Equal(["ai-zh"], selected.Select(s => s.lan));
    }

    [Theory]
    [InlineData("ZH", "zh-Hans,ai-zh")]
    [InlineData("zh-Hans", "zh-Hans")]
    [InlineData("AI-ZH", "ai-zh")]
    [InlineData("en", "en,ai-en,en-GB")]
    public void LanguageFilter_UsesCaseInsensitiveFamilyOrExactMatching(string language, string expected)
    {
        var selected = SubtitleSelection.Filter(Tracks(), new MyOption
        {
            SubtitleLanguage = language,
            AiSubtitlePolicy = "include"
        });

        Assert.Equal(expected.Split(','), selected.Select(s => s.lan));
    }

    [Fact]
    public async Task CommandLine_BindsSkipAiFalseAndNewOptions()
    {
        MyOption? option = null;
        var command = CommandLineInvoker.GetRootCommand(bound =>
        {
            option = bound;
            return Task.CompletedTask;
        });

        var exitCode = await command.InvokeAsync([
            "BV1xx411c7mD", "--skip-ai", "false", "--subtitle-language", "zh,en",
            "--ai-subtitle-policy", "include"
        ]);

        Assert.Equal(0, exitCode);
        Assert.NotNull(option);
        Assert.False(option.SkipAi);
        Assert.Equal("zh,en", option.SubtitleLanguage);
        Assert.Equal("include", option.AiSubtitlePolicy);
    }

    [Theory]
    [InlineData("--subtitle-language", "zh,,en")]
    [InlineData("--subtitle-language", "")]
    [InlineData("--ai-subtitle-policy", "human-first")]
    public async Task InvalidSubtitleOptions_FailCliBeforeCallback(string name, string value)
    {
        var invoked = false;
        var command = CommandLineInvoker.GetRootCommand(_ =>
        {
            invoked = true;
            return Task.CompletedTask;
        });

        var exitCode = await command.InvokeAsync(["BV1xx411c7mD", name, value]);

        Assert.NotEqual(0, exitCode);
        Assert.False(invoked);
    }

    [Theory]
    [InlineData("zh,,en", null)]
    [InlineData("", null)]
    [InlineData(null, "not-a-policy")]
    public void InvalidSubtitleOptions_FailApiValidation(string? language, string? policy)
    {
        var server = new BBDownTApiServer();

        var result = server.ValidateAndNormalizeServerRequest(new ServeRequestOptions
        {
            Url = "BV1xx411c7mD",
            SubtitleLanguage = language,
            AiSubtitlePolicy = policy
        });

        Assert.NotNull(result);
    }

    [Fact]
    public void InfoMode_PrintsEveryFoundTrackWithSelectionWithoutReadingInput()
    {
        using var output = new StringWriter();
        var selected = SubtitleSelection.Choose(Tracks(), new MyOption
        {
            OnlyShowInfo = true,
            Interactive = true,
            SubtitleLanguage = "zh",
            AiSubtitlePolicy = "exclude"
        }, new ThrowingReader(), output);

        Assert.Equal(["zh-Hans"], selected.Select(s => s.lan));
        var text = output.ToString();
        Assert.Contains("发现 6 条，符合筛选 1 条", text);
        Assert.Contains("1. zh-Hans", text);
        Assert.Contains("2. ai-zh", text);
        Assert.Contains("已选", text);
        Assert.Contains("未选", text);
    }

    [Fact]
    public void InteractiveMode_AcceptsRangesAllNoneAndDefaultAndRepromptsInvalidInput()
    {
        Assert.Equal(["zh-Hans", "en"], ChooseInteractive("1-2"));
        Assert.Equal(["zh-Hans", "en", "en-GB"], ChooseInteractive("ALL"));
        Assert.Empty(ChooseInteractive("NONE"));
        Assert.Equal(["zh-Hans", "en", "en-GB"], ChooseInteractive(""));

        using var output = new StringWriter();
        var repaired = SubtitleSelection.Choose(Tracks(), new MyOption { Interactive = true },
            new StringReader("0\n2-3\n"), output);
        Assert.Equal(["en", "en-GB"], repaired.Select(s => s.lan));
        Assert.Contains("字幕序号无效", output.ToString());
    }

    [Fact]
    public void ApiJsonContext_RoundTripsSubtitleOptions()
    {
        var option = new MyOption
        {
            Url = "BV1xx411c7mD",
            SkipAi = false,
            SubtitleLanguage = "zh-Hans,ai-zh",
            AiSubtitlePolicy = "only"
        };

        var json = JsonSerializer.Serialize(option, SourceGenerationContext.Default.MyOption);
        var restored = JsonSerializer.Deserialize(json, SourceGenerationContext.Default.MyOption);

        Assert.NotNull(restored);
        Assert.False(restored.SkipAi);
        Assert.Equal(option.SubtitleLanguage, restored.SubtitleLanguage);
        Assert.Equal(option.AiSubtitlePolicy, restored.AiSubtitlePolicy);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public void ApiInteractive_IsOnlyAllowedForNonInteractiveInfoMode(bool infoOnly, bool accepted)
    {
        var error = new BBDownTApiServer().ValidateAndNormalizeServerRequest(new ServeRequestOptions
        {
            Url = "BV1xx411c7mD", SubOnly = true, Interactive = true, OnlyShowInfo = infoOnly
        });
        Assert.Equal(accepted, error is null);
    }

    private static IEnumerable<string> ChooseInteractive(string answer)
    {
        return SubtitleSelection.Choose(Tracks(), new MyOption { Interactive = true },
                new StringReader(answer + "\n"), TextWriter.Null)
            .Select(s => s.lan);
    }

    private static List<Subtitle> Tracks() =>
    [
        Track("zh-Hans", type: 0),
        Track("ai-zh", type: 1),
        Track("en", type: 0),
        Track("ai-en", type: 1),
        Track("en-GB", type: null),
        Track("ai-fr", type: null)
    ];

    private static Subtitle Track(string language, int? type) => new()
    {
        lan = language,
        url = "https://example.test/" + language,
        path = language + ".srt",
        lanDoc = language,
        type = type
    };

    private sealed class ThrowingReader : TextReader
    {
        public override string? ReadLine() => throw new Xunit.Sdk.XunitException("Info mode read input.");
    }
}
