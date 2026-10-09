using System;
using System.IO;

namespace BBDownT;

internal static class MediaOutput
{
    internal static bool Write(string destination, Func<string, int> write)
    {
        destination = Path.GetFullPath(destination);
        var directory = Path.GetDirectoryName(destination)!;
        Directory.CreateDirectory(directory);
        var staged = Path.Combine(directory, $".bbdownt-{Guid.NewGuid():N}.partial{Path.GetExtension(destination)}");
        try
        {
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
