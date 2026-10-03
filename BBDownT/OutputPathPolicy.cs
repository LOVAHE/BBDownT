using System;
using System.IO;

namespace BBDownT;

internal static class OutputPathPolicy
{
    internal static string Resolve(string path, string? restrictedRoot, Func<string, bool>? isLink = null)
    {
        // CLI and explicitly unrestricted servers retain absolute-path support.
        if (restrictedRoot is null) return path;
        if (Path.IsPathRooted(path))
            throw new ArgumentException("服务器输出模板展开后必须是相对路径");

        return ValidateContained(Path.GetFullPath(path, Path.GetFullPath(restrictedRoot)), restrictedRoot, isLink);
    }

    internal static string ResolveArtifact(string path, string? restrictedRoot, Func<string, bool>? isLink = null)
        => restrictedRoot is null ? path : ValidateContained(Path.GetFullPath(path), restrictedRoot, isLink);

    private static string ValidateContained(string resolved, string restrictedRoot, Func<string, bool>? isLink)
    {
        var root = Path.GetFullPath(restrictedRoot);
        var relative = Path.GetRelativePath(root, resolved);
        if (relative == ".." || Path.IsPathRooted(relative)
            || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            || relative.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal))
            throw new ArgumentException("服务器输出模板展开后的路径不能超出下载目录");

        // The configured root itself may be an administrator-selected link.
        // Descendant links/junctions must not redirect an accepted output path.
        var current = root;
        foreach (var part in relative.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries))
        {
            if (part == ".") continue;
            current = Path.Combine(current, part);
            if ((isLink ?? IsReparsePoint)(current))
                throw new ArgumentException("服务器输出路径不能经过符号链接或目录链接");
        }
        return resolved;
    }

    private static bool IsReparsePoint(string path)
    {
        try { return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0; }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
    }
}
