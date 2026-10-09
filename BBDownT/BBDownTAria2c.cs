using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading.Tasks;

namespace BBDownT;

static class BBDownTAria2c
{
    public static string ARIA2C = "aria2c";

    public static async Task<int> RunCommandCodeAsync(string command, IEnumerable<string> args)
    {
        using Process p = new();
        p.StartInfo.UseShellExecute = false;
        p.StartInfo.RedirectStandardOutput = false;
        p.StartInfo.FileName = command;
        foreach (var arg in args) p.StartInfo.ArgumentList.Add(arg);
        p.Start();
        await p.WaitForExitAsync();
        return p.ExitCode;
    }

    public static async Task<int> DownloadFileByAria2cAsync(string url, string path, string extraArgs)
        => await RunCommandCodeAsync(ARIA2C, BuildDownloadArguments(url, path, extraArgs));

    internal static List<string> BuildDownloadArguments(string url, string path, string extraArgs,
        bool? international = null)
    {
        var intl = international ?? BBDownT.Core.Config.COOKIE_IS_INTL;
        List<string> args = ["--auto-file-renaming=false", "--download-result=hide", "--allow-overwrite=true",
            "--console-log-level=warn", "-x16", "-s16", "-j16", "-k5M"];
        var referer = MediaRequestPolicy.GetReferer(url, intl);
        if (referer is not null) args.Add($"--header=Referer: {referer}");
        args.Add("--header=User-Agent: Mozilla/5.0");
        args.AddRange(SplitArguments(extraArgs ?? ""));
        args.AddRange([url, "-d", Path.GetDirectoryName(path) ?? "", "-o", Path.GetFileName(path)]);
        return args;
    }

    internal static List<string> SplitArguments(string arguments)
    {
        var results = new List<string>();
        for (var i = 0; i < arguments.Length; i++)
        {
            while (i < arguments.Length && arguments[i] is ' ' or '\t') i++;
            if (i == arguments.Length) break;
            results.Add(ReadArgument(arguments, ref i));
        }
        return results;
    }

    private static string ReadArgument(string arguments, ref int i)
    {
        var current = new StringBuilder();
        var inQuotes = false;
        while (i < arguments.Length)
        {
            var backslashes = 0;
            while (i < arguments.Length && arguments[i] == '\\')
            {
                i++;
                backslashes++;
            }
            if (backslashes > 0)
            {
                if (i >= arguments.Length || arguments[i] != '"')
                {
                    current.Append('\\', backslashes);
                }
                else
                {
                    current.Append('\\', backslashes / 2);
                    if (backslashes % 2 != 0)
                    {
                        current.Append('"');
                        i++;
                    }
                }
                continue;
            }

            var c = arguments[i];
            if (c == '"')
            {
                if (inQuotes && i < arguments.Length - 1 && arguments[i + 1] == '"')
                {
                    current.Append('"');
                    i++;
                }
                else
                {
                    inQuotes = !inQuotes;
                }
                i++;
                continue;
            }
            if (c is ' ' or '\t' && !inQuotes) break;
            current.Append(c);
            i++;
        }
        return current.ToString();
    }
}
