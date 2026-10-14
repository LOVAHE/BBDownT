namespace BBDownT.Tests;

public class ExclusiveModeTests
{
    [Fact]
    public void NeedsMuxer_ReturnsTrueForNormalDownload()
    {
        Assert.True(Program.NeedsMuxer(new MyOption()));
    }

    [Theory]
    [InlineData("skip-mux")]
    [InlineData("info")]
    [InlineData("cover")]
    [InlineData("subtitle")]
    [InlineData("danmaku")]
    public void NeedsMuxer_ReturnsFalseForNonMuxingModes(string mode)
    {
        var option = new MyOption
        {
            SkipMux = mode == "skip-mux",
            OnlyShowInfo = mode == "info",
            CoverOnly = mode == "cover",
            SubOnly = mode == "subtitle",
            DanmakuOnly = mode == "danmaku"
        };

        Assert.False(Program.NeedsMuxer(option));
    }

    [Theory]
    [InlineData("", true)]
    [InlineData("info", false)]
    [InlineData("cover", false)]
    [InlineData("danmaku", false)]
    public void InteractiveStreamChoice_IsSkippedWhenNoStreamIsDownloaded(string mode, bool prompts)
    {
        var option = new MyOption
        {
            Interactive = true,
            OnlyShowInfo = mode == "info",
            CoverOnly = mode == "cover",
            DanmakuOnly = mode == "danmaku"
        };

        Assert.Equal(prompts, Program.ShouldPromptTrackSelection(option));
    }

    [Theory]
    [InlineData("xml,ass", false)]
    [InlineData("xml", true)]
    public async Task MalformedDanmaku_SucceedsOnlyWhenRawXmlWasAllThatWasRequested(string formats, bool saved)
    {
        using var files = new MediaTestDirectory();
        var xml = files.Write("Title.xml", "<i><d p=");
        var ass = files.FilePath("Title.ass");
        var recorded = new List<string>();

        Assert.Equal(saved, await Program.SaveDanmakuAsync(xml, ass, Formats(formats), recorded.Add));

        Assert.True(File.Exists(xml));
        Assert.False(File.Exists(ass));
        Assert.Equal(new[] { xml }, recorded);
    }

    [Theory]
    [InlineData("xml,ass", true, true)]
    [InlineData("ass", false, true)]
    [InlineData("xml", true, false)]
    public async Task SavedDanmakuFiles_AreReportedAsOutputs(string formats, bool keepsXml, bool writesAss)
    {
        using var files = new MediaTestDirectory();
        var xml = files.Write("Title.xml",
            "<?xml version=\"1.0\" encoding=\"UTF-8\"?><i><d p=\"1.5,1,25,16711680,0,0,abc,1\">弹幕</d></i>");
        var ass = files.FilePath("Title.ass");
        var recorded = new List<string>();

        Assert.True(await Program.SaveDanmakuAsync(xml, ass, Formats(formats), recorded.Add));

        Assert.Equal(keepsXml, File.Exists(xml));
        Assert.Equal(writesAss, File.Exists(ass));
        Assert.Equal(new[] { writesAss ? ass : null, keepsXml ? xml : null }.OfType<string>(), recorded);
    }

    [Theory]
    [InlineData("sub,danmaku", "--sub-only、--danmaku-only、--cover-only 只能指定其中一个")]
    [InlineData("sub,cover", "--sub-only、--danmaku-only、--cover-only 只能指定其中一个")]
    [InlineData("danmaku,cover", "--sub-only、--danmaku-only、--cover-only 只能指定其中一个")]
    [InlineData("sub,danmaku,cover", "--sub-only、--danmaku-only、--cover-only 只能指定其中一个")]
    [InlineData("sub,skip-subtitle", "--sub-only 与 --skip-subtitle 不能同时使用")]
    public async Task ConflictingOnlyModes_AreRejectedByTheCliAndTheApi(string flags, string expected)
    {
        ServeRequestOptions Create()
        {
            var modes = flags.Split(',');
            return new ServeRequestOptions
            {
                Url = "BV1xx411c7mD", SubOnly = modes.Contains("sub"), DanmakuOnly = modes.Contains("danmaku"),
                CoverOnly = modes.Contains("cover"), SkipSubtitle = modes.Contains("skip-subtitle")
            };
        }

        Assert.Equal(expected, new BBDownTApiServer().ValidateAndNormalizeServerRequest(Create()));
        var option = Create();
        var error = await Assert.ThrowsAsync<ArgumentException>(() => Program.ExecuteWorkAsync(option,
            prepare: _ => throw new InvalidOperationException("Invalid options must not be prepared")));
        Assert.Equal(expected, error.Message);
        Assert.Equal(Create().SubOnly, option.SubOnly);
    }

    [Theory]
    [InlineData("sub")]
    [InlineData("danmaku")]
    [InlineData("cover")]
    public void SingleOnlyMode_StillCombinesWithDanmakuDownload(string mode)
    {
        Assert.Null(Program.ValidateExclusiveOptions(new MyOption
        {
            SubOnly = mode == "sub", DanmakuOnly = mode == "danmaku", CoverOnly = mode == "cover", DownloadDanmaku = true
        }));
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData("xml,ass", true)]
    [InlineData(" ASS ， xml ", true)]
    [InlineData("srt", false)]
    [InlineData("ass;xml", false)]
    [InlineData(",", false)]
    [InlineData(" ", false)]
    public async Task DanmakuFormats_AreValidatedByTheCliAndTheApi(string? formats, bool valid)
    {
        var apiError = new BBDownTApiServer().ValidateAndNormalizeServerRequest(
            new ServeRequestOptions { Url = "BV1xx411c7mD", DownloadDanmakuFormats = formats });
        Assert.Equal(valid, apiError is null);
        if (valid) return;

        var error = await Assert.ThrowsAsync<ArgumentException>(() => Program.ExecuteWorkAsync(
            new MyOption { Url = "BV1xx411c7mD", DownloadDanmakuFormats = formats },
            prepare: _ => throw new InvalidOperationException("Invalid options must not be prepared")));
        Assert.Equal(apiError, error.Message);
        Assert.Contains("xml/ass", error.Message);
    }

    private static BBDownTDanmakuFormat[] Formats(string names)
        => names.Split(',').Select(BBDownTDanmakuFormatInfo.FromFormatName).ToArray();
}
