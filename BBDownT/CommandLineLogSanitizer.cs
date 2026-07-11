using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace BBDownT;

internal static class CommandLineLogSanitizer
{
    private const string RedactedValue = "<redacted>";

    private static readonly HashSet<string> SensitiveOptions = new(StringComparer.OrdinalIgnoreCase)
    {
        "--cookie",
        "-c",
        "--access-token",
        "-token",
        "--api-token"
    };

    public static string Format(IEnumerable<string> arguments)
    {
        var sanitized = new List<string>();
        var redactNext = false;

        foreach (var argument in arguments)
        {
            if (redactNext)
            {
                sanitized.Add(RedactedValue);
                redactNext = false;
                continue;
            }

            var equalsIndex = argument.IndexOf('=');
            var optionName = equalsIndex >= 0 ? argument[..equalsIndex] : argument;
            if (!SensitiveOptions.Contains(optionName))
            {
                sanitized.Add(argument);
                continue;
            }

            if (equalsIndex >= 0)
            {
                sanitized.Add($"{optionName}={RedactedValue}");
            }
            else
            {
                sanitized.Add(argument);
                redactNext = true;
            }
        }

        return string.Join(' ', sanitized);
    }

    public static string SanitizeText(string text)
    {
        return Regex.Replace(
            text,
            "(?i)(--cookie|-c|--access-token|-token|--api-token)(\\s+|=)(?:\\\"[^\\\"]*\\\"|'[^']*'|\\S+)",
            "$1$2<redacted>");
    }
}
