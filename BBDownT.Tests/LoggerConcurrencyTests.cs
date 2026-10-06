using System.Text.RegularExpressions;
using BBDownT.Core;

namespace BBDownT.Tests;

public class LoggerConcurrencyTests
{
    [Fact]
    public void ConcurrentLogging_KeepsEveryEntryTogetherAndRedactsSecrets()
    {
        const int entryCount = 84;
        using var output = new YieldingWriter();
        CaptureConsole(output, true, () =>
            Parallel.For(0, entryCount, new ParallelOptions { MaxDegreeOfParallelism = 8 }, index =>
            {
                var message = $"entry-{index:D3} access_key=test-secret-{index}&qn=127";
                switch (index % 7)
                {
                    case 0: Logger.Log(message); break;
                    case 1: Logger.LogError(message); break;
                    case 2: Logger.LogColor(message); break;
                    case 3: Logger.LogWarn(message); break;
                    case 4: Logger.LogColor(message, time: false); break;
                    case 5: Logger.LogWarn(message, time: false); break;
                    case 6: Logger.LogDebug("  {0}  ", message); break;
                }
            }));

        var text = output.ToString();
        Assert.DoesNotContain("test-secret", text);
        Assert.EndsWith(Environment.NewLine, text);
        var lines = text.Split(Environment.NewLine, StringSplitOptions.None);
        Assert.Equal(entryCount + 1, lines.Length);
        Assert.Equal("", lines[^1]);

        var seen = new HashSet<int>();
        foreach (var line in lines.Take(entryCount))
        {
            var match = Regex.Match(line,
                @"^(?<prefix>\[\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3}\] - | {28})entry-(?<index>\d{3}) access_key=<redacted>&qn=127$");
            Assert.True(match.Success, $"A log entry was split or combined: {line}");
            var index = int.Parse(match.Groups["index"].Value);
            Assert.InRange(index, 0, entryCount - 1);
            Assert.True(seen.Add(index), $"Duplicate log entry: {index}");
            if (index % 7 is 4 or 5)
                Assert.Equal(new string(' ', 28), match.Groups["prefix"].Value);
            else
                Assert.StartsWith("[", match.Groups["prefix"].Value);
        }
        Assert.Equal(entryCount, seen.Count);
    }

    [Fact]
    public void Log_WithEnterFalse_PreservesOutputWithoutNewline()
    {
        using var output = new StringWriter();
        CaptureConsole(output, false, () => Logger.Log("entry access_key=test-secret&qn=127", enter: false));

        Assert.Matches(@"^\[\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3}\] - entry access_key=<redacted>&qn=127$",
            output.ToString());
        Assert.DoesNotContain(Environment.NewLine, output.ToString());
    }

    [Fact]
    public void LogDebug_PreservesDisabledAndFormattingBehavior()
    {
        using var output = new StringWriter();
        CaptureConsole(output, false, () => Logger.LogDebug("disabled {0}", "entry"));
        Assert.Equal("", output.ToString());

        CaptureConsole(output, true, () =>
        {
            Logger.LogDebug("  formatted {0}  ", "entry");
            Logger.LogDebug("  unformatted entry  ");
        });

        var lines = output.ToString().Split(Environment.NewLine, StringSplitOptions.None);
        Assert.Equal(3, lines.Length);
        Assert.EndsWith(" - formatted entry", lines[0]);
        Assert.EndsWith(" -   unformatted entry  ", lines[1]);
        Assert.Equal("", lines[2]);
    }

    private static void CaptureConsole(TextWriter output, bool debug, Action action)
    {
        var previousOutput = Console.Out;
        var previousDebug = Config.DEBUG_LOG;
        try
        {
            Console.SetOut(output);
            Config.DEBUG_LOG = debug;
            action();
        }
        finally
        {
            Console.SetOut(previousOutput);
            Config.DEBUG_LOG = previousDebug;
        }
    }

    private sealed class YieldingWriter : StringWriter
    {
        public override void Write(string? value)
        {
            base.Write(value);
            Thread.Sleep(1);
            Thread.Yield();
        }

        public override void WriteLine()
        {
            base.WriteLine();
            Thread.Sleep(1);
            Thread.Yield();
        }
    }
}
