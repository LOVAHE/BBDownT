namespace BBDownT.Tests;

// File tests share ownership bookkeeping, not business fixtures or assertions.
internal sealed class MediaTestDirectory : IDisposable
{
    private readonly HashSet<string> files = [];
    private readonly List<string> directories = [];
    internal string Root { get; } = Path.Combine(Path.GetTempPath(), "bbdownt-media-" + Guid.NewGuid().ToString("N"));

    internal MediaTestDirectory()
    {
        Directory.CreateDirectory(Root);
        directories.Add(Root);
    }

    internal string FilePath(string name)
    {
        var path = Path.Combine(Root, name);
        files.Add(path);
        return path;
    }

    internal string Write(string name, string contents)
    {
        var path = FilePath(name);
        File.WriteAllText(path, contents);
        return path;
    }

    internal string CreateDirectory(string name)
    {
        var path = Path.Combine(Root, name);
        Directory.CreateDirectory(path);
        directories.Add(path);
        return path;
    }

    public void Dispose()
    {
        foreach (var file in files) File.Delete(file);
        if (!Directory.Exists(Root)) return;
        var unregistered = Directory.EnumerateFileSystemEntries(Root, "*", SearchOption.AllDirectories)
            .Where(entry => !directories.Contains(entry))
            .Select(entry => Path.GetRelativePath(Root, entry))
            .Order()
            .ToList();
        Directory.Delete(Root, recursive: true);
        if (unregistered.Count > 0)
            throw new IOException("Unregistered test artifacts: " + string.Join(", ", unregistered));
    }
}
