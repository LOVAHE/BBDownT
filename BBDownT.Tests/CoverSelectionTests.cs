using System.CommandLine;
using static BBDownT.BBDownTDownloadUtil;

namespace BBDownT.Tests;

public class CoverSelectionTests
{
    [Fact]
    public async Task SkipCover_PreventsTheDownloadCallback()
    {
        var calls = 0;
        var result = await Program.DownloadCoverAsync(new MyOption
        {
            SkipCover = true
        }, "https://example.test/cover.jpg", "/in-memory/cover.jpg", new DownloadConfig(),
            (_, _, _) => { calls++; return Task.CompletedTask; });

        Assert.False(result);
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task InfoOnly_PreventsCoverDownload()
    {
        Assert.False(await Program.DownloadCoverAsync(new MyOption { OnlyShowInfo = true, CoverOnly = true },
            "https://example.test/cover.jpg", "/in-memory/cover.jpg", new DownloadConfig(),
            (_, _, _) => throw new Exception("Info must not download")));
    }

    [Fact]
    public async Task EnabledCover_ForwardsUrlPathAndExistingDownloadSettings()
    {
        var configuration = new DownloadConfig { UseAria2c = true, ForceHttp = false, MultiThread = true };
        var calls = 0;
        Assert.True(await Program.DownloadCoverAsync(new MyOption { CoverOnly = true },
            "https://example.test/cover.jpg", "/in-memory/cover.jpg", configuration,
            (url, path, config) =>
            {
                Assert.Equal("https://example.test/cover.jpg", url);
                Assert.Equal("/in-memory/cover.jpg", path);
                Assert.Same(configuration, config);
                calls++;
                return Task.CompletedTask;
            }));
        Assert.Equal(1, calls);
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
    public void ConflictingCoverFlags_FailBeforeNormalizingOtherOptions()
    {
        var option = new MyOption { SkipCover = true, CoverOnly = true, AudioOnly = true, VideoOnly = true };
        var error = Assert.Throws<ArgumentException>(() => Program.HandleConflictingOptions(option));

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

        Assert.Equal(Program.ValidateCoverOptions(new MyOption { CoverOnly = true, SkipCover = true }), error);
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
