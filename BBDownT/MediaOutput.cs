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
        using var writeLock = AcquireWriteLock(staged, destination);
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

    private static FileStream AcquireWriteLock(string staged, string destination)
    {
        try
        {
            return new FileStream(staged + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1,
                FileOptions.DeleteOnClose);
        }
        catch (IOException ex) when (IsLockConflict(ex))
        {
            throw new IOException($"另一个BBDownT进程正在写入{destination}", ex);
        }
    }

    internal static bool IsLockConflict(Exception error)
    {
        for (var cause = error; cause is not null; cause = cause.InnerException)
        {
            if (cause.GetType() != typeof(IOException)) continue;
            var code = cause.HResult;
            if (OperatingSystem.IsWindows() ? code is unchecked((int)0x80070020) or unchecked((int)0x80070021)
                : code == (OperatingSystem.IsLinux() ? 11 : 35))
                return true;
        }
        return false;
    }

    internal static void DeleteInput(string input, string destination, Action<string>? delete = null)
    {
        if (string.IsNullOrEmpty(input)) return;
        if (!string.Equals(Path.GetFullPath(input), Path.GetFullPath(destination), OutputPathPolicy.PathComparison))
        {
            var remove = delete ?? File.Delete;
            remove(input);
            remove(input + ".resume");
        }
    }
}
