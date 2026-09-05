using System.CommandLine;
using System.Text.Json;

namespace BBDownT.Tests;

public class SpaceBatchDownloadTests
{
    private const string SpaceUrl = "https://space.bilibili.com/42/upload/video";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Options_AreBoundByCliAndJson(bool downloadAll)
    {
        MyOption? bound = null;
        var command = CommandLineInvoker.GetRootCommand(option => { bound = option; return Task.CompletedTask; });
        var args = new List<string> { SpaceUrl, "--delay-per-video", "3", "--delay-per-page", "7" };
        if (downloadAll) args.Add("--download-all");

        Assert.Equal(0, await command.InvokeAsync(args.ToArray()));
        Assert.NotNull(bound);
        Assert.Equal(downloadAll, bound.DownloadAll);
        Assert.Equal(3, bound.DelayPerVideo);
        Assert.Equal("7", bound.DelayPerPage);
        var json = JsonSerializer.Serialize(bound, SourceGenerationContext.Default.MyOption);
        var request = JsonSerializer.Deserialize(json, SourceGenerationContext.Default.ServeRequestOptions)!;
        Assert.Equal(downloadAll, request.DownloadAll);
        Assert.Equal(3, request.DelayPerVideo);
        Assert.Null(new BBDownTApiServer().ValidateAndNormalizeServerRequest(request));
    }

    [Fact]
    public void Defaults_ExportOnlyWithTenSecondIntervalAndNoMuxerRequirement()
    {
        var option = new MyOption { Url = SpaceUrl };
        Assert.False(option.DownloadAll);
        Assert.Equal(10, option.DelayPerVideo);
        Assert.False(Program.NeedsMuxer(option));
        option.DownloadAll = true;
        Assert.True(Program.NeedsMuxer(option));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(int.MaxValue)]
    public void InvalidIntervals_AreRejectedAtTheApiBoundary(int seconds)
    {
        var request = new ServeRequestOptions { Url = SpaceUrl, DownloadAll = true, DelayPerVideo = seconds };
        Assert.Contains("DelayPerVideo", new BBDownTApiServer().ValidateAndNormalizeServerRequest(request));
    }

    [Theory]
    [InlineData("https://space.bilibili.com/42", true)]
    [InlineData(SpaceUrl, true)]
    [InlineData("https://space.bilibili.com/42/video?tid=0", true)]
    [InlineData("https://space.bilibili.com/42/lists/1?type=season", false)]
    [InlineData("https://space.bilibili.com/42/favlist", false)]
    [InlineData("https://space.bilibili.com/42/channel/seriesdetail?sid=1", false)]
    [InlineData("https://www.bilibili.com/video/av1", false)]
    public void DownloadAll_OnlyAcceptsSpaceSubmissions(string url, bool valid)
    {
        Assert.Equal(valid, SpaceBatchDownload.ValidateOptions(new MyOption { Url = url, DownloadAll = true }) is null);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public async Task ExportOnlyAndInfoOnly_DoNotDownloadOrDelay(bool downloadAll, bool infoOnly)
    {
        var path = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(path, Url(1));
            var task = new DownloadTask("mid:42", SpaceUrl, 1);
            await SpaceBatchDownload.HandleExportAsync(path,
                new MyOption { Url = SpaceUrl, DownloadAll = downloadAll, OnlyShowInfo = infoOnly },
                _ => throw new InvalidOperationException("Must not download"), task,
                _ => throw new InvalidOperationException("Must not delay"));
            Assert.Equal(path, Assert.Single(task.CreateSnapshot().SavePaths));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task MissingFile_DoesNotStartDownload()
    {
        var path = Path.GetTempFileName();
        File.Delete(path);
        await Assert.ThrowsAsync<FileNotFoundException>(() => SpaceBatchDownload.HandleExportAsync(
            path, Options(), _ => throw new InvalidOperationException("Must not download")));
    }

    [Fact]
    public async Task EntireManifest_IsValidatedBeforeTheFirstDownload()
    {
        var path = Path.GetTempFileName();
        try
        {
            await File.WriteAllLinesAsync(path, [Url(1), "invalid"]);
            await Assert.ThrowsAsync<InvalidDataException>(() => SpaceBatchDownload.HandleExportAsync(
                path, Options(), _ => throw new InvalidOperationException("Must not download")));
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public async Task Entries_AreSerialWithWaitsOnlyBetweenAttempts(int seconds)
    {
        var path = Path.GetTempFileName();
        var events = new List<string>();
        var active = 0;
        try
        {
            await File.WriteAllLinesAsync(path, [Url(1), "", Url(1), Url(2), Url(3)]);
            await SpaceBatchDownload.HandleExportAsync(path, Options(seconds), async option =>
            {
                Assert.Equal(1, ++active);
                events.Add(option.Url);
                await Task.Yield();
                active--;
            }, delay: milliseconds => { events.Add($"wait:{milliseconds}"); return Task.CompletedTask; });
            var expected = seconds == 0 ? new[] { Url(1), Url(2), Url(3) }
                : new[] { Url(1), "wait:2000", Url(2), "wait:2000", Url(3) };
            Assert.Equal(expected, events);
            Assert.Equal(0, active);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task FailedEntry_ContinuesAndThenFailsTheOverallBatch()
    {
        var path = Path.GetTempFileName();
        var calls = new List<string>();
        var waits = 0;
        try
        {
            await File.WriteAllLinesAsync(path, [Url(1), Url(2)]);
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => SpaceBatchDownload.HandleExportAsync(path,
                Options(1), option =>
                {
                    calls.Add(option.Url);
                    if (option.Url == Url(1)) throw new IOException("fixture failure");
                    return Task.CompletedTask;
                }, delay: _ => { waits++; return Task.CompletedTask; }));
            Assert.Equal(new[] { Url(1), Url(2) }, calls);
            Assert.Equal(1, waits);
            Assert.Contains("1/2", error.Message);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task PerEntryMutations_DoNotChangeNextEntryOrParentOptions()
    {
        var path = Path.GetTempFileName();
        var parent = Options(0);
        parent.SelectPage = "2";
        parent.SelectPageSpecified = true;
        parent.EncodingPriorityFirst = true;
        parent.WorkDir = "relative-original-directory";
        parent.UseTvApi = true;
        parent.Aria2cArgs = "normalized-proxy";
        parent.Aria2cProxy = "legacy-proxy";
        parent.AddDfnSubfix = true;
        var outputDirectory = Path.GetFullPath(Environment.CurrentDirectory);
        try
        {
            await File.WriteAllLinesAsync(path, [Url(1), Url(2)]);
            await SpaceBatchDownload.HandleExportAsync(path, parent, child =>
            {
                Assert.False(child.DownloadAll);
                Assert.True(child.UseTvApi);
                Assert.True(child.SelectPageSpecified);
                Assert.True(child.EncodingPriorityFirst);
                Assert.Equal("2", child.SelectPage);
                Assert.Equal(outputDirectory, child.WorkDir);
                Assert.Equal("normalized-proxy", child.Aria2cArgs);
                Assert.Equal("", child.Aria2cProxy);
                Assert.False(child.AddDfnSubfix);
                child.UseTvApi = false;
                child.SelectPage = "ALL";
                return Task.CompletedTask;
            });
            Assert.True(parent.DownloadAll);
            Assert.True(parent.UseTvApi);
            Assert.Equal("2", parent.SelectPage);
            Assert.Equal("relative-original-directory", parent.WorkDir);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task Cancellation_DoesNotContinueWithAnotherVideo()
    {
        var path = Path.GetTempFileName();
        var calls = 0;
        try
        {
            await File.WriteAllLinesAsync(path, [Url(1), Url(2)]);
            await Assert.ThrowsAsync<OperationCanceledException>(() => SpaceBatchDownload.HandleExportAsync(path, Options(0),
                _ => { calls++; throw new OperationCanceledException(); }));
            Assert.Equal(1, calls);
        }
        finally { File.Delete(path); }
    }

    private static MyOption Options(int seconds = 10) => new() { Url = SpaceUrl, DownloadAll = true, DelayPerVideo = seconds };
    private static string Url(int aid) => $"https://www.bilibili.com/video/av{aid}";
}
