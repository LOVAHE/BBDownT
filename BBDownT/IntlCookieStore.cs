using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace BBDownT;

internal static class IntlCookieStore
{
    internal const string FileName = "BBDownTIntl.data";

    internal static string Normalize(string input)
    {
        var cookie = input.Trim();
        if (cookie.StartsWith("Cookie:", StringComparison.OrdinalIgnoreCase)) cookie = cookie[7..].Trim();
        if (cookie.Length == 0 || cookie.Length > 16384 || cookie.Any(character => char.IsControl(character)))
            throw new ArgumentException("请输入完整的国际站 Cookie 请求头，不要包含换行");

        var pairs = cookie.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (pairs.Length == 0) throw new ArgumentException("国际站 Cookie 不能为空");
        foreach (var pair in pairs)
        {
            var equals = pair.IndexOf('=');
            if (equals < 1 || pair[..equals].Any(character => !char.IsAsciiLetterOrDigit(character)
                && !"!#$%&'*+-.^_`|~".Contains(character)))
                throw new ArgumentException("国际站 Cookie 格式无效，应为 name=value; name=value");
        }
        return string.Join("; ", pairs);
    }

    internal static (string Cookie, string? FilePath) Load(
        string currentCookie, string directory, bool international,
        Func<string, bool>? exists = null, Func<string, string>? read = null)
    {
        if (!string.IsNullOrEmpty(currentCookie)) return (currentCookie, null);
        var path = Path.Combine(directory, international ? FileName : "BBDownT.data");
        if (!(exists ?? File.Exists)(path)) return ("", null);
        var cookie = (read ?? File.ReadAllText)(path);
        return (international ? Normalize(cookie) : cookie, path);
    }

    internal static Task SaveAsync(string directory, string input, Func<string, string, Task>? write = null)
    {
        var cookie = Normalize(input);
        return BBDownTLoginUtil.SaveLoginDataAsync(directory, FileName, cookie, write);
    }
}
