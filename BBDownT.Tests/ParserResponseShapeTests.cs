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

    [Theory]
    [InlineData("{\"is_preview\":1}", true)]
    [InlineData("{\"result\":{\"is_preview\":true}}", true)]
    [InlineData("{\"result\":{\"video_info\":{\"is_preview\":1}}}", true)]
    [InlineData("{\"data\":{\"video_info\":{\"is_preview\":true}}}", true)]
    [InlineData("{\"result\":{\"is_preview\":0,\"is_drm\":true}}", false)]
    [InlineData("{\"data\":{\"is_preview\":false}}", false)]
    [InlineData("{\"result\":{\"is_preview\":1.25}}", false)]
    [InlineData("{\"result\":{\"is_preview\":9223372036854775807}}", false)]
    [InlineData("{\"result\":\"success\",\"is_preview\":1}", true)]
    [InlineData("[]", false)]
    [InlineData("null", false)]
    public void PreviewMetadata_RecognizesSupportedEnvelopesWithoutConfusingDrm(string json, bool expected)
    {
        using var document = JsonDocument.Parse(json);

        Assert.Equal(expected, Parser.IsPreviewOnlyResponse(document.RootElement));
    }
}
