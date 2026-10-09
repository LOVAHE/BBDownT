using System.Text.Json;
using static BBDownT.Core.Entity.Entity;

namespace BBDownT.Tests;

public class OutputPathPolicyTests
{
    [Theory]
    [InlineData("link", "link/out.mp4")]
    [InlineData("nested/link", "nested/link/out.mp4")]
    [InlineData("out.mp4", "out.mp4")]
    public void RestrictedOutput_RejectsExistingDirectoryAndFileLinks(string linkedRelative, string output)
    {
        var root = Path.Combine(Environment.CurrentDirectory, "synthetic-root");
        var linked = Path.Combine(root, linkedRelative.Replace('/', Path.DirectorySeparatorChar));
        var error = Assert.Throws<ArgumentException>(() => OutputPathPolicy.Resolve(output, root, path => path == linked));
        Assert.Contains("链接", error.Message);
    }

    [Theory]
    [InlineData("intl_13287667/video.tmp")]
    public void RestrictedCacheArtifacts_RejectLinksBeforeTemporaryOrSubtitleWrites(string relative)
    {
        var root = Path.Combine(Environment.CurrentDirectory, "synthetic-root");
        var path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
        Assert.Throws<ArgumentException>(() => OutputPathPolicy.ResolveArtifact(path, root, candidate => candidate == path));
        var linkedDirectory = Path.Combine(root, "intl_13287667");
        Assert.Throws<ArgumentException>(() => OutputPathPolicy.ResolveArtifact(path, root, candidate => candidate == linkedDirectory));
    }

    [Fact]
    public void RestrictedOutput_ChecksEachDescendantWhileAllowingTheConfiguredRootItself()
    {
        var root = Path.Combine(Environment.CurrentDirectory, "synthetic-root");
        var checkedPaths = new List<string>();
        var result = OutputPathPolicy.Resolve("nested/out.mp4", root, path =>
        {
            checkedPaths.Add(path);
            return path == root;
        });
        Assert.Equal(Path.Combine(root, "nested", "out.mp4"), result);
        Assert.Equal(new[] { Path.Combine(root, "nested"), result }, checkedPaths);
        Assert.Equal(root, OutputPathPolicy.ResolveArtifact(root, root, _ => throw new Exception("No descendant exists")));
    }

    [Fact]
    public void UnrestrictedOutput_DoesNotApplyServerLinkRestrictions()
    {
        Assert.Equal("link/out.mp4", OutputPathPolicy.Resolve("link/out.mp4", null, _ => throw new Exception("CLI is unrestricted")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ChapterMetadata_RejectsLinkedChapterFileForVideoAndAudioOnlyOutputs(bool audioOnly)
    {
        var root = Path.Combine(Environment.CurrentDirectory, "synthetic-root");
        var media = Path.Combine(root, "123", "track.mp4");
        var chapter = Path.Combine(root, "123", "chapters");
        Assert.Throws<ArgumentException>(() => BBDownTMuxer.GetChapterPath(audioOnly ? "" : media,
            audioOnly ? media : "", root, path => path == chapter));
    }

    [Fact]
    public void DebugResponseOutput_RejectsLinkedJsonDestination()
    {
        var root = Path.Combine(Environment.CurrentDirectory, "synthetic-root");
        var debug = Path.Combine(root, "debug_20261003000000000.json");
        Assert.Throws<ArgumentException>(() => OutputPathPolicy.ResolveArtifact(debug, root, path => path == debug));
    }

    [Fact]
    public async Task SpaceListExport_RejectsLinkedDestinationBeforeCallingItsWriter()
    {
        var root = Path.Combine(Environment.CurrentDirectory, "synthetic-root");
        var export = Path.Combine(root, "UP的投稿视频.txt");
        var calls = 0;
        await Assert.ThrowsAsync<ArgumentException>(() => Program.WriteSpaceExportAsync(export, "synthetic-video-url", root,
            (_, _) => { calls++; return Task.CompletedTask; }, path => path == export));
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task SpaceListExport_WritesUnlinkedDestinationWithinTheRoot()
    {
        var root = Path.Combine(Environment.CurrentDirectory, "synthetic-root");
        var export = Path.Combine(root, "UP的投稿视频.txt");
        var calls = 0;
        await Program.WriteSpaceExportAsync(export, "synthetic-video-url", root, (path, content) =>
        {
            Assert.Equal(export, path);
            Assert.Equal("synthetic-video-url", content);
            calls++;
            return Task.CompletedTask;
        }, _ => false);
        Assert.Equal(1, calls);
    }

    [Theory]
    [InlineData(true, false, true, "INTL")]
    [InlineData(false, true, true, "INTL")]
    [InlineData(true, true, true, "INTL")]
    [InlineData(true, true, false, "APP")]
    [InlineData(true, false, false, "TV")]
    [InlineData(false, true, false, "APP")]
    [InlineData(false, false, false, "WEB")]
    public void FileTemplateApiMode_MatchesTheActualRequestPriority(bool tv, bool app, bool intl, string expected)
    {
        var option = new MyOption { UseTvApi = tv, UseAppApi = app, UseIntlApi = intl };
        Assert.Equal(expected, Program.GetApiType(option));
    }

    private static Page InternationalPage(string episode = "13287667") => new(1, "", "", episode, "E1", 0, "", 1);

    [Theory]
    [InlineData("<aid>_<cid>", "intl_13287667_13287667.mp4")]
    [InlineData("<bvid>/target", "intl_13287667/target.mp4")]
    [InlineData("<epid>", "13287667.mp4")]
    [InlineData("<episodeId>", "13287667.mp4")]
    public void InternationalIdTemplates_UseStableEpisodeNamesWithoutInventingMetadata(string template, string expected)
    {
        var page = InternationalPage();
        var output = Program.FormatSavePath(template, "LINK CLICK", null, null, page, 24, "INTL", 1);
        Assert.Equal(expected, output);
        Assert.False(Path.IsPathRooted(output));
        Assert.Equal("", page.aid);
        Assert.Equal("", page.cid);
        Assert.Equal("", page.bvid);
    }

    [Theory]
    [InlineData("<aid>/target")]
    [InlineData("<cid>")]
    [InlineData("<bvid>")]
    public void InternationalIdTemplates_DoNotCollideBetweenEpisodes(string template)
    {
        var first = Program.FormatSavePath(template, "LINK CLICK", null, null, InternationalPage(), 24, "INTL", 1);
        var second = Program.FormatSavePath(template, "LINK CLICK", null, null, InternationalPage("13287745"), 24, "INTL", 1);
        Assert.NotEqual(first, second);
    }

    [Fact]
    public void DefaultServer_ConstrainsExpandedInternationalCoverDestinationToDownloadRoot()
    {
        var request = new ServeRequestOptions
        {
            Url = "https://www.bilibili.tv/play/2110869", UseIntlApi = true, CoverOnly = true,
            MultiFilePattern = "<aid>/target"
        };
        var server = new BBDownTApiServer();
        Assert.Null(server.ValidateAndNormalizeServerRequest(request));
        Assert.Equal(request.WorkDir, request.RestrictedOutputRoot);

        var media = Program.FormatSavePath(request.MultiFilePattern, "LINK CLICK", null, null,
            InternationalPage(), 24, "INTL", 1, request.RestrictedOutputRoot);
        var cover = Path.ChangeExtension(media, ".png");
        Assert.Equal(Path.Combine(request.RestrictedOutputRoot!, "intl_13287667", "target.png"), cover);
        Assert.Equal(Path.Combine("intl_13287667", "target.png"), Path.GetRelativePath(request.RestrictedOutputRoot!, cover));
    }

    [Theory]
    [InlineData("<ownerMid>/target")]
    [InlineData("<publishDate:..>/target")]
    public void DefaultServer_RejectsExpansionThatEscapesRootEvenWhenRawTemplatePasses(string template)
    {
        var request = new ServeRequestOptions
        {
            Url = "https://www.bilibili.tv/play/2110869", UseIntlApi = true,
            MultiFilePattern = template, CoverOnly = true
        };
        Assert.Null(new BBDownTApiServer().ValidateAndNormalizeServerRequest(request));
        var error = Assert.Throws<ArgumentException>(() => Program.FormatSavePath(template, "LINK CLICK",
            null, null, InternationalPage(), 24, "INTL", 1, request.RestrictedOutputRoot));
        Assert.Contains("服务器输出模板", error.Message);
    }

    [Theory]
    [InlineData("../target.mp4")]
    [InlineData("nested/../../target.mp4")]
    [InlineData("/target.mp4")]
    public void RestrictedOutput_RejectsParentAndAbsoluteDestinations(string path)
    {
        Assert.Throws<ArgumentException>(() => OutputPathPolicy.Resolve(path, Environment.CurrentDirectory));
    }

    [Fact]
    public void RestrictedOutput_RejectsSiblingDirectoryWithMatchingRootPrefix()
    {
        var root = Path.Combine(Environment.CurrentDirectory, "download-root");
        Assert.Throws<ArgumentException>(() => OutputPathPolicy.Resolve("../download-root-other/target.mp4", root));
    }

    [Fact]
    public void RestrictedOutput_NormalizesSafeNestedPathsWithoutTouchingTheFilesystem()
    {
        var root = Path.Combine(Environment.CurrentDirectory, "download-root");
        Assert.Equal(Path.Combine(root, "target.mp4"), OutputPathPolicy.Resolve("nested/../target.mp4", root));
    }

    [Fact]
    public void DomesticTemplates_RetainRealIdsAndExistingRelativePaths()
    {
        var page = new Page(1, "12", "34", "", "part", 1, "", 0);
        Assert.Equal("12/34.mp4", Program.FormatSavePath("<aid>/<cid>", "Title", null, null, page, 1, "WEB", 0));
        Assert.Equal("12/34.mp4", Program.FormatSavePath("<aid>/<cid>", "Title", null, null, page, 1, "INTL", 0));
    }

    [Fact]
    public void UnrestrictedCli_RetainsExplicitAbsoluteOutputSupport()
    {
        var path = Path.Combine(Environment.CurrentDirectory, "absolute-output.mp4");
        Assert.Equal(path.Replace('\\', '/'), Program.FormatSavePath(path, "Title", null, null, InternationalPage(), 24, "INTL", 1));
    }

    [Fact]
    public void BatchClone_PreservesTheServerOwnedBoundary()
    {
        var request = new ServeRequestOptions { Url = "BV17x411w7KC" };
        Assert.Null(new BBDownTApiServer().ValidateAndNormalizeServerRequest(request));
        var child = request.ForBatchVideo("BV17x411w7KC", request.WorkDir);
        Assert.Equal(request.RestrictedOutputRoot, child.RestrictedOutputRoot);
    }

    [Fact]
    public void JsonCannotOverrideOrExposeTheServerOwnedBoundary()
    {
        var request = JsonSerializer.Deserialize("""
            {"Url":"https://www.bilibili.tv/play/2110869","RestrictedOutputRoot":"/synthetic-escape"}
            """, SourceGenerationContext.Default.ServeRequestOptions)!;
        Assert.Null(request.RestrictedOutputRoot);
        Assert.Null(new BBDownTApiServer().ValidateAndNormalizeServerRequest(request));
        var json = JsonSerializer.Serialize(request, SourceGenerationContext.Default.ServeRequestOptions);
        Assert.DoesNotContain("RestrictedOutputRoot", json);
    }
}
