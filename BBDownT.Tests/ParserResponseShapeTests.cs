using System.Text.Json;
using BBDownT.Core;

namespace BBDownT.Tests;

public class ParserResponseShapeTests
{
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
}
