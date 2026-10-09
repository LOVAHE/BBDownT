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
            Assert.Equal(["--server-max-queue", "5"], arguments[^2..]);
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
                ? ["--server-max-queue", "5", "--listen", "http://127.0.0.1:1", "--api-token", "secret"]
                : ["--hide-streams", "--delay-per-page", "2", "--api-token", "secret"], merged);
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

    private static RootCommand CreateRootCommand()
    {
        var rootCommand = CommandLineInvoker.GetRootCommand(_ => Task.CompletedTask);
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
