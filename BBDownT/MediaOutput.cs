using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace BBDownT;

internal static class MediaOutput
{
    internal static bool Write(string destination, Func<string, int> write)
    {
        destination = Path.GetFullPath(destination);
        var directory = Path.GetDirectoryName(destination)!;
        Directory.CreateDirectory(directory);
        var staged = StagedPath(destination);
        try
        {
            File.Delete(staged);
            if (write(staged) != 0 || !File.Exists(staged) || new FileInfo(staged).Length == 0)
                return false;
            File.Move(staged, destination, overwrite: true);
            return true;
        }
        finally
        {
            if (File.Exists(staged)) File.Delete(staged);
        }
    }

    internal static string StagedPath(string destination)
    {
        destination = Path.GetFullPath(destination);
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(destination)))[..32].ToLowerInvariant();
        return Path.Combine(Path.GetDirectoryName(destination)!, $".bbdownt-{key}.partial{Path.GetExtension(destination)}");
    }

    internal static void DeleteInput(string input, string destination, Action<string>? delete = null)
    {
        if (string.IsNullOrEmpty(input)) return;
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!string.Equals(Path.GetFullPath(input), Path.GetFullPath(destination), comparison))
        {
            var remove = delete ?? File.Delete;
            remove(input);
            remove(input + ".resume");
        }
    }
}
