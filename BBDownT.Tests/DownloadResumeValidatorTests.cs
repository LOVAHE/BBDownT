using System.Net;
using System.Net.Http.Headers;

namespace BBDownT.Tests;

public class DownloadResumeValidatorTests
{
    [Fact]
    public void Matches_PrefersEntityTag()
    {
        var validator = new DownloadResumeValidator("\"entity-v1\"", null);
        using var matching = CreateResponse("\"entity-v1\"");
        using var changed = CreateResponse("\"entity-v2\"");

        Assert.True(validator.Matches(DownloadResumeValidator.FromResponse(matching)));
        Assert.False(validator.Matches(DownloadResumeValidator.FromResponse(changed)));
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
