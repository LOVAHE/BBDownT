using System.CommandLine;
using static BBDownT.BBDownTDownloadUtil;

namespace BBDownT.Tests;

public class CoverSelectionTests
{
    [Theory]
    [InlineData("not-found")]
    [InlineData("timeout")]
    [InlineData("interrupted")]
    public async Task FailedEmbeddedCover_DoesNotFailThePage(string kind)
    {
        Exception failure = kind switch
        {
            "not-found" => new HttpRequestException("Not Found", null, System.Net.HttpStatusCode.NotFound),
            "timeout" => new TaskCanceledException("HTTP timeout", new TimeoutException()),
            _ => new IOException("connection reset")
        };

        await Program.DownloadCoverForMuxAsync(new MyOption(), "https://example.test/cover.jpg", "/in-memory/cover.jpg",
            (_, _, _) => throw failure);
    }

    [Theory]
    [InlineData("path")]
    [InlineData("cancelled")]
    public async Task EmbeddedCover_KeepsPathAndCancellationErrors(string kind)
    {
        Exception failure = kind == "path"
            ? new ArgumentException("服务器输出路径不能经过符号链接或目录链接")
            : new OperationCanceledException();

        Assert.Same(failure, await Assert.ThrowsAnyAsync<Exception>(() => Program.DownloadCoverForMuxAsync(new MyOption(),
            "https://example.test/cover.jpg", "/in-memory/cover.jpg", (_, _, _) => throw failure)));
    }

    [Fact]
    public async Task EmbeddedCover_UsesThePlainArtifactDownload()
    {
        var option = new MyOption { UseAria2c = true, ForceHttp = true, MultiThread = true, RestrictedOutputRoot = "/in-memory" };
        DownloadConfig? used = null;

        await Program.DownloadCoverForMuxAsync(option, "https://example.test/cover.jpg", "/in-memory/cover.jpg",
            (url, path, config) =>
            {
                Assert.Equal("https://example.test/cover.jpg", url);
                Assert.Equal("/in-memory/cover.jpg", path);
                used = config;
                return Task.CompletedTask;
            });

        Assert.Equal(new DownloadConfig { RestrictedOutputRoot = "/in-memory" }, used);
    }

    [Fact]
    public void ArtifactDownloads_IgnoreMediaTransportOptions()
    {
        var task = new DownloadTask("1", "BV1xx411c7mD", 1);
        var config = Program.ArtifactDownloadConfig(new MyOption
        {
            UseAria2c = true, Aria2cArgs = "-x16", ForceHttp = true, MultiThread = true, RestrictedOutputRoot = "/in-memory"
        }, task);

        Assert.Equal(new DownloadConfig { RestrictedOutputRoot = "/in-memory", RelatedTask = task }, config);
    }

    [Theory]
    [InlineData("https://i0.hdslb.com/bfs/archive/cover.jpg", ".jpg")]
    [InlineData("https://pic.bstarstatic.com/ogv/cover.png?w=1280", ".png")]
    [InlineData("cover.webp", ".webp")]
    public void CoverExtension_ComesFromTheUrlPath(string url, string expected)
    {
        Assert.Equal(expected, Program.GetCoverExtension(url));
    }

    [Fact]
    public void SkipCover_DoesNotProbeOrEmbedAnExistingCachedCover()
    {
        Assert.Equal("", Program.GetCoverForMux(new MyOption { SkipCover = true }, "/in-memory/old-cover.jpg",
            _ => throw new Exception("Skipped covers must not be inspected")));
    }

    [Theory]
    [InlineData(true, "/in-memory/cover.jpg")]
    [InlineData(false, "")]
    public void EnabledCover_EmbedsOnlyAnExistingCover(bool exists, string expected)
    {
        Assert.Equal(expected, Program.GetCoverForMux(new MyOption(), "/in-memory/cover.jpg", _ => exists));
    }

    [Fact]
    public async Task ConflictingCoverFlags_FailBeforeNormalizingOtherOptions()
    {
        var option = new MyOption { Url = "BV1xx411c7mD", SkipCover = true, CoverOnly = true, AudioOnly = true, VideoOnly = true };
        var error = await Assert.ThrowsAsync<ArgumentException>(() => Program.ExecuteWorkAsync(option,
            prepare: _ => throw new InvalidOperationException("Invalid options must not be prepared")));

        Assert.Contains("--cover-only", error.Message);
        Assert.Contains("--skip-cover", error.Message);
        Assert.False(option.SkipMux);
    }

    [Fact]
    public void Api_RejectsTheSameConflictingCoverFlags()
    {
        var error = new BBDownTApiServer().ValidateAndNormalizeServerRequest(new ServeRequestOptions
        {
            Url = "https://www.bilibili.tv/play/2110869", UseIntlApi = true,
            CoverOnly = true, SkipCover = true
        });

        Assert.Equal(Program.ValidateExclusiveOptions(new MyOption { CoverOnly = true, SkipCover = true }), error);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CliAndBatchClone_PreserveSkipCover(bool international)
    {
        MyOption? bound = null;
        var command = CommandLineInvoker.GetRootCommand(option => { bound = option; return Task.CompletedTask; });
        var args = international
            ? new[] { "-intl", "--skip-cover", "https://www.bilibili.tv/play/2110869" }
            : new[] { "--skip-cover", "BV1xx411c7mD" };

        Assert.Equal(0, await command.InvokeAsync(args));
        Assert.NotNull(bound);
        Assert.True(bound.SkipCover);
        var batch = bound.ForBatchVideo(bound.Url, "/in-memory");
        Assert.True(batch.SkipCover);
        Assert.Equal(international, batch.UseIntlApi);
    }
}
