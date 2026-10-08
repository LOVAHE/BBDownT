using System.Diagnostics;
using System.Text.Json;

namespace BBDownT.Tests;

/// <summary>
/// 网页前端与服务端共用的链接规则对照用例(TestData/link-rules.json)：
/// C# 侧用它检查 SpaceBatchDownload.IsSpaceUrl；装有 node 时运行 WebUi/check-link-rules.mjs，
/// 用同一份数据检查 index.html 里的前端函数。改动任一侧的规则时，两边都会被检查到。
/// </summary>
public class WebUiLinkRulesTests
{
    internal static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "BBDownT.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("找不到仓库根目录(BBDownT.sln)");
    }

    private static JsonElement Fixture()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(RepoRoot(), "BBDownT.Tests", "TestData", "link-rules.json")));
        return document.RootElement.Clone();
    }

    public static IEnumerable<object[]> SpaceCases() =>
        Fixture().GetProperty("space").EnumerateArray()
            .Select(c => new object[] { c.GetProperty("url").GetString()!, c.GetProperty("expected").GetBoolean() });

    [Theory]
    [MemberData(nameof(SpaceCases))]
    public void IsSpaceUrl_MatchesTheSharedFixture(string url, bool expected)
    {
        Assert.Equal(expected, SpaceBatchDownload.IsSpaceUrl(url));
    }

    public static IEnumerable<object[]> CleanCases() =>
        Fixture().GetProperty("clean").EnumerateArray()
            .Select(c => new object[] { c.GetProperty("input").GetString()!, c.GetProperty("expected").GetString()! });

    /// <summary>
    /// 下载历史里记录的链接与网页前端 cleanLink 去掉的分享跟踪参数相同
    /// </summary>
    [Theory]
    [MemberData(nameof(CleanCases))]
    public void HistoryCleanUrl_MatchesTheSharedFixture(string input, string expected)
    {
        Assert.Equal(expected, DownloadHistory.CleanUrl(input));
    }

    [Fact]
    public void IndexHtml_KeepsTheLinkRulesMarkers()
    {
        var html = File.ReadAllText(Path.Combine(RepoRoot(), "BBDownT", "WebUi", "index.html"));
        var begin = html.IndexOf("// link-rules:begin", StringComparison.Ordinal);
        var end = html.IndexOf("// link-rules:end", StringComparison.Ordinal);

        Assert.True(begin >= 0 && end > begin, "index.html 需要保留 link-rules:begin/end 标记，node 检查脚本靠它抽出链接规则");
        var rules = html[begin..end];
        foreach (var name in new[] { "function splitInput(", "function linkKey(", "function isSpaceUrl(", "function linkKind(", "function pagesExpr(" })
            Assert.Contains(name, rules);
    }

    [Fact]
    public void IndexHtml_KeepsTheStreamPrefMarkers()
    {
        var html = File.ReadAllText(Path.Combine(RepoRoot(), "BBDownT", "WebUi", "index.html"));
        var begin = html.IndexOf("// stream-pref:begin", StringComparison.Ordinal);
        var end = html.IndexOf("// stream-pref:end", StringComparison.Ordinal);

        Assert.True(begin >= 0 && end > begin, "index.html 需要保留 stream-pref:begin/end 标记，node 检查脚本靠它抽出流匹配规则");
        var rules = html[begin..end];
        foreach (var name in new[] { "function codecOrder(", "function matchVideoPref(", "function matchAudioPref(", "function guestParse(", "function pickByPref(" })
            Assert.Contains(name, rules);
    }

    [Fact]
    public void IndexHtml_KeepsTheBatchRulesMarkers()
    {
        var html = File.ReadAllText(Path.Combine(RepoRoot(), "BBDownT", "WebUi", "index.html"));
        var begin = html.IndexOf("// batch-rules:begin", StringComparison.Ordinal);
        var end = html.IndexOf("// batch-rules:end", StringComparison.Ordinal);

        Assert.True(begin >= 0 && end > begin, "index.html 需要保留 batch-rules:begin/end 标记，node 检查脚本靠它抽出多个链接逐个解析的规则");
        var rules = html[begin..end];
        foreach (var name in new[] { "const BATCH_MAX = 20;", "function batchPlan(", "function batchProgress(", "const nextBatchRow = ", "function rowStreams(", "function pinStreams(", "const batchRowRequest = " })
            Assert.Contains(name, rules);
    }

    /// <summary>
    /// check-link-rules.mjs：链接规则(对照 TestData/link-rules.json)；
    /// check-stream-pref.mjs：按上次的选择自动选中流的匹配规则(相同、同画质换编码、更低最接近的一档、不选)；
    /// check-batch-rules.mjs：多个链接逐个解析(去重、20 个上限、解析顺序、每行的流和下载请求、自动选中)
    /// </summary>
    [Theory]
    [InlineData("check-link-rules.mjs")]
    [InlineData("check-stream-pref.mjs")]
    [InlineData("check-batch-rules.mjs")]
    public async Task FrontendRules_PassTheNodeChecks_WhenNodeIsAvailable(string scriptName)
    {
        var node = FindOnPath(OperatingSystem.IsWindows() ? "node.exe" : "node");
        if (node is null) return; // 没有 node 时不检查(GitHub 的 ubuntu/macOS 镜像自带 node)

        var script = Path.Combine(RepoRoot(), "BBDownT.Tests", "WebUi", scriptName);
        using var process = Process.Start(new ProcessStartInfo(node, $"\"{script}\"")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        })!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));

        Assert.True(process.ExitCode == 0, $"{await stderr}{await stdout}");
    }

    private static string? FindOnPath(string fileName) =>
        (Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(dir => Path.Combine(dir, fileName))
            .FirstOrDefault(File.Exists);
}
