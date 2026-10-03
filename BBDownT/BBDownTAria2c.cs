using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using BBDownT.Core.Util;

namespace BBDownT;

static class BBDownTAria2c
{
    public static string ARIA2C = "aria2c";

    public static async Task<int> RunCommandCodeAsync(string command, string args)
    {
        using Process p = new();
        p.StartInfo.UseShellExecute = false;
        p.StartInfo.RedirectStandardOutput = false;
        p.StartInfo.FileName = command;
        p.StartInfo.Arguments = args;
        p.Start();
        await p.WaitForExitAsync();
        return p.ExitCode;
    }

    public static async Task<int> DownloadFileByAria2cAsync(string url, string path, string extraArgs)
        => await RunCommandCodeAsync(ARIA2C, BuildDownloadArguments(url, path, extraArgs));

    internal static string BuildDownloadArguments(string url, string path, string extraArgs,
        bool? international = null)
    {
        var headerArgs = "";
        var intl = international ?? BBDownT.Core.Config.COOKIE_IS_INTL;
        var referer = MediaRequestPolicy.GetReferer(url, intl);
        if (referer is not null) headerArgs += $" --header=\"Referer: {referer}\"";
        headerArgs += " --header=\"User-Agent: Mozilla/5.0\"";
        if (!intl && HTTPUtil.ShouldSendCookie(url))
            headerArgs += $" --header=\"Cookie: {HTTPUtil.GetCookieHeaderValue(url)}\"";
        return $" --auto-file-renaming=false --download-result=hide --allow-overwrite=true --console-log-level=warn -x16 -s16 -j16 -k5M {headerArgs} {extraArgs} \"{url}\" -d \"{Path.GetDirectoryName(path)}\" -o \"{Path.GetFileName(path)}\"";
    }
}
