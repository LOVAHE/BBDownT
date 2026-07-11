using System.CommandLine;

namespace BBDownT.Tests;

public class CommandLineInvokerTests
{
    [Fact]
    public async Task SimplyMuxOption_IsRegisteredAndBound()
    {
        MyOption? boundOption = null;
        var rootCommand = CommandLineInvoker.GetRootCommand(option =>
        {
            boundOption = option;
            return Task.CompletedTask;
        });

        var exitCode = await rootCommand.InvokeAsync(["BV1xx411c7mD", "--simply-mux"]);

        Assert.Equal(0, exitCode);
        Assert.NotNull(boundOption);
        Assert.True(boundOption.SimplyMux);
    }
}
