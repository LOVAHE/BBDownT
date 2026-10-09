using System.Globalization;
using System.Text;
using BBDownT.Core;

namespace BBDownT.Tests;

public class ProgressLogRenderingTests
{
    [Theory]
    [InlineData(40)]
    [InlineData(80)]
    [InlineData(120)]
    public void BoundedTerminalWidth_PhaseUpdatesAndLogsStayWithinDisplayColumns(int columns)
    {
        CaptureConsole(output =>
        {
            using var progress = new ProgressBar(output, terminalColumns: columns);
            progress.ReportDownload(0.08, 0, "校验 80%");
            progress.RefreshDisplay();
            Assert.Contains("8.00%", Render(output).CurrentLine);
            Assert.Contains("校验 80%", Render(output).CurrentLine);
            Assert.InRange(Render(output).MaximumColumn, 0, columns - 1);

            Logger.LogWarn("校验", time: false);
            progress.ReportDownload(0.09, 0, "校验 90% " + new string('片', 160));
            progress.RefreshDisplay();
            Assert.Equal(new string(' ', 28) + "校验", Assert.Single(Render(output).Lines));
            Assert.Contains("9.00%", Render(output).CurrentLine);
            Assert.Contains("校验", Render(output).CurrentLine);
            Assert.EndsWith("…", Render(output).CurrentLine);
            Assert.InRange(Render(output).MaximumColumn, 0, columns - 1);

            progress.ReportDownload(0.10, 0);
            progress.RefreshDisplay();
            Assert.Contains("10.00%", Render(output).CurrentLine);
            Assert.DoesNotContain("校验", Render(output).CurrentLine);
            Assert.InRange(Render(output).MaximumColumn, 0, columns - 1);
            progress.Dispose();
            Assert.Equal("", Render(output).CurrentLine);
            Assert.InRange(Render(output).MaximumColumn, 0, columns - 1);
            Assert.Equal(0, Render(output).BackspaceUnderflows);
        });
    }

    [Theory]
    [InlineData(40)]
    [InlineData(80)]
    [InlineData(120)]
    public void OversizedPhaseAndSpeed_PreservePercentAndPhaseWithinAvailableWidth(int columns)
    {
        var text = ProgressBar.FormatDisplayText(1, " - 123456789.00MB/s",
            "校验 100% " + new string('片', 160), '|', columns);
        var terminal = new TerminalRendering(text);

        Assert.Contains("100.00%", terminal.CurrentLine);
        Assert.Contains("校验", terminal.CurrentLine);
        Assert.EndsWith("…", terminal.CurrentLine);
        Assert.DoesNotContain("/s", terminal.CurrentLine);
        Assert.InRange(terminal.MaximumColumn, 0, columns - 1);
        Assert.Equal(0, terminal.BackspaceUnderflows);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(120)]
    public void UnavailableOrWideTerminal_PreservesTheOriginalDisplayStyle(int columns)
    {
        var text = ProgressBar.FormatDisplayText(0.08, " - 1.00MB/s", "校验 80%", '|', columns);

        Assert.Equal(new string(' ', 28) + "[" + new string('#', 3) + new string('-', 37)
            + "] 8.00% | - 1.00MB/s - 校验 80%", text);
    }

    [Fact]
    public void WarningBetweenUpdates_LeavesCompleteLogAndRedrawsFromNewLine()
    {
        CaptureConsole(output =>
        {
            using var progress = new ProgressBar(output);
            progress.Report(0.08);
            progress.RefreshDisplay();
            Logger.LogWarn("校验已有分片");
            progress.Report(0.09);
            progress.RefreshDisplay();

            var terminal = Render(output);
            Assert.Matches(@"^\[\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3}\] - 校验已有分片$",
                Assert.Single(terminal.Lines));
            Assert.Contains("9.00%", terminal.CurrentLine);
            Assert.Equal(0, terminal.BackspaceUnderflows);
        });
    }

    [Fact]
    public void VerificationStatusBetweenWarnings_ClearsItsFullDisplayWidth()
    {
        CaptureConsole(output =>
        {
            using var progress = new ProgressBar(output);
            progress.ReportDownload(0.08, 0, "校验 80%");
            progress.RefreshDisplay();
            Assert.EndsWith(" - 校验 80%", Render(output).CurrentLine);

            Logger.LogWarn("校验已有分片", time: false);
            progress.ReportDownload(0.09, 0, "校验 90%");
            progress.RefreshDisplay();

            var terminal = Render(output);
            Assert.Equal(new string(' ', 28) + "校验已有分片", Assert.Single(terminal.Lines));
            Assert.EndsWith(" - 校验 90%", terminal.CurrentLine);
            Assert.Equal(0, terminal.BackspaceUnderflows);
            progress.Dispose();
            Assert.Equal("", Render(output).CurrentLine);
            Assert.Equal(0, Render(output).BackspaceUnderflows);
        });
    }

    [Fact]
    public void VerificationStatusToDownload_RemovesWideSuffixAndClearsShorterLine()
    {
        CaptureConsole(output =>
        {
            using var progress = new ProgressBar(output);
            progress.ReportDownload(0.08, 0, "校验 100%");
            progress.RefreshDisplay();
            progress.ReportDownload(0.09, 0);
            progress.RefreshDisplay();

            var terminal = Render(output);
            Assert.Empty(terminal.Lines);
            Assert.Contains("9.00%", terminal.CurrentLine);
            Assert.EndsWith(" /", terminal.CurrentLine);
            Assert.DoesNotContain("校验", terminal.CurrentLine);
            Assert.Equal(0, terminal.BackspaceUnderflows);
            progress.Dispose();
            Assert.Equal("", Render(output).CurrentLine);
            Assert.Equal(0, Render(output).BackspaceUnderflows);
        });
    }

    [Fact]
    public void SupplementaryWideCharacterUpdate_DoesNotSplitSharedSurrogatePrefix()
    {
        CaptureConsole(output =>
        {
            using var line = Logger.RegisterProgressLine(output);
            line.Update("\U00020000");
            int previousLength = output.ToString().Length;

            line.Update("\U00020001");

            Assert.Equal("\b\b\U00020001", output.ToString()[previousLength..]);
            Assert.Equal("\U00020001", Render(output).CurrentLine);
            line.Dispose();
            Assert.Equal("", Render(output).CurrentLine);
            Assert.Equal(0, Render(output).BackspaceUnderflows);
        });
    }

    [Fact]
    public void CombiningMarksAndFullWidthText_RedrawWithoutLeavingOldCells()
    {
        CaptureConsole(output =>
        {
            using var line = Logger.RegisterProgressLine(output);
            line.Update("ｅ校验e\u0301");
            line.Update("ｅ校验e\u0300");
            Assert.Equal("ｅ校验e\u0300", Render(output).CurrentLine);

            line.Update("e\u0300");

            Assert.Equal("e\u0300", Render(output).CurrentLine);
            line.Dispose();
            Assert.Equal("", Render(output).CurrentLine);
            Assert.Equal(0, Render(output).BackspaceUnderflows);
        });
    }

    [Fact]
    public void EveryLogEntryPoint_ClearsProgressAndPreservesPrefixesAndRedaction()
    {
        CaptureConsole(output =>
        {
            using var progress = new ProgressBar(output);
            Action[] entries =
            [
                () => Logger.Log("normal access_key=test-secret&qn=127"),
                () => Logger.LogError("error access_key=test-secret&qn=127"),
                () => Logger.LogColor("color access_key=test-secret&qn=127"),
                () => Logger.LogWarn("warning access_key=test-secret&qn=127"),
                () => Logger.LogColor("color-no-time access_key=test-secret&qn=127", time: false),
                () => Logger.LogWarn("warning-no-time access_key=test-secret&qn=127", time: false),
                () => Logger.LogDebug("  {0}  ", "debug access_key=test-secret&qn=127")
            ];
            foreach (var entry in entries)
            {
                progress.Report(0.08);
                progress.RefreshDisplay();
                entry();
            }

            var terminal = Render(output);
            Assert.Equal(entries.Length, terminal.Lines.Count);
            for (int index = 0; index < terminal.Lines.Count; index++)
            {
                var line = terminal.Lines[index];
                Assert.EndsWith("access_key=<redacted>&qn=127", line);
                if (index is 4 or 5)
                    Assert.StartsWith(new string(' ', 28), line);
                else
                    Assert.Matches(@"^\[\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3}\] - ", line);
            }
            Assert.DoesNotContain("test-secret", output.ToString());
            Assert.Equal("", terminal.CurrentLine);
            Assert.Equal(0, terminal.BackspaceUnderflows);
        }, debug: true);
    }

    [Fact]
    public void UnterminatedLog_PreventsProgressFromOverwritingItUntilNextNewline()
    {
        CaptureConsole(output =>
        {
            using var progress = new ProgressBar(output);
            progress.Report(0.08);
            progress.RefreshDisplay();
            Logger.Log("partial", enter: false);
            var partialOutput = output.ToString();

            progress.Report(0.09);
            progress.RefreshDisplay();
            Assert.Equal(partialOutput, output.ToString());
            Assert.Empty(Render(output).Lines);
            Assert.EndsWith(" - partial", Render(output).CurrentLine);

            Logger.Log("complete");
            progress.RefreshDisplay();
            var terminal = Render(output);
            var line = Assert.Single(terminal.Lines);
            Assert.Contains(" - partial[", line);
            Assert.EndsWith(" - complete", line);
            Assert.Contains("9.00%", terminal.CurrentLine);
            Assert.Equal(0, terminal.BackspaceUnderflows);
        });
    }

    [Fact]
    public void DisabledDebugLog_DoesNotClearActiveProgress()
    {
        CaptureConsole(output =>
        {
            using var progress = new ProgressBar(output);
            progress.Report(0.08);
            progress.RefreshDisplay();
            var before = output.ToString();

            Logger.LogDebug("disabled");

            Assert.Equal(before, output.ToString());
            Assert.Contains("8.00%", Render(output).CurrentLine);
        });
    }

    [Fact]
    public void DisposedProgress_UnregistersAndDoesNotRedrawAfterLogging()
    {
        CaptureConsole(output =>
        {
            using var progress = new ProgressBar(output);
            progress.Report(0.08);
            progress.RefreshDisplay();
            progress.Dispose();
            Assert.Equal("", Render(output).CurrentLine);

            var beforeLog = output.ToString().Length;
            Logger.LogWarn("after disposal", time: false);
            var afterLog = output.ToString();
            Assert.Equal(new string(' ', 28) + "after disposal" + Environment.NewLine,
                afterLog[beforeLog..]);
            progress.Report(0.09);
            progress.RefreshDisplay();
            progress.Dispose();
            Assert.Equal(afterLog, output.ToString());
            Assert.Equal(new string(' ', 28) + "after disposal", Assert.Single(Render(output).Lines));
            Assert.Equal(0, Render(output).BackspaceUnderflows);
        });
    }

    [Fact]
    public void ReplacedProgress_CannotClearOrUpdateTheActiveLine()
    {
        CaptureConsole(output =>
        {
            using var first = new ProgressBar(output);
            first.Report(0.08);
            first.RefreshDisplay();
            using var second = new ProgressBar(output);
            second.Report(0.09);
            second.RefreshDisplay();
            var before = output.ToString();

            first.RefreshDisplay();
            first.Dispose();

            Assert.Equal(before, output.ToString());
            Assert.Contains("9.00%", Render(output).CurrentLine);
            Logger.LogWarn("active line", time: false);
            second.RefreshDisplay();
            Assert.Equal(new string(' ', 28) + "active line", Assert.Single(Render(output).Lines));
            Assert.Contains("9.00%", Render(output).CurrentLine);
            Assert.Equal(0, Render(output).BackspaceUnderflows);
        });
    }

    [Fact]
    public void RedirectedOutput_ContainsLogsWithoutProgressControlCharacters()
    {
        CaptureConsole(output =>
        {
            using var progress = new ProgressBar(output, outputRedirected: true);
            progress.Report(0.08);
            progress.RefreshDisplay();
            Logger.LogWarn("redirected", time: false);
            progress.Report(0.09);
            progress.RefreshDisplay();
            progress.Dispose();

            Assert.Equal(new string(' ', 28) + "redirected" + Environment.NewLine, output.ToString());
        });
    }

    [Fact]
    public void ConcurrentWarningsAndProgress_KeepEveryWarningOnItsOwnCompleteLine()
    {
        const int entryCount = 64;
        CaptureConsole(output =>
        {
            using var progress = new ProgressBar(output);
            Parallel.Invoke(
                () =>
                {
                    for (int index = 0; index < entryCount; index++)
                        Logger.LogWarn($"entry-{index:D3}", time: false);
                },
                () =>
                {
                    for (int index = 0; index < entryCount; index++)
                    {
                        progress.Report(index % 2 == 0 ? 0.08 : 0.09);
                        progress.RefreshDisplay();
                    }
                });
            progress.Report(0.09);
            progress.RefreshDisplay();

            var terminal = Render(output);
            Assert.Equal(entryCount, terminal.Lines.Count);
            for (int index = 0; index < entryCount; index++)
                Assert.Equal(new string(' ', 28) + $"entry-{index:D3}", terminal.Lines[index]);
            Assert.Contains("9.00%", terminal.CurrentLine);
            Assert.Equal(0, terminal.BackspaceUnderflows);
        });
    }

    private static void CaptureConsole(Action<StringWriter> action, bool debug = false)
    {
        var previousOutput = Console.Out;
        var previousDebug = Config.DEBUG_LOG;
        using var output = new YieldingWriter();
        try
        {
            Console.SetOut(output);
            Config.DEBUG_LOG = debug;
            action(output);
        }
        finally
        {
            Console.SetOut(previousOutput);
            Config.DEBUG_LOG = previousDebug;
        }
    }

    private static TerminalRendering Render(StringWriter output) => new(output.ToString());

    private sealed class YieldingWriter : StringWriter
    {
        public override void Write(string? value)
        {
            base.Write(value);
            Thread.Yield();
        }

        public override void WriteLine()
        {
            base.WriteLine();
            Thread.Yield();
        }
    }

    // Replays display cells independently of the production renderer. A CJK
    // character occupies a lead cell and a continuation cell, not one char slot.
    // Completed lines are immutable; a backspace at column zero is invalid.
    private sealed class TerminalRendering
    {
        internal List<string> Lines { get; } = [];
        internal string CurrentLine { get; }
        internal int BackspaceUnderflows { get; private set; }
        internal int MaximumColumn { get; private set; }

        internal TerminalRendering(string script)
        {
            var line = new List<string>();
            int cursor = 0;
            foreach (var character in script.EnumerateRunes())
            {
                switch (character.Value)
                {
                    case '\b':
                        if (cursor == 0) BackspaceUnderflows++;
                        else cursor--;
                        break;
                    case '\r':
                        cursor = 0;
                        break;
                    case '\n':
                        Lines.Add(string.Concat(line).TrimEnd(' '));
                        line.Clear();
                        cursor = 0;
                        break;
                    default:
                        int columns = CharacterColumns(character);
                        if (columns == 0)
                        {
                            if (cursor > 0 && (Rune.GetUnicodeCategory(character) is UnicodeCategory.NonSpacingMark
                                or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark))
                            {
                                int baseColumn = cursor - 1;
                                if (line[baseColumn] == "" && baseColumn > 0) baseColumn--;
                                line[baseColumn] += character.ToString();
                            }
                            break;
                        }
                        while (line.Count < cursor + columns) line.Add(" ");
                        for (int column = cursor; column < cursor + columns; column++)
                            ClearCell(line, column);
                        line[cursor] = character.ToString();
                        if (columns == 2) line[cursor + 1] = "";
                        cursor += columns;
                        MaximumColumn = Math.Max(MaximumColumn, cursor);
                        break;
                }
            }
            CurrentLine = string.Concat(line).TrimEnd(' ');
        }

        private static void ClearCell(List<string> line, int column)
        {
            if (line[column] == "" && column > 0) line[column - 1] = " ";
            if (column + 1 < line.Count && line[column + 1] == "") line[column + 1] = " ";
            line[column] = " ";
        }

        private static int CharacterColumns(Rune character)
        {
            if (Rune.GetUnicodeCategory(character) is UnicodeCategory.NonSpacingMark
                or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark
                or UnicodeCategory.Control or UnicodeCategory.Format)
                return 0;

            // The tests use Han ideographs (including supplementary planes)
            // and fullwidth Latin; keep this simulation separate from the
            // production width table so a renderer bug is observable here.
            return character.Value is >= 0x2E80 and <= 0xA4CF
                or >= 0xFF01 and <= 0xFF60
                or >= 0x20000 and <= 0x3FFFD ? 2 : 1;
        }
    }
}
