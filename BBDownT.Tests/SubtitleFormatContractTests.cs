using System.CommandLine;
using System.Text.Json;

namespace BBDownT.Tests;

public class SubtitleFormatContractTests
{
    [Fact]
    public async Task Cli_ReusesExistingSubtitleFiltersAndInteractiveSelection()
    {
        MyOption? bound = null;
        var command = CommandLineInvoker.GetRootCommand(option =>
        {
            bound = option;
            return Task.CompletedTask;
        });
        var exit = await command.InvokeAsync([
            "https://www.bilibili.tv/play/2110869", "-intl", "-ia",
            "--subtitle-language", "en,th", "--ai-subtitle-policy", "include"
        ]);

        Assert.Equal(0, exit);
        Assert.NotNull(bound);
        Assert.True(bound.Interactive);
        Assert.Equal("en,th", bound.SubtitleLanguage);
        Assert.Equal("include", bound.AiSubtitlePolicy);
        Assert.True(bound.UseIntlApi);
    }

    [Fact]
    public void Api_AcceptsExistingSubtitleFiltersWithTheDefaultFormatSelection()
    {
        var request = new ServeRequestOptions
        {
            Url = "https://www.bilibili.tv/play/2110869", UseIntlApi = true,
            SubtitleLanguage = "en,th", AiSubtitlePolicy = "include"
        };

        Assert.Null(new BBDownTApiServer().ValidateAndNormalizeServerRequest(request));
    }

    [Fact]
    public void JsonAndBatchClone_PreserveExistingSubtitleFilters()
    {
        var original = new MyOption
        {
            Url = "https://www.bilibili.tv/play/2110869", UseIntlApi = true,
            SubtitleLanguage = "th", AiSubtitlePolicy = "include"
        };
        var json = JsonSerializer.Serialize(original, SourceGenerationContext.Default.MyOption);
        var restored = JsonSerializer.Deserialize(json, SourceGenerationContext.Default.MyOption);
        Assert.NotNull(restored);
        var batch = restored.ForBatchVideo("https://www.bilibili.tv/play/2110869/13287745", "/in-memory");

        Assert.Equal("th", batch.SubtitleLanguage);
        Assert.Equal("include", batch.AiSubtitlePolicy);
        Assert.True(batch.UseIntlApi);
        batch.SubtitleLanguage = "en";
        Assert.Equal("th", original.SubtitleLanguage);
        Assert.Equal("th", restored.SubtitleLanguage);
    }
}
