using BBDownT.Core;

namespace BBDownT.Tests;

public class DanmakuUtilTests
{
    [Theory]
    [InlineData("FF0000", "\\c&H0000FF&")]
    [InlineData("00A0E9", "\\c&HE9A000&")]
    [InlineData("FFFFFF", null)]
    [InlineData("", null)]
    public void Colors_AreWrittenInAssBlueGreenRedOrder(string rgb, string? tag)
    {
        Assert.Equal(tag, DanmakuUtil.AssColorTag(rgb));
    }

    [Fact]
    public void Text_CannotOpenOverrideBlocksOrBreakTheEventLine()
    {
        Assert.Equal("(╯°□°)\\{╯︵┻━┻\\}", DanmakuUtil.EscapeAssText("(╯°□°){╯︵┻━┻}"));
        Assert.Equal("C:\\\u200BNew", DanmakuUtil.EscapeAssText("C:\\New"));
        Assert.Equal("第一行\\N第二行\\N第三行", DanmakuUtil.EscapeAssText("第一行\r\n第二行\n第三行"));
    }

    [Fact]
    public void InvalidXmlCharacters_DoNotDiscardTheDanmaku()
    {
        using var files = new MediaTestDirectory();
        var path = files.Write("danmaku.xml",
            "<?xml version=\"1.0\" encoding=\"UTF-8\"?><i>"
            + "<d p=\"1.5,1,25,16711680,0,0,abc,1\">正常\b弹幕</d>"
            + "<d p=\"2,7,25,16777215,0,0,abc,2\">[0.5,0.5,\"1-1\",4.5,\"text\"]</d>"
            + "<d p=\"3,5,25,16776960,0,0,abc,3\">顶部&#1;弹幕</d></i>");

        var danmakus = DanmakuUtil.ParseXml(path);

        Assert.NotNull(danmakus);
        Assert.Equal(["正常弹幕", "[0.5,0.5,\"1-1\",4.5,\"text\"]", "顶部\u0001弹幕"], danmakus.Select(d => d.Content));
        Assert.Equal([false, true, false], danmakus.Select(d => d.IsAdvanced));
    }

    [Fact]
    public void BrokenXml_IsReportedAsUnparsable()
    {
        using var files = new MediaTestDirectory();
        var path = files.Write("danmaku.xml", "<?xml version=\"1.0\"?><i><d p=\"1,1,25,0,0,0,a,1\">truncated");

        Assert.Null(DanmakuUtil.ParseXml(path));
    }

    [Fact]
    public async Task Ass_SkipsScriptedDanmakuAndEscapesText()
    {
        using var files = new MediaTestDirectory();
        var output = files.FilePath("danmaku.ass");
        DanmakuUtil.DanmakuItem[] danmakus =
        [
            new("1,1,25,16711680,0,0,a,1".Split(','), "{\\an8}注入"),
            new("2,8,25,16777215,0,0,a,2".Split(','), "function(){}"),
            new("3,9,25,16777215,0,0,a,3".Split(','), "def text t1 {}"),
            new("4,5,25,16776960,0,0,a,4".Split(','), "顶部")
        ];

        await DanmakuUtil.SaveAsAssAsync(danmakus, output);

        var events = (await File.ReadAllLinesAsync(output)).Where(line => line.StartsWith("Dialogue:")).ToArray();
        Assert.Equal(2, events.Length);
        Assert.EndsWith("\\c&H0000FF&}\\{\\\u200Ban8\\}注入", events[0]);
        Assert.EndsWith("\\c&H00FFFF&}顶部", events[1]);
    }
}
