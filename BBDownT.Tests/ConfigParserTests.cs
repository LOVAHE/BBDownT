namespace BBDownT.Tests;

public class ConfigParserTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ValidConfig_AllowsWhitespaceCommentsAndBothPathForms(bool inlinePath)
    {
        var path = Path.GetTempFileName();
        await File.WriteAllTextAsync(path, "   \n  # indented comment\n--hide-streams\n");
        var arguments = inlinePath
            ? new List<string> { "serve", $"--config-file={path}" }
            : new List<string> { "serve", "--config-file", path };
        var rootCommand = CommandLineInvoker.GetRootCommand(_ => Task.CompletedTask);
        rootCommand.TreatUnmatchedTokensAsErrors = true;
        try
        {
            Assert.True(BBDownTConfigParser.HandleConfig(arguments, rootCommand, "serve"));
            Assert.Contains("--hide-streams", arguments);
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
        var rootCommand = CommandLineInvoker.GetRootCommand(_ => Task.CompletedTask);
        rootCommand.TreatUnmatchedTokensAsErrors = true;
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
}
