using BBDownT.Core.Util;

namespace BBDownT.Tests;

public class HTTPUtilTests
{
    [Fact]
    public void GenerateDefaultUserAgent_ProducesVariedNonProductIdentities()
    {
        var random = new Random(20260914);
        var values = Enumerable.Range(0, 2000)
            .Select(_ => HTTPUtil.GenerateDefaultUserAgent(random))
            .ToHashSet(StringComparer.Ordinal);

        Assert.True(values.Count >= 50);
        Assert.All(values, value =>
        {
            Assert.NotEmpty(value);
            Assert.DoesNotContain("BBDownT", value, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Mozilla", value, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("okhttp", value, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Go-http-client", value, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain('\r', value);
            Assert.DoesNotContain('\n', value);
            using var request = new HttpRequestMessage();
            request.Headers.UserAgent.ParseAdd(value);
        });
        Assert.Contains(values, value => value.StartsWith("Dart/", StringComparison.Ordinal));
        Assert.Contains(values, value => value.StartsWith("curl/", StringComparison.Ordinal));
        Assert.Contains(values, value => value.StartsWith("Dalvik/", StringComparison.Ordinal));
        Assert.Contains(values, value => value.Contains("Android", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("libavutil      57. 28.100 / 57. 28.100", true)]
    [InlineData("libavutil      57. 17.100 / 57. 17.100", true)]
    [InlineData("libavutil      57. 16.100 / 57. 16.100", false)]
    [InlineData("libavutil      56. 70.100 / 56. 70.100", false)]
    [InlineData("libavutil      60.  8.100 / 60.  8.100", true)]
    [InlineData("ffmpeg version unknown", false)]
    public void DolbyVisionSupport_ComparesTheLibavutilMinorVersion(string versionInfo, bool expected)
    {
        Assert.Equal(expected, BBDownT.BBDownTUtil.SupportsDolbyVision(versionInfo));
    }

    [Fact]
    public void CustomUserAgent_AppliesOnlyUntilTheNextTaskClearsIt()
    {
        var automatic = HTTPUtil.UserAgent;
        try
        {
            HTTPUtil.UserAgent = "Custom/1.0";
            Assert.False(HTTPUtil.IsAutomaticUserAgent);
            Assert.Equal("Custom/1.0", HTTPUtil.UserAgent);

            HTTPUtil.UserAgent = "";

            Assert.True(HTTPUtil.IsAutomaticUserAgent);
            Assert.Equal(automatic, HTTPUtil.UserAgent);
        }
        finally
        {
            HTTPUtil.UserAgent = "";
        }
    }
}
