using System.Text;
using static BBDownT.Core.Entity.Entity;

namespace BBDownT.Tests;

public class OutputNameLimitTests
{
    private static Page CreatePage() => new(1, "170001", "2", "", "P1", 0, "", 1);

    private static int Bytes(string value) => Encoding.UTF8.GetByteCount(value);

    [Theory]
    [InlineData(1, 1)]
    [InlineData(72, 72)]
    [InlineData(73, 72)]
    public void Titles_AreShortenedOnlyWhenTheyLeaveNoRoomForDerivedNames(int length, int kept)
    {
        var output = Program.FormatSavePath("<videoTitle>", new string('测', length), null, null, CreatePage(), 1, "WEB", 1);

        Assert.Equal(new string('测', kept) + ".mp4", output);
        Assert.True(Bytes(output) <= OutputNameLimit.MaxFileNameBytes);
    }

    [Fact]
    public void TemplateSuffixes_SurviveBecauseTheTitleIsShortenedFirst()
    {
        var page = CreatePage();

        var output = Program.FormatSavePath("<videoTitle> [<bvid>]", new string('测', 80), null, null, page, 1, "WEB", 1);

        Assert.EndsWith($" [{page.bvid}].mp4", output);
        Assert.StartsWith("测测测", output);
        Assert.True(Bytes(output) <= OutputNameLimit.MaxFileNameBytes);
    }

    [Fact]
    public void Directories_KeepTitlesUpToTheFileSystemLimit()
    {
        var title = new string('测', 80);

        var output = Program.FormatSavePath("<videoTitle>/[P<pageNumberWithZero>]<pageTitle>", title, null, null,
            CreatePage(), 1, "WEB", 1);

        Assert.Equal(title + "/[P1]P1.mp4", output);
    }

    [Fact]
    public void Truncation_NeverSplitsACharacterAndDropsTrailingDots()
    {
        var emoji = string.Concat(Enumerable.Repeat("😀", 70));
        var dotted = new string('测', 70) + new string('.', 20) + "尾";

        var directory = Program.FormatSavePath("<videoTitle>/x", emoji, null, null, CreatePage(), 1, "WEB", 1).Split('/')[0];
        var file = Program.FormatSavePath("<videoTitle>", dotted, null, null, CreatePage(), 1, "WEB", 1);

        Assert.True(Bytes(directory) <= OutputNameLimit.MaxDirectoryNameBytes);
        Assert.Equal(directory, Encoding.UTF8.GetString(Encoding.UTF8.GetBytes(directory)));
        Assert.Equal(string.Concat(Enumerable.Repeat("😀", 63)), directory);
        Assert.Equal(new string('测', 70) + ".mp4", file);
    }

    [Fact]
    public void OverlongLiteralTemplateText_IsTruncatedAsALastResort()
    {
        var output = Program.FormatSavePath(new string('a', 300) + "<videoTitle>", "title", null, null, CreatePage(), 1, "WEB", 1);

        Assert.EndsWith(".mp4", output);
        Assert.Equal(OutputNameLimit.MaxFileNameBytes, Bytes(output));
    }

    [Fact]
    public void MuxStaging_UsesAFixedLengthNameNextToLongDestinations()
    {
        using var files = new MediaTestDirectory();
        var destination = files.FilePath(new string('测', 80) + ".mp4");

        Assert.True(MediaOutput.Write(destination, path =>
        {
            Assert.Equal(Path.GetDirectoryName(destination), Path.GetDirectoryName(path));
            Assert.True(Bytes(Path.GetFileName(path)) < 64);
            File.WriteAllText(path, "media");
            return 0;
        }));

        Assert.Equal("media", File.ReadAllText(destination));
    }

    [Fact]
    public async Task ResumeState_SavesBesideAMaximumLengthName()
    {
        using var files = new MediaTestDirectory();
        var path = files.FilePath(new string('测', 80) + ".xml.tmp.resume");
        var state = new DownloadResumeState("identity", "source", 0, null, 10, true, 10, new string('0', 64),
            new DownloadResumeValidator(null, DateTimeOffset.UnixEpoch));

        await state.SaveAsync(path);

        Assert.Equal(255, Bytes(Path.GetFileName(path)));
        Assert.Equal(state, await DownloadResumeState.LoadAsync(path));
        Assert.Equal(new[] { path }, Directory.GetFiles(files.Root));
    }
}
