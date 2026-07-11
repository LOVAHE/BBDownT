using System.Net;
using System.Net.Http.Headers;

namespace BBDownT.Tests;

public class DownloadResumeValidatorTests
{
    [Fact]
    public async Task SaveAndLoad_RoundTripsEntityTagAndLastModified()
    {
        var path = Path.GetTempFileName();
        var expected = new DownloadResumeValidator(
            "\"entity-v1\"",
            new DateTimeOffset(2026, 7, 10, 0, 0, 0, TimeSpan.Zero));
        try
        {
            await expected.SaveAsync(path);

            var actual = await DownloadResumeValidator.LoadAsync(path);

            Assert.Equal(expected, actual);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Matches_PrefersEntityTag()
    {
        var validator = new DownloadResumeValidator("\"entity-v1\"", null);
        using var matching = CreateResponse("\"entity-v1\"");
        using var changed = CreateResponse("\"entity-v2\"");

        Assert.True(validator.Matches(matching));
        Assert.False(validator.Matches(changed));
    }

    private static HttpResponseMessage CreateResponse(string entityTag)
    {
        return new HttpResponseMessage(HttpStatusCode.PartialContent)
        {
            Headers = { ETag = EntityTagHeaderValue.Parse(entityTag) },
            Content = new ByteArrayContent([])
        };
    }
}
