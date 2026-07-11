using System.Text.RegularExpressions;

namespace BBDownT.Core;

public static class Logger
{
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
        new("(?i)(\"(?:Cookie|AccessToken|authorization)\"\\s*:\\s*\")[^\"]+", RegexOptions.Compiled),
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

    public static void Log(object text, bool enter = true)
    {
        Console.Write(DateTime.Now.ToString("[yyyy-MM-dd HH:mm:ss.fff]") + " - " + RedactSensitiveText(text));
        if (enter) Console.WriteLine();
    }

    public static void LogError(object text)
    {
        Console.Write(DateTime.Now.ToString("[yyyy-MM-dd HH:mm:ss.fff]") + " - ");
        Console.ForegroundColor = ConsoleColor.Red;
        Console.Write(RedactSensitiveText(text));
        Console.ResetColor();
        Console.WriteLine();
    }

    public static void LogColor(object text, bool time = true)
    {
        if (time)
            Console.Write(DateTime.Now.ToString("[yyyy-MM-dd HH:mm:ss.fff]") + " - ");
        Console.ForegroundColor = ConsoleColor.Cyan;
        if (time)
            Console.Write(RedactSensitiveText(text));
        else
            Console.Write("                            " + RedactSensitiveText(text));
        Console.ResetColor();
        Console.WriteLine();
    }

    public static void LogWarn(object text, bool time = true)
    {
        if (time)
            Console.Write(DateTime.Now.ToString("[yyyy-MM-dd HH:mm:ss.fff]") + " - ");
        Console.ForegroundColor = ConsoleColor.DarkYellow;
        if (time)
            Console.Write(RedactSensitiveText(text));
        else
            Console.Write("                            " + RedactSensitiveText(text));
        Console.ResetColor();
        Console.WriteLine();
    }

    public static void LogDebug(string toFormat, params object[] args)
    {
        if (Config.DEBUG_LOG)
        {
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.Write(DateTime.Now.ToString("[yyyy-MM-dd HH:mm:ss.fff]") + " - ");
            string message;
            if (args.Length > 0)
                message = string.Format(toFormat, args).Trim();
            else
                message = toFormat;
            Console.Write(RedactSensitiveText(message));
            Console.ResetColor();
            Console.WriteLine();
        }
    }
}
