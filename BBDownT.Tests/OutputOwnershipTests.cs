using System.Text;
using static BBDownT.Core.Entity.Entity;

namespace BBDownT.Tests;

public class OutputOwnershipTests
{
    private static Page CreatePage(string aid, string cid, int index = 1) => new(index, aid, cid, "", "P" + index, 0, "", 1);

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
}
