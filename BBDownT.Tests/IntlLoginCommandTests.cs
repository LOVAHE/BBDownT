using BBDownT.Core;

namespace BBDownT.Tests;

public class IntlLoginCommandTests
{
    public static TheoryData<string[], bool, bool> LoginArguments => new()
    {
        { ["--debug", "loginintl"], true, false },
        { ["loginintl", "--debug"], true, false },
        { ["--debug", "loginintl", "--cookie"], true, true },
        { ["loginintl", "--import-cookie", "--debug"], true, true },
        { ["loginintl"], false, false },
        { ["--debug", "false", "loginintl"], false, false },
        { ["loginintl", "--debug", "false"], false, false }
    };

    [Theory]
    [MemberData(nameof(LoginArguments))]
    public async Task Login_ReusesDebugOptionBeforeAndAfterCommand(string[] args, bool debug, bool importCookie)
    {
        var previous = Config.DEBUG_LOG;
        try
        {
            Config.DEBUG_LOG = !debug;
            var calls = 0;
            var exit = await Program.InvokeCommandLineAsync(args,
                _ => throw new Exception("Download must not run"),
                () => throw new Exception("Migration must not run"),
                loginIntl: import =>
                {
                    calls++;
                    Assert.Equal(importCookie, import);
                    Assert.Equal(debug, Config.DEBUG_LOG);
                    return Task.FromResult(7);
                });

            Assert.Equal(7, exit);
            Assert.Equal(1, calls);
        }
        finally { Config.DEBUG_LOG = previous; }
    }

    [Fact]
    public async Task LoginHelp_DescribesDebugWithoutRunningLogin()
    {
        var previous = Console.Out;
        using var output = new StringWriter();
        try
        {
            Console.SetOut(output);
            var exit = await Program.InvokeCommandLineAsync(["loginintl", "--help"],
                _ => throw new Exception("Download must not run"),
                () => throw new Exception("Migration must not run"),
                loginIntl: _ => throw new Exception("Login must not run"));

            Assert.Equal(0, exit);
            Assert.Contains("loginintl [options]", output.ToString());
            Assert.Contains("--debug", output.ToString());
            Assert.Contains("--import-cookie", output.ToString());
            Assert.DoesNotContain("--api-token", output.ToString());
            Assert.DoesNotContain("<url>", output.ToString());
            Assert.DoesNotContain("--config-file", output.ToString());
        }
        finally { Console.SetOut(previous); }
    }
}
