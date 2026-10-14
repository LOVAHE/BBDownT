using System.Runtime.Versioning;
using System.CommandLine;

namespace BBDownT.Tests;

public class ConfigParserTests
{
    [Fact]
    public async Task ValidConfig_AllowsWhitespaceCommentsAndInlinePath()
    {
        var path = Path.GetTempFileName();
        await File.WriteAllTextAsync(path, "   \n  # indented comment\n--server-max-queue\n5\n");
        var arguments = new List<string> { "serve", $"--config-file={path}" };
        try
        {
            Assert.True(BBDownTConfigParser.HandleConfig(arguments, CreateRootCommand(), "serve"));
            Assert.Equal("--server-max-queue=5", arguments[^1]);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SharedConfig_AppliesOnlyTheOptionsOfTheCurrentCommand(bool serve)
    {
        var path = Path.GetTempFileName();
        await File.WriteAllTextAsync(path, "--hide-streams\n--server-max-queue\n5\n--listen \"http://127.0.0.1:1\"\n--delay-per-page\n2\n--api-token\nsecret\n");
        var arguments = serve
            ? new List<string> { "serve", "--config-file", path }
            : new List<string> { "BV1xx411c7mD", "--config-file", path };
        try
        {
            Assert.True(BBDownTConfigParser.HandleConfig(arguments, CreateRootCommand(), serve ? "serve" : null));

            var merged = arguments.Skip(3).ToList();
            Assert.Equal(serve
                ? ["--server-max-queue=5", "--listen=http://127.0.0.1:1", "--api-token=secret"]
                : ["--hide-streams", "--delay-per-page=2", "--api-token=secret"], merged);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task InvalidConfig_IsReportedAndNotPartiallyApplied()
    {
        var path = Path.GetTempFileName();
        await File.WriteAllTextAsync(path, "--hide-streams\n--unknown-option\n");
        var arguments = new List<string> { "BV1xx411c7mD", "--config-file", path };
        var rootCommand = CreateRootCommand();
        var originalOutput = Console.Out;
        using var output = new StringWriter();
        try
        {
            Console.SetOut(output);

            var success = BBDownTConfigParser.HandleConfig(arguments, rootCommand);

            Assert.False(success);
            Assert.DoesNotContain("--hide-streams", arguments);
            Assert.Contains("unknown-option", output.ToString(), StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Console.SetOut(originalOutput);
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData("--aria2c-args\n-x16 -s16 -j16 -k 5M\n", "-x16 -s16 -j16 -k 5M")]
    [InlineData("--aria2c-args\n-c\n", "-c")]
    [InlineData("--aria2c-args\n\"-q\"\n", "-q")]
    [InlineData("--aria2c-args=\"-x16 -s16\"\n", "-x16 -s16")]
    [InlineData("--aria2c-args=-x16 -s16\n", "-x16 -s16")]
    [InlineData("--aria2c-args -x16 -s16\n", "-x16 -s16")]
    [InlineData("--aria2c-args @x\n", "@x")]
    [InlineData("--listen\n-not-an-option\n--aria2c-args\n# comment\n-x8\n", "-x8")]
    public async Task OptionValues_MayStartWithADashOrContainSpaces(string config, string aria2cArgs)
    {
        var path = Path.GetTempFileName();
        await File.WriteAllTextAsync(path, config + "--hide-streams\n");
        MyOption? bound = null;
        var rootCommand = CreateRootCommand(option =>
        {
            bound = option;
            return Task.CompletedTask;
        });
        var arguments = new List<string> { "BV1xx411c7mD", "--config-file", path };
        try
        {
            Assert.True(BBDownTConfigParser.HandleConfig(arguments, rootCommand));
            Assert.Equal(0, await rootCommand.InvokeAsync(arguments.ToArray()));

            Assert.NotNull(bound);
            Assert.Equal(aria2cArgs, bound.Aria2cArgs);
            Assert.True(bound.HideStreams);
            Assert.Equal("", bound.Cookie);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void MissingExplicitConfigFile_IsAnError(bool inline)
    {
        var path = Path.Combine(Path.GetTempPath(), $"bbdownt-missing-{Guid.NewGuid():N}.config");
        List<string> arguments = inline
            ? ["BV1xx411c7mD", $"--config-file={path}"]
            : ["BV1xx411c7mD", "--config-file", path];
        var original = arguments.ToArray();

        var output = CaptureOutput(() => Assert.False(BBDownTConfigParser.HandleConfig(arguments, CreateRootCommand())));

        Assert.Contains($"配置文件不存在: {path}", output);
        Assert.Equal(original, arguments);
    }

    [UnixNonRootFact]
    [UnsupportedOSPlatform("windows")]
    public async Task UnreadableConfig_IsReportedInPlainWords()
    {
        var path = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(path, "--hide-streams\n");
            File.SetUnixFileMode(path, UnixFileMode.None);
            var arguments = new List<string> { "BV1xx411c7mD", "--config-file", path };

            var output = CaptureOutput(() => Assert.False(BBDownTConfigParser.HandleConfig(arguments, CreateRootCommand())));

            Assert.Contains("没有权限读写文件或目录", output);
        }
        finally
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData("response-file")]
    [InlineData("colon")]
    public async Task ServeOptionsGivenInAnyForm_KeepPrecedenceOverTheConfig(string form)
    {
        var config = Path.GetTempFileName();
        var responseFile = Path.GetTempFileName();
        await File.WriteAllTextAsync(config, "--listen\nhttp://127.0.0.1:1\n--server-max-queue\n5\n");
        await File.WriteAllTextAsync(responseFile, "--listen http://127.0.0.1:2\n");
        var given = form == "colon" ? "--listen:http://127.0.0.1:2" : $"@{responseFile}";
        var arguments = new List<string> { "serve", given, "--config-file", config };
        var rootCommand = CreateRootCommand();
        try
        {
            Assert.True(BBDownTConfigParser.HandleConfig(arguments, rootCommand, "serve"));

            Assert.Equal(["serve", given, "--config-file", config, "--server-max-queue=5"], arguments);
            Assert.Empty(rootCommand.Parse(arguments.ToArray()).Errors);
        }
        finally
        {
            File.Delete(config);
            File.Delete(responseFile);
        }
    }

    private static string CaptureOutput(Action action)
    {
        var originalOutput = Console.Out;
        using var output = new StringWriter();
        try
        {
            Console.SetOut(output);
            action();
        }
        finally
        {
            Console.SetOut(originalOutput);
        }
        return output.ToString();
    }

    private static RootCommand CreateRootCommand(Func<MyOption, Task>? action = null)
    {
        var rootCommand = CommandLineInvoker.GetRootCommand(action ?? (_ => Task.CompletedTask));
        rootCommand.AddGlobalOption(new Option<string>(["--api-token"]));
        rootCommand.AddCommand(new Command("serve")
        {
            new Option<string>(["--listen", "-l"]),
            new Option<int>(["--server-max-queue"])
        });
        rootCommand.TreatUnmatchedTokensAsErrors = true;
        return rootCommand;
    }
}
