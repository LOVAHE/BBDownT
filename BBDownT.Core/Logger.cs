using System.Text.RegularExpressions;

using System.Globalization;
using System.Text;

namespace BBDownT.Core;

public static class Logger
{
    internal const int TimestampWidth = 28;
    private static readonly object OutputLock = new();
    private static ProgressLine? activeProgressLine;
    private static bool logLineOpen;

    // Progress rendering shares the log lock, without calling back into a timer
    // or retaining its owner. Only the most recently registered line is active.
    internal static ProgressLine RegisterProgressLine(TextWriter? output = null)
    {
        lock (OutputLock)
        {
            activeProgressLine?.Clear();
            return activeProgressLine = new ProgressLine(output ?? Console.Out);
        }
    }

    internal sealed class ProgressLine : IDisposable
    {
        private readonly TextWriter output;
        private string currentText = "";
        private bool disposed;

        internal ProgressLine(TextWriter output) => this.output = output;

        internal void Update(string text)
        {
            lock (OutputLock)
            {
                if (disposed || activeProgressLine != this || logLineOpen) return;

                int commonPrefixLength = 0;
                int lastVisiblePrefixStart = 0;
                var previousRunes = currentText.EnumerateRunes().GetEnumerator();
                var nextRunes = text.EnumerateRunes().GetEnumerator();
                while (true)
                {
                    bool hasPrevious = previousRunes.MoveNext();
                    bool hasNext = nextRunes.MoveNext();
                    if (!hasPrevious || !hasNext || previousRunes.Current != nextRunes.Current)
                    {
                        // A changed combining mark needs its base redrawn too.
                        if ((hasPrevious && GetDisplayWidth(previousRunes.Current) == 0)
                            || (hasNext && GetDisplayWidth(nextRunes.Current) == 0))
                            commonPrefixLength = lastVisiblePrefixStart;
                        break;
                    }
                    if (GetDisplayWidth(previousRunes.Current) > 0)
                        lastVisiblePrefixStart = commonPrefixLength;
                    commonPrefixLength += previousRunes.Current.Utf16SequenceLength;
                }

                var rendering = new StringBuilder();
                rendering.Append('\b', GetDisplayWidth(currentText.AsSpan(commonPrefixLength)));
                rendering.Append(text[commonPrefixLength..]);
                int overlapCount = GetDisplayWidth(currentText.AsSpan()) - GetDisplayWidth(text.AsSpan());
                if (overlapCount > 0)
                {
                    rendering.Append(' ', overlapCount);
                    rendering.Append('\b', overlapCount);
                }
                output.Write(rendering.ToString());
                currentText = text;
            }
        }

        // Called only while OutputLock is held. Clearing also resets the baseline
        // so a later update cannot backtrack into the intervening log entry.
        internal void Clear()
        {
            if (currentText.Length == 0) return;
            int columns = GetDisplayWidth(currentText.AsSpan());
            output.Write(new string('\b', columns) + new string(' ', columns) + new string('\b', columns));
            currentText = "";
        }

        internal static int GetDisplayWidth(ReadOnlySpan<char> text)
        {
            int columns = 0;
            foreach (var character in text.EnumerateRunes())
                columns += GetDisplayWidth(character);
            return columns;
        }

        // Progress text uses the usual narrow/wide terminal convention. This
        // deliberately does not model terminal wrapping or emoji cluster shaping.
        internal static int GetDisplayWidth(Rune character)
        {
            var category = Rune.GetUnicodeCategory(character);
            if (category is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark
                or UnicodeCategory.EnclosingMark or UnicodeCategory.Control or UnicodeCategory.Format)
                return 0;

            int value = character.Value;
            return value is >= 0x1100 and <= 0x115F
                or >= 0x2329 and <= 0x232A
                or >= 0x2E80 and <= 0xA4CF and not 0x303F
                or >= 0xAC00 and <= 0xD7A3
                or >= 0xF900 and <= 0xFAFF
                or >= 0xFE10 and <= 0xFE19
                or >= 0xFE30 and <= 0xFE6F
                or >= 0xFF00 and <= 0xFF60
                or >= 0xFFE0 and <= 0xFFE6
                or >= 0x20000 and <= 0x3FFFD ? 2 : 1;
        }

        public void Dispose()
        {
            lock (OutputLock)
            {
                if (disposed) return;
                disposed = true;
                if (activeProgressLine != this) return;
                Clear();
                activeProgressLine = null;
                logLineOpen = false;
            }
        }
    }

    private static readonly Regex[] SensitivePatterns =
    [
        new("(?i)(Cookie:\\s*)[^\\r\\n]+", RegexOptions.Compiled),
        new("(?i)(Authorization:\\s*)[^\\r\\n]+", RegexOptions.Compiled),
        new("(?i)(SESSDATA=)[^;\\s&]+", RegexOptions.Compiled),
        new("(?i)(bili_jct=)[^;\\s&]+", RegexOptions.Compiled),
        new("(?i)(DedeUserID=)[^;\\s&]+", RegexOptions.Compiled),
        new("(?i)(ac_time_value=)[^;\\s&]+", RegexOptions.Compiled),
        new("(?i)(refresh_token=)[^;\\s&]+", RegexOptions.Compiled),
        new("(?i)(access_token=)[^;\\s&]+", RegexOptions.Compiled),
        new("(?i)(access_key=)[^;\\s&]+", RegexOptions.Compiled),
        new("(?i)(\"(?:Cookie|AccessToken|authorization|refresh_token|access_token|access_key|ac_time_value|SESSDATA|bili_jct)\"\\s*:\\s*\")[^\"]+", RegexOptions.Compiled),
        new("(?i)(://[^/\\s:@\"]+:)[^@\\s/\"]+(?=@)", RegexOptions.Compiled),
        new("(?i)((?:AccessToken|Authorization)\\s*=\\s*)[^;\\s&]+", RegexOptions.Compiled),
        new("(?i)(identify_v1\\s+)[^\\s,\";]+", RegexOptions.Compiled)
    ];

    public static string RedactSensitiveText(object? text)
    {
        var value = text?.ToString() ?? "";
        foreach (var pattern in SensitivePatterns)
        {
            value = pattern.Replace(value, "$1<redacted>");
        }
        return value;
    }

    public static void Log(object text, bool enter = true) => Write(text, null, enter: enter);

    public static void LogError(object text) => Write(text, ConsoleColor.Red);

    public static void LogColor(object text, bool time = true) => Write(text, ConsoleColor.Cyan, time);

    public static void LogWarn(object text, bool time = true) => Write(text, ConsoleColor.DarkYellow, time);

    public static void LogDebug(string toFormat, params object[] args)
    {
        if (!Config.DEBUG_LOG) return;
        var message = args.Length > 0 ? string.Format(toFormat, args).Trim() : toFormat;
        Write(message, ConsoleColor.DarkGray, colorTimestamp: true);
    }

    private static void Write(object text, ConsoleColor? color, bool time = true, bool enter = true, bool colorTimestamp = false)
    {
        lock (OutputLock)
        {
            activeProgressLine?.Clear();
            var prefix = time ? DateTime.Now.ToString("[yyyy-MM-dd HH:mm:ss.fff]") + " - " : new string(' ', TimestampWidth);
            if (color is { } foreground)
            {
                if (time && !colorTimestamp)
                {
                    Console.Write(prefix);
                    prefix = "";
                }
                Console.ForegroundColor = foreground;
            }
            Console.Write(prefix + RedactSensitiveText(text));
            if (color is not null) Console.ResetColor();
            if (enter) Console.WriteLine();
            logLineOpen = activeProgressLine is not null && !enter;
        }
    }
}
