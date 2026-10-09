namespace BBDownT.Tests;

public class CommandLineEntryTests
{
    [Fact]
    public async Task StandaloneVersion_ExitsSuccessfullyWithoutMigrationOrDownload()
    {
        var result = await Invoke(["--version"]);

        Assert.Equal(0, result.ExitCode);
        Assert.Matches(@"^\d+\.\d+\.\d+", result.Output.Trim());
        Assert.Equal("", result.Error);
        Assert.Equal(0, result.MigrationCalls);
        Assert.Equal(0, result.DownloadCalls);
    }

    [Theory]
    [InlineData("--help")]
    [InlineData("-h")]
    [InlineData("-?")]
    public async Task HelpAliases_KeepWorkingWithoutStartingADownload(string alias)
    {
        var result = await Invoke([alias], config: "");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("--download-all", result.Output);
        Assert.Contains("--delay-per-video", result.Output);
        Assert.Contains("--migrate", result.Output);
        Assert.Contains("--update", result.Output);
        Assert.Equal(0, result.MigrationCalls);
        Assert.Equal(0, result.UpdateCalls);
        Assert.Equal(0, result.DownloadCalls);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("url-and-unknown")]
    [InlineData("combined-version")]
    public async Task InvalidArguments_RemainRejected(string scenario)
    {
        string[] args = scenario switch
        {
            "missing" => [],
            "url-and-unknown" => ["fixture", "--unknown-option"],
            "combined-version" => ["fixture", "--version"],
            _ => throw new InvalidOperationException()
        };
        var result = await Invoke(args, config: "");

        Assert.Equal(1, result.ExitCode);
        Assert.NotEmpty(result.Error);
        Assert.Equal(0, result.DownloadCalls);
    }

    [Fact]
    public async Task UnknownFirstToken_StillReachesTheExistingUrlValidation()
    {
        // System.CommandLine binds an otherwise-unrecognized first token to
        // the string URL argument. URL validation belongs to the work handler.
        var result = await Invoke(["--unknown-option"], config: "");

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(1, result.DownloadCalls);
        Assert.NotNull(result.Option);
        Assert.Equal("--unknown-option", result.Option.Url);
    }

    [Fact]
    public async Task NormalInvocation_PreservesConfigMergingAndBindingWithoutMigration()
    {
        var result = await Invoke(
            ["fixture", "--simply-mux", "--delay-per-video", "7", "--select-page", "2"],
            config: "--hide-streams\n");

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(0, result.MigrationCalls);
        Assert.Equal(1, result.DownloadCalls);
        Assert.NotNull(result.Option);
        Assert.Equal("fixture", result.Option.Url);
        Assert.True(result.Option.SimplyMux);
        Assert.True(result.Option.HideStreams);
        Assert.Equal(7, result.Option.DelayPerVideo);
        Assert.Equal("2", result.Option.SelectPage);
        Assert.True(result.Option.SelectPageSpecified);
    }

    [Fact]
    public async Task DownloadInvocation_IgnoresServeOnlyOptionsInSharedConfig()
    {
        var result = await Invoke(["fixture"], config: "--hide-streams\n--server-max-queue\n5\n--listen\nhttp://127.0.0.1:1\n--server-allow-custom-output\n");

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(1, result.DownloadCalls);
        Assert.NotNull(result.Option);
        Assert.Equal("fixture", result.Option.Url);
        Assert.True(result.Option.HideStreams);
    }

    [Fact]
    public async Task CombinedOptions_ReachTheWorkHandlerAndOverrideConfig()
    {
        var result = await Invoke([
            "https://space.bilibili.com/42", "--download-all", "--delay-per-video=15",
            "--delay-per-page", "2", "--audio-only", "--multi-thread=false", "--force-http=false",
            "--skip-ai=false", "--subtitle-language", "zh,en", "--ai-subtitle-policy", "include",
            "-e", "hevc,avc", "-q", "1080P 高清", "-p", "2",
            "--work-dir", "downloads", "--file-pattern", "<bvid>[<dfn>]"
        ], config: "--delay-per-video\n99\n--multi-thread\ntrue\n");

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(1, result.DownloadCalls);
        var option = Assert.IsType<MyOption>(result.Option);
        Assert.True(option.DownloadAll);
        Assert.Equal(15, option.DelayPerVideo);
        Assert.Equal("2", option.DelayPerPage);
        Assert.True(option.AudioOnly);
        Assert.False(option.MultiThread);
        Assert.False(option.ForceHttp);
        Assert.False(option.SkipAi);
        Assert.Equal("zh,en", option.SubtitleLanguage);
        Assert.Equal("include", option.AiSubtitlePolicy);
        Assert.Equal("hevc,avc", option.EncodingPriority);
        Assert.Equal("1080P 高清", option.DfnPriority);
        Assert.True(option.EncodingPriorityFirst);
        Assert.Equal("2", option.SelectPage);
        Assert.Equal("downloads", option.WorkDir);
        Assert.Equal("<bvid>[<dfn>]", option.FilePattern);
    }

    [Theory]
    [InlineData("--migrate", 0)]
    [InlineData("--migrate", 1)]
    [InlineData("--update", 0)]
    [InlineData("--update", 1)]
    public async Task MigrationAndUpdate_AreExplicitAndReturnTheirResult(string flag, int exitCode)
    {
        var result = await Invoke([flag], migrationExitCode: exitCode, updateExitCode: exitCode);

        Assert.Equal(exitCode, result.ExitCode);
        Assert.Equal(flag == "--migrate" ? 1 : 0, result.MigrationCalls);
        Assert.Equal(flag == "--update" ? 1 : 0, result.UpdateCalls);
        Assert.Equal(0, result.DownloadCalls);
    }

    [Theory]
    [InlineData("--migrate\n")]
    [InlineData("--update\n")]
    public async Task StandaloneCommandsInConfig_AreRejectedWithoutRunning(string config)
    {
        var result = await Invoke(["fixture"], config: config);

        Assert.Equal(1, result.ExitCode);
        Assert.Equal(0, result.UpdateCalls);
        Assert.Equal(0, result.MigrationCalls);
        Assert.Equal(0, result.DownloadCalls);
    }

    public static TheoryData<string[]> InvalidStandaloneArguments => new()
    {
        new[] { "--migrate", "--help" },
        new[] { "--migrate", "fixture" },
        new[] { "--migrate", "--version" },
        new[] { "--update", "--help" },
        new[] { "--update", "--migrate" },
        new[] { "--update", "fixture" },
        new[] { "fixture", "--update" },
        new[] { "--update", "serve" },
        new[] { "serve", "--update" },
        new[] { "--update=true" },
        new[] { "-update" }
    };

    [Theory]
    [MemberData(nameof(InvalidStandaloneArguments))]
    public async Task StandaloneCommandsWithOtherArgumentsOrUnsupportedSpelling_AreRejected(string[] args)
    {
        var result = await Invoke(args);

        Assert.Equal(1, result.ExitCode);
        Assert.NotEmpty(result.Error);
        Assert.Equal(0, result.UpdateCalls);
        Assert.Equal(0, result.MigrationCalls);
        Assert.Equal(0, result.DownloadCalls);
    }

    [Fact]
    public async Task UpdateWithConfigFile_IsRejectedBeforeReadingConfig()
    {
        var result = await Invoke(["--update"], config: "--definitely-unknown\n");

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("--update", result.Error);
        Assert.DoesNotContain("--definitely-unknown", result.Error);
        Assert.Equal(0, result.UpdateCalls);
        Assert.Equal(0, result.MigrationCalls);
        Assert.Equal(0, result.DownloadCalls);
    }

    private static async Task<InvocationResult> Invoke(
        string[] args,
        string? config = null,
        int migrationExitCode = 0,
        int updateExitCode = 0)
    {
        var originalOutput = Console.Out;
        var originalError = Console.Error;
        using var output = new StringWriter();
        using var error = new StringWriter();
        string? path = null;
        var migrations = 0;
        var updates = 0;
        var downloads = 0;
        MyOption? bound = null;
        try
        {
            if (config is not null)
            {
                path = Path.GetTempFileName();
                await File.WriteAllTextAsync(path, config);
                args = [.. args, "--config-file", path];
            }
            Console.SetOut(output);
            Console.SetError(error);
            var code = await Program.InvokeCommandLineAsync(args, option =>
            {
                downloads++;
                bound = option;
                return Task.CompletedTask;
            }, () => { migrations++; return migrationExitCode; },
            () =>
            {
                updates++;
                return Task.FromResult(updateExitCode);
            });
            return new InvocationResult(code, output.ToString(), error.ToString(), migrations, updates, downloads, bound);
        }
        finally
        {
            Console.SetOut(originalOutput);
            Console.SetError(originalError);
            if (path is not null) File.Delete(path);
        }
    }

    private sealed record InvocationResult(
        int ExitCode, string Output, string Error, int MigrationCalls, int UpdateCalls, int DownloadCalls, MyOption? Option);
}
