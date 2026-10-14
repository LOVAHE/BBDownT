using System.Runtime.Versioning;
using System.Text;
using System.Collections.Generic;
using static BBDownT.Core.Entity.Entity;

namespace BBDownT.Tests;

public class OutputOwnershipTests
{
    private static Page CreatePage(string aid, string cid, int index = 1) => new(index, aid, cid, "", "P" + index, 0, 1);

    [Fact]
    public void AnotherVideoWithTheSameName_GetsANameWithItsBvid()
    {
        using var files = new MediaTestDirectory();
        var ownership = new OutputOwnership(files.FilePath("outputs"));
        var first = CreatePage("170001", "1");
        var second = CreatePage("170002", "2");
        var path = files.Write("title.mp4", "first video");
        ownership.Record(path, first);

        var resolved = ownership.Resolve(path, second, multiPage: false);

        Assert.Equal(files.FilePath($"title [{second.bvid}].mp4"), resolved);
        Assert.Equal(path, ownership.Resolve(path, first, multiPage: false));
    }

    [Fact]
    public void AnotherPageOfTheSameVideo_GetsANameWithItsPageNumber()
    {
        using var files = new MediaTestDirectory();
        var ownership = new OutputOwnership(files.FilePath("outputs"));
        var path = files.Write("title.mp4", "page one");
        ownership.Record(path, CreatePage("170001", "1"));
        var second = CreatePage("170001", "2", index: 2);

        Assert.Equal(files.FilePath($"title [{second.bvid} P2].mp4"), ownership.Resolve(path, second, multiPage: true));
    }

    [Fact]
    public void UnrecordedFilesAndFreeNames_KeepTheRequestedPath()
    {
        using var files = new MediaTestDirectory();
        var ownership = new OutputOwnership(files.FilePath("outputs"));
        var page = CreatePage("170001", "1");
        var free = files.FilePath("free.mp4");
        var legacy = files.Write("legacy.mp4", "downloaded before ownership records");

        Assert.Equal(free, ownership.Resolve(free, page, multiPage: false));
        Assert.Equal(legacy, ownership.Resolve(legacy, page, multiPage: false));
    }

    [Fact]
    public void ANameWhoseFileWasRemoved_CanBeReused()
    {
        using var files = new MediaTestDirectory();
        var ownership = new OutputOwnership(files.FilePath("outputs"));
        var path = files.Write("title.mp4", "first video");
        ownership.Record(path, CreatePage("170001", "1"));
        File.Delete(path);

        Assert.Equal(path, ownership.Resolve(path, CreatePage("170002", "2"), multiPage: false));
    }

    [Fact]
    public void Records_ArePersistedForLaterRuns()
    {
        using var files = new MediaTestDirectory();
        var index = files.FilePath("outputs");
        var path = files.Write("title.mp4", "first video");
        new OutputOwnership(index).Record(path, CreatePage("170001", "1"));
        var second = CreatePage("170002", "2");

        Assert.Equal(files.FilePath($"title [{second.bvid}].mp4"), new OutputOwnership(index).Resolve(path, second, multiPage: false));
    }

    [Fact]
    public void LongNames_StayWithinTheFileNameLimit()
    {
        using var files = new MediaTestDirectory();
        var ownership = new OutputOwnership(files.FilePath("outputs"));
        var name = new string('测', 72) + ".mp4";
        var path = files.Write(name, "first video");
        ownership.Record(path, CreatePage("170001", "1"));

        var resolved = ownership.Resolve(path, CreatePage("170002", "2", index: 12), multiPage: true);

        Assert.EndsWith(" P12].mp4", resolved);
        Assert.True(Encoding.UTF8.GetByteCount(Path.GetFileName(resolved)) <= OutputNameLimit.MaxFileNameBytes);
    }

    [Fact]
    public void ShortenedNames_KeepTheVideoOnlyMarker()
    {
        using var files = new MediaTestDirectory();
        var ownership = new OutputOwnership(files.FilePath("outputs"));
        var title = new string('测', 72);
        var videoOnly = files.Write(title + ".audio-ja.video.mp4", "another video");
        var full = files.Write(title + ".audio-ja.mp4", "another video");
        ownership.Record(videoOnly, CreatePage("170001", "1"));
        ownership.Record(full, CreatePage("170001", "1"));
        var page = CreatePage("170002", "2");

        var resolvedVideoOnly = ownership.Resolve(videoOnly, page, multiPage: false);
        var resolvedFull = ownership.Resolve(full, page, multiPage: false);

        Assert.EndsWith($" [{page.bvid}].audio-ja.video.mp4", resolvedVideoOnly);
        Assert.EndsWith($" [{page.bvid}].audio-ja.mp4", resolvedFull);
        Assert.True(Encoding.UTF8.GetByteCount(Path.GetFileName(resolvedVideoOnly)) <= OutputNameLimit.MaxFileNameBytes);
    }

    [Fact]
    public void ShortenedNames_KeepTheDubLanguageSuffix()
    {
        using var files = new MediaTestDirectory();
        var ownership = new OutputOwnership(files.FilePath("outputs"));
        var title = new string('测', 72);
        var japanese = files.Write(title + ".audio-ja.mp4", "another video");
        var original = files.Write(title + ".mp4", "another video");
        ownership.Record(japanese, CreatePage("170001", "1"));
        ownership.Record(original, CreatePage("170001", "1"));
        var page = CreatePage("170002", "2");

        var dub = ownership.Resolve(japanese, page, multiPage: false);
        var standard = ownership.Resolve(original, page, multiPage: false);

        Assert.EndsWith($" [{page.bvid}].audio-ja.mp4", dub);
        Assert.EndsWith($" [{page.bvid}].mp4", standard);
        Assert.NotEqual(dub, standard);
        Assert.True(Encoding.UTF8.GetByteCount(Path.GetFileName(dub)) <= OutputNameLimit.MaxFileNameBytes);
    }

    [Fact]
    public void AnUnwritableIndex_DoesNotFailTheDownload()
    {
        using var files = new MediaTestDirectory();
        var blocker = files.Write("blocker", "not a directory");
        var ownership = new OutputOwnership(Path.Combine(blocker, "outputs"));
        var path = files.Write("title.mp4", "video");

        ownership.Record(path, CreatePage("170001", "1"));

        Assert.Equal(path, ownership.Resolve(path, CreatePage("170002", "2"), multiPage: false));
    }

    [UnixNonRootFact]
    [UnsupportedOSPlatform("windows")]
    public void AnIndexThatCouldNotBeRead_IsReadAgainLater()
    {
        using var files = new MediaTestDirectory();
        var index = files.FilePath("outputs");
        var path = files.Write("title.mp4", "first video");
        var ownership = new OutputOwnership(index);
        var second = CreatePage("170002", "2");
        Assert.Equal(path, ownership.Resolve(path, second, multiPage: false));
        new OutputOwnership(index).Record(path, CreatePage("170001", "1"));
        var mode = File.GetUnixFileMode(index);
        try
        {
            File.SetUnixFileMode(index, UnixFileMode.None);
            Assert.Equal(path, ownership.Resolve(path, second, multiPage: false));
        }
        finally
        {
            File.SetUnixFileMode(index, mode);
        }

        Assert.Equal(files.FilePath($"title [{second.bvid}].mp4"), ownership.Resolve(path, second, multiPage: false));
    }

    [Fact]
    public void ConflictingAlternativeNames_AreCheckedUntilAnUnownedNameIsFound()
    {
        var first = CreatePage("170001", "1");
        var second = CreatePage("170002", "2");
        var original = Path.GetFullPath("ownership-title.mp4");
        var firstAlternative = Path.GetFullPath($"ownership-title [{second.bvid}].mp4");
        var owners = new Dictionary<string, string>
        {
            [original] = Owner(first),
            [firstAlternative] = Owner(first)
        };
        var ownership = InMemoryOwnership(owners);

        Assert.Equal(Path.GetFullPath($"ownership-title [{second.bvid}] (2).mp4"),
            ownership.Resolve(original, second, multiPage: false));
    }

    [Fact]
    public void MultipleConflictingAlternatives_AreSkippedWithoutRepeatingSuffixes()
    {
        var first = CreatePage("170001", "1");
        var second = CreatePage("170002", "2");
        var original = Path.GetFullPath("ownership-title.mp4");
        var owners = new Dictionary<string, string>
        {
            [original] = Owner(first),
            [Path.GetFullPath($"ownership-title [{second.bvid}].mp4")] = Owner(first),
            [Path.GetFullPath($"ownership-title [{second.bvid}] (2).mp4")] = Owner(first)
        };

        Assert.Equal(Path.GetFullPath($"ownership-title [{second.bvid}] (3).mp4"),
            InMemoryOwnership(owners).Resolve(original, second, multiPage: false));
    }

    [Fact]
    public void AlternativeAlreadyOwnedBySamePage_CanBeReused()
    {
        var first = CreatePage("170001", "1");
        var second = CreatePage("170002", "2");
        var original = Path.GetFullPath("ownership-title.mp4");
        var alternative = Path.GetFullPath($"ownership-title [{second.bvid}].mp4");
        var owners = new Dictionary<string, string>
        {
            [original] = Owner(first),
            [alternative] = Owner(second)
        };

        Assert.Equal(alternative, InMemoryOwnership(owners).Resolve(original, second, multiPage: false));
    }

    [Fact]
    public void UnknownOwnerKeepsRequestedPath_AndUnusablePathIgnoresStaleOwner()
    {
        var page = CreatePage("170002", "2");
        var unknown = Path.GetFullPath("unknown-owner.mp4");
        var stale = Path.GetFullPath("deleted-output.mp4");
        var owners = new Dictionary<string, string> { [stale] = Owner(CreatePage("170001", "1")) };

        Assert.Equal(unknown, InMemoryOwnership(owners).Resolve(unknown, page, multiPage: false));
        Assert.Equal(stale, InMemoryOwnership(owners, _ => false).Resolve(stale, page, multiPage: false));
    }

    [Fact]
    public void DeletedAlternative_IgnoresItsStaleOwnershipRecord()
    {
        var first = CreatePage("170001", "1");
        var second = CreatePage("170002", "2");
        var original = Path.GetFullPath("ownership-title.mp4");
        var alternative = Path.GetFullPath($"ownership-title [{second.bvid}].mp4");
        var owners = new Dictionary<string, string>
        {
            [original] = Owner(first),
            [alternative] = Owner(first)
        };

        var ownership = InMemoryOwnership(owners, path => path == original);

        Assert.Equal(alternative, ownership.Resolve(original, second, multiPage: false));
    }

    [Fact]
    public void ConflictingLanguageNames_KeepLanguageSuffixAndRespectByteLimit()
    {
        var first = CreatePage("170001", "1");
        var second = CreatePage("170002", "2");
        var original = Path.GetFullPath(new string('测', 72) + ".audio-ja.mp4");
        var firstAlternative = Path.GetFullPath(new string('测', 64) + $" [{second.bvid}].audio-ja.mp4");
        var owners = new Dictionary<string, string>
        {
            [original] = Owner(first),
            [firstAlternative] = Owner(first)
        };

        var resolved = InMemoryOwnership(owners).Resolve(original, second, multiPage: false);

        Assert.EndsWith($" [{second.bvid}] (2).audio-ja.mp4", resolved);
        Assert.True(Encoding.UTF8.GetByteCount(Path.GetFileName(resolved)) <= OutputNameLimit.MaxFileNameBytes);
    }

    [Fact]
    public void SameAidDifferentContentId_IsAConflictingPage()
    {
        var first = CreatePage("170001", "1", index: 1);
        var second = CreatePage("170001", "2", index: 2);
        var original = Path.GetFullPath("multi-page.mp4");
        var owners = new Dictionary<string, string> { [original] = Owner(first) };

        Assert.Equal(Path.GetFullPath($"multi-page [{second.bvid} P2].mp4"),
            InMemoryOwnership(owners).Resolve(original, second, multiPage: true));
    }

    private static OutputOwnership InMemoryOwnership(Dictionary<string, string> owners,
        Func<string, bool>? isUsable = null) =>
        new("unused-index", isUsable ?? (_ => true), path => owners.GetValueOrDefault(Path.GetFullPath(path)));

    private static string Owner(Page page) => $"{page.DownloadId}:{page.cid}";
}
