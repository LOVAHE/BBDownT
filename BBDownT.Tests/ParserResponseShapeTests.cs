using System.Text.Json;
using BBDownT.Core;

namespace BBDownT.Tests;

public class ParserResponseShapeTests
{
    [Theory]
    [InlineData("{\"data\":{\"v_voucher\":\"voucher-test\"}}", true)]
    [InlineData("{\"data\":{\"v_voucher\":\"\"}}", false)]
    [InlineData("{\"data\":{\"dash\":{}}}", false)]
    [InlineData("not-json", false)]
    public void IsRiskControlVoucherResponse_UsesJsonShape(string json, bool expected)
    {
        Assert.Equal(expected, Parser.IsRiskControlVoucherResponse(json));
    }

    [Fact]
    public void ParseJsonRoot_ReturnsDetachedElement()
    {
        var root = Parser.ParseJsonRoot("{\"data\":{\"value\":1}}");

        Assert.Equal(1, root.GetProperty("data").GetProperty("value").GetInt32());
    }

    [Fact]
    public void IsIntlResponse_UsesJsonStructureInsteadOfSourceFormatting()
    {
        using var document = JsonDocument.Parse(
            """
            {
              "data" : {
                "video_info" : {
                  "stream_list" : []
                }
              }
            }
            """);

        Assert.True(Parser.IsIntlResponse(document.RootElement));
    }

    [Theory]
    [InlineData("{\"data\":{\"dash\":{}}}", "dash")]
    [InlineData("{\"result\":{\"durl\":[]}}", "durl")]
    [InlineData("{\"result\":{\"video_info\":{\"dash\":{}}}}", "dash")]
    [InlineData("{\"dash\":{}}", "dash")]
    public void SelectResponseRoot_SupportsKnownEnvelopeShapes(string json, string expectedProperty)
    {
        using var document = JsonDocument.Parse(json);

        var root = Parser.SelectResponseRoot(document.RootElement);

        Assert.True(root.TryGetProperty(expectedProperty, out _));
    }

    private const string DrmBlockedJson =
        """
        {"code":0,"message":"success","result":{"play_check":{"limit_play_reason":"DRM_UNSUPPORTED","play_detail":"PLAY_NONE"},"video_info":{"is_drm":true},"view_info":{"dialog":{"code":0,"count_down_sec":0,"title":{"text":"当前设备加密等级较低，应版权方要求无法播放，请更换其他浏览器尝试"}}}}}
        """;

    [Fact]
    public void TryGetPlayBlockInfo_DetectsDrmRefusalWithDialog()
    {
        Assert.True(Parser.TryGetPlayBlockInfo(DrmBlockedJson, out var reason, out var dialogText));

        Assert.Equal("DRM_UNSUPPORTED", reason);
        Assert.Contains("加密等级", dialogText);
    }

    [Theory]
    [InlineData("{\"result\":{\"video_info\":{\"is_drm\":true}}}", true)]
    [InlineData("{\"result\":{\"video_info\":{\"is_drm\":false},\"dash\":{}}}", false)]
    [InlineData("{\"code\":0,\"message\":\"success\",\"result\":{\"quality\":80,\"dash\":{\"video\":[]}}}", false)]
    [InlineData("{\"code\":-404,\"message\":\"啥都木有\"}", false)]
    [InlineData("not-json", false)]
    public void TryGetPlayBlockInfo_ClassifiesResponses(string json, bool expected)
    {
        Assert.Equal(expected, Parser.TryGetPlayBlockInfo(json, out _, out _));
    }

    [Theory]
    [InlineData("{\"result\":{\"is_preview\":1,\"durl\":[]}}", true)]
    [InlineData("{\"result\":{\"is_preview\":true,\"durl\":[]}}", true)]
    [InlineData("{\"result\":{\"is_preview\":0,\"dash\":{}}}", false)]
    [InlineData("{\"result\":{\"is_preview\":false,\"dash\":{}}}", false)]
    [InlineData("not-json", false)]
    public void IsPreviewOnlyResponse_UsesJsonShape(string json, bool expected)
    {
        Assert.Equal(expected, Parser.IsPreviewOnlyResponse(json));
    }
}
