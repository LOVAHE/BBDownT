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

    [Fact]
    public void GenerateTransportUserAgent_CoversMultipleClientImplementations()
    {
        var random = new Random(20260914);
        var values = Enumerable.Range(0, 250)
            .Select(_ => HTTPUtil.GenerateTransportUserAgent(random))
            .ToArray();

        Assert.Contains(values, value => value.StartsWith("Dart/", StringComparison.Ordinal));
        Assert.Contains(values, value => value.StartsWith("curl/", StringComparison.Ordinal));
        Assert.Contains(values, value => value.StartsWith("Dalvik/", StringComparison.Ordinal));
    }
}
