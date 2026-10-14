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

    private readonly object gate = new();
    private Func<string, bool>? artifactExistsOverride;
    private Func<string, string?>? ownerLookupOverride;
    private Dictionary<string, string>? owners;
    private long loadedLength = -1;

    internal OutputOwnership(string indexPath, Func<string, bool> artifactExists,
        Func<string, string?> ownerLookup) : this(indexPath)
    {
        artifactExistsOverride = artifactExists;
        ownerLookupOverride = ownerLookup;
    }

    internal string Resolve(string savePath, Page page, bool multiPage)
    {
        lock (gate)
        {
            if (!IsUsableArtifact(savePath)) return savePath;
            var owner = OwnerOf(savePath);
            if (owner is null || owner == Identity(page)) return savePath;

            var label = string.IsNullOrEmpty(page.bvid) ? page.DownloadId : page.bvid;
            var suffix = multiPage ? $" [{label} P{page.index}]" : $" [{label}]";
            var fileName = Path.GetFileName(savePath);
            var stem = Path.GetFileNameWithoutExtension(fileName);
            var marker = stem.EndsWith(AudioLanguageSelection.VideoOnlyMarker, StringComparison.Ordinal)
                ? AudioLanguageSelection.VideoOnlyMarker : "";
            stem = stem[..^marker.Length];
            var language = Path.GetExtension(stem);
            if (!language.StartsWith(AudioLanguageSelection.LanguageSuffixPrefix, StringComparison.Ordinal)) language = "";
            var titleStem = stem[..^language.Length];
            var extension = Path.GetExtension(fileName);
            var directory = savePath[..^fileName.Length];
            for (var sequence = 1; ; sequence++)
            {
                var disambiguator = sequence == 1 ? suffix : $"{suffix} ({sequence})";
                var ending = disambiguator + language + marker + extension;
                var title = OutputNameLimit.Shorten(titleStem,
                    OutputNameLimit.MaxFileNameBytes - Encoding.UTF8.GetByteCount(ending));
                var alternative = directory + title + ending;
                var alternativeOwner = IsUsableArtifact(alternative) ? OwnerOf(alternative) : null;
                if (alternativeOwner is null || alternativeOwner == Identity(page))
                {
                    Log($"{savePath}属于其他视频, 本视频将保存为: {alternative}");
                    return alternative;
                }
            }
        }
    }

    internal bool IsOwnedByAnother(string path, Page page)
    {
        lock (gate)
        {
            var owner = OwnerOf(path);
            return owner is not null && owner != Identity(page);
        }
    }

    private bool IsUsableArtifact(string path) => artifactExistsOverride?.Invoke(path) ?? Program.IsUsableArtifact(path);

    private string? OwnerOf(string path)
    {
        var fullPath = Path.GetFullPath(path);
        return ownerLookupOverride is not null
            ? ownerLookupOverride(fullPath)
            : Owners().GetValueOrDefault(fullPath);
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
                var line = $"{identity}\t{path}\n";
                var expectedLength = loadedLength + Encoding.UTF8.GetByteCount(line);
                File.AppendAllText(indexPath, line);
                if (ReferenceEquals(current, owners) && IndexLength() == expectedLength)
                {
                    current[path] = identity;
                    loadedLength = expectedLength;
                }
                else owners = null;
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
        var loaded = new Dictionary<string, string>(OutputPathPolicy.PathComparer);
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
                return loaded;
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
