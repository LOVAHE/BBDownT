using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using static BBDownT.Core.Entity.Entity;
using static BBDownT.Core.Logger;

namespace BBDownT;

internal sealed class OutputOwnership(string indexPath)
{
    internal const string FileName = "BBDownT.outputs";

    internal static OutputOwnership Default { get; } = new(Path.Combine(Program.APP_DIR, FileName));

    private static readonly StringComparer PathComparer = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;

    private readonly object gate = new();
    private Dictionary<string, string>? owners;
    private long loadedLength = -1;

    internal string Resolve(string savePath, Page page, bool multiPage)
    {
        lock (gate)
        {
            if (!Program.IsUsableArtifact(savePath)) return savePath;
            var owner = Owners().GetValueOrDefault(Path.GetFullPath(savePath));
            if (owner is null || owner == Identity(page)) return savePath;

            var label = string.IsNullOrEmpty(page.bvid) ? page.DownloadId : page.bvid;
            var suffix = multiPage ? $" [{label} P{page.index}]" : $" [{label}]";
            var fileName = Path.GetFileName(savePath);
            var stem = Path.GetFileNameWithoutExtension(fileName);
            var language = Path.GetExtension(stem);
            if (!language.StartsWith(AudioLanguageSelection.LanguageSuffixPrefix, StringComparison.Ordinal)) language = "";
            var ending = suffix + language + Path.GetExtension(fileName);
            var title = OutputNameLimit.Shorten(stem[..^language.Length],
                OutputNameLimit.MaxFileNameBytes - Encoding.UTF8.GetByteCount(ending));
            var alternative = savePath[..^fileName.Length] + title + ending;
            Log($"{savePath}属于其他视频, 本视频将保存为: {alternative}");
            return alternative;
        }
    }

    internal void Record(string savePath, Page page)
    {
        var path = Path.GetFullPath(savePath);
        if (path.AsSpan().IndexOfAny('\r', '\n') >= 0) return;
        var identity = Identity(page);
        lock (gate)
        {
            var current = Owners();
            if (current.TryGetValue(path, out var owner) && owner == identity) return;
            try
            {
                File.AppendAllText(indexPath, $"{identity}\t{path}\n");
                current[path] = identity;
                loadedLength = IndexLength();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                LogDebug("记录输出文件失败: {0}", ex.Message);
            }
        }
    }

    private Dictionary<string, string> Owners()
    {
        var length = IndexLength();
        if (owners is not null && length == loadedLength) return owners;
        var loaded = new Dictionary<string, string>(PathComparer);
        if (length > 0)
        {
            try
            {
                foreach (var line in File.ReadLines(indexPath))
                {
                    var separator = line.IndexOf('\t');
                    if (separator > 0) loaded[line[(separator + 1)..]] = line[..separator];
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                LogDebug("读取{0}失败: {1}", FileName, ex.Message);
            }
        }
        owners = loaded;
        loadedLength = length;
        return loaded;
    }

    private long IndexLength()
    {
        try
        {
            var info = new FileInfo(indexPath);
            return info.Exists ? info.Length : 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    private static string Identity(Page page) => $"{page.DownloadId}:{page.cid}";
}
