using System.CommandLine;
using System.Text.Json;

namespace BBDownT.Tests;

public class AudioLanguageOptionsTests
{
    [Fact]
    public async Task Cli_BindsPlaybackLanguageSeparatelyFromMuxLanguage()
    {
        MyOption? selected = null;
        var command = CommandLineInvoker.GetRootCommand(option =>
        {
            selected = option;
            return Task.CompletedTask;
        });

        Assert.Equal(0, await command.InvokeAsync(["BV1xx411c7mD", "--audio-language", "en-US", "--language", "eng", "-info"]));
        Assert.NotNull(selected);
        Assert.Equal("en-US", selected.AudioLanguage);
        Assert.Equal("eng", selected.Language);
        Assert.True(selected.OnlyShowInfo);
    }

    [Fact]
    public async Task Cli_InvalidLanguageDoesNotRunWork()
    {
        var command = CommandLineInvoker.GetRootCommand(_ => throw new Exception("Must not run"));
        Assert.NotEqual(0, await command.InvokeAsync(["BV1xx411c7mD", "--audio-language", "en,zh"]));
    }

    [Fact]
    public async Task ConfigFile_BindsAudioLanguageThroughTheSameCommand()
    {
        var path = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(path, "--audio-language en-US\n");
            MyOption? selected = null;
            var command = CommandLineInvoker.GetRootCommand(option => { selected = option; return Task.CompletedTask; });
            var args = new List<string> { "BV1xx411c7mD", "--config-file", path };

            Assert.True(BBDownTConfigParser.HandleConfig(args, command));
            Assert.Equal(0, await command.InvokeAsync(args.ToArray()));
            Assert.Equal("en-US", selected?.AudioLanguage);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ApiAndBatch_PreserveLanguageWithoutChangingMuxMetadata()
    {
        var request = JsonSerializer.Deserialize("""
            {"Url":"BV1xx411c7mD","AudioLanguage":"en-US","Language":"eng","OnlyShowInfo":true}
            """, SourceGenerationContext.Default.ServeRequestOptions)!;
        var server = new BBDownTApiServer();

        Assert.Null(server.ValidateAndNormalizeServerRequest(request));
        Assert.Equal("en-US", request.AudioLanguage);
        Assert.Equal("eng", request.Language);
        var copy = request.ForBatchVideo("https://www.bilibili.com/video/av1", Environment.CurrentDirectory);
        Assert.Equal("en-US", copy.AudioLanguage);
        Assert.Equal("eng", copy.Language);
        Assert.Contains("\"AudioLanguage\":\"en-US\"",
            JsonSerializer.Serialize(request, SourceGenerationContext.Default.ServeRequestOptions));
    }

    [Theory]
    [InlineData("tv")]
    [InlineData("app")]
    [InlineData("intl")]
    [InlineData("subtitle")]
    [InlineData("cover")]
    [InlineData("danmaku")]
    public void UnsupportedModes_RejectExplicitLanguageAtCliAndApiBoundaries(string mode)
    {
        var request = new ServeRequestOptions
        {
            Url = "BV1xx411c7mD", AudioLanguage = "en-US",
            UseTvApi = mode == "tv", UseAppApi = mode == "app", UseIntlApi = mode == "intl",
            SubOnly = mode == "subtitle", CoverOnly = mode == "cover", DanmakuOnly = mode == "danmaku"
        };

        Assert.NotNull(AudioLanguageSelection.ValidateOptions(request));
        Assert.Throws<ArgumentException>(() => Program.SetUpWork(request));
        Assert.NotNull(new BBDownTApiServer().ValidateAndNormalizeServerRequest(request));

        request.AudioLanguage = null;
        Assert.Null(AudioLanguageSelection.ValidateOptions(request));
    }
}
