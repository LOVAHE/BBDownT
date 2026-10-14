using System.CommandLine;

namespace BBDownT.Tests;

public class CommandLineInvokerTests
{
    public static TheoryData<string, string?, string, object> BoundOptions => new()
    {
        { "--use-tv-api", null, nameof(MyOption.UseTvApi), true },
        { "--use-app-api", null, nameof(MyOption.UseAppApi), true },
        { "--use-intl-api", null, nameof(MyOption.UseIntlApi), true },
        { "--use-mp4box", null, nameof(MyOption.UseMP4box), true },
        { "--encoding-priority", "hevc,avc", nameof(MyOption.EncodingPriority), "hevc,avc" },
        { "--dfn-priority", "1080P 高码率", nameof(MyOption.DfnPriority), "1080P 高码率" },
        { "--only-show-info", null, nameof(MyOption.OnlyShowInfo), true },
        { "--show-all", null, nameof(MyOption.ShowAll), true },
        { "--use-aria2c", null, nameof(MyOption.UseAria2c), true },
        { "--interactive", null, nameof(MyOption.Interactive), true },
        { "--hide-streams", null, nameof(MyOption.HideStreams), true },
        { "--multi-thread", "false", nameof(MyOption.MultiThread), false },
        { "--simply-mux", null, nameof(MyOption.SimplyMux), true },
        { "--video-only", null, nameof(MyOption.VideoOnly), true },
        { "--audio-only", null, nameof(MyOption.AudioOnly), true },
        { "--danmaku-only", null, nameof(MyOption.DanmakuOnly), true },
        { "--sub-only", null, nameof(MyOption.SubOnly), true },
        { "--cover-only", null, nameof(MyOption.CoverOnly), true },
        { "--skip-mux", null, nameof(MyOption.SkipMux), true },
        { "--skip-subtitle", null, nameof(MyOption.SkipSubtitle), true },
        { "--skip-cover", null, nameof(MyOption.SkipCover), true },
        { "--force-http", "false", nameof(MyOption.ForceHttp), false },
        { "--download-danmaku", null, nameof(MyOption.DownloadDanmaku), true },
        { "--download-danmaku-formats", "ass", nameof(MyOption.DownloadDanmakuFormats), "ass" },
        { "--skip-ai", "false", nameof(MyOption.SkipAi), false },
        { "--subtitle-language", "en", nameof(MyOption.SubtitleLanguage), "en" },
        { "--ai-subtitle-policy", "include", nameof(MyOption.AiSubtitlePolicy), "include" },
        { "--video-ascending", null, nameof(MyOption.VideoAscending), true },
        { "--audio-ascending", null, nameof(MyOption.AudioAscending), true },
        { "--allow-pcdn", null, nameof(MyOption.AllowPcdn), true },
        { "--file-pattern", "<bvid>", nameof(MyOption.FilePattern), "<bvid>" },
        { "--multi-file-pattern", "<bvid>/<pageNumber>", nameof(MyOption.MultiFilePattern), "<bvid>/<pageNumber>" },
        { "--select-page", "2", nameof(MyOption.SelectPage), "2" },
        { "--language", "jpn", nameof(MyOption.Language), "jpn" },
        { "--audio-language", "en", nameof(MyOption.AudioLanguage), "en" },
        { "--user-agent", "test-agent", nameof(MyOption.UserAgent), "test-agent" },
        { "--cookie", "SESSDATA=x", nameof(MyOption.Cookie), "SESSDATA=x" },
        { "--access-token", "token", nameof(MyOption.AccessToken), "token" },
        { "--aria2c-args", "-x4", nameof(MyOption.Aria2cArgs), "-x4" },
        { "--work-dir", "work", nameof(MyOption.WorkDir), "work" },
        { "--ffmpeg-path", "ffmpeg-bin", nameof(MyOption.FFmpegPath), "ffmpeg-bin" },
        { "--mp4box-path", "mp4box-bin", nameof(MyOption.Mp4boxPath), "mp4box-bin" },
        { "--aria2c-path", "aria2c-bin", nameof(MyOption.Aria2cPath), "aria2c-bin" },
        { "--upos-host", "upos.example.test", nameof(MyOption.UposHost), "upos.example.test" },
        { "--force-replace-host", "false", nameof(MyOption.ForceReplaceHost), false },
        { "--save-archives-to-file", null, nameof(MyOption.SaveArchivesToFile), true },
        { "--delay-per-page", "7", nameof(MyOption.DelayPerPage), "7" },
        { "--download-all", null, nameof(MyOption.DownloadAll), true },
        { "--delay-per-video", "3", nameof(MyOption.DelayPerVideo), 3 },
        { "--host", "proxy.example.test", nameof(MyOption.Host), "proxy.example.test" },
        { "--ep-host", "ep.example.test", nameof(MyOption.EpHost), "ep.example.test" },
        { "--tv-host", "tv.example.test", nameof(MyOption.TvHost), "tv.example.test" },
        { "--area", "hk", nameof(MyOption.Area), "hk" },
        { "--aria2c-proxy", "http://127.0.0.1:1", nameof(MyOption.Aria2cProxy), "http://127.0.0.1:1" },
        { "--only-hevc", null, nameof(MyOption.OnlyHevc), true },
        { "--only-avc", null, nameof(MyOption.OnlyAvc), true },
        { "--only-av1", null, nameof(MyOption.OnlyAv1), true },
        { "--add-dfn-subfix", null, nameof(MyOption.AddDfnSubfix), true },
        { "--no-padding-page-num", null, nameof(MyOption.NoPaddingPageNum), true },
        { "--bandwith-ascending", null, nameof(MyOption.BandwithAscending), true },
        { "--debug", null, nameof(MyOption.Debug), true },
    };

    [Theory]
    [MemberData(nameof(BoundOptions))]
    public async Task EveryOption_IsBoundToMyOption(string alias, string? value, string property, object expected)
    {
        MyOption? bound = null;
        var rootCommand = CommandLineInvoker.GetRootCommand(option =>
        {
            bound = option;
            return Task.CompletedTask;
        });
        string[] args = value is null ? ["BV1xx411c7mD", alias] : ["BV1xx411c7mD", alias, value];

        Assert.Equal(0, await rootCommand.InvokeAsync(args));
        Assert.NotNull(bound);
        var info = typeof(MyOption).GetProperty(property)!;
        Assert.NotEqual(expected, info.GetValue(new MyOption()));
        Assert.Equal(expected, info.GetValue(bound));
    }

    [Fact]
    public void EveryRegisteredOption_HasABindingCase()
    {
        var registered = CommandLineInvoker.GetRootCommand(_ => Task.CompletedTask).Options
            .Select(option => "--" + option.Name)
            .Where(alias => alias != "--config-file")
            .Order();
        var covered = BoundOptions.Select(row => (string)row[0]).Order();

        Assert.Equal(registered, covered);
    }
}
