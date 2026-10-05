using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace BBDownT.Tests;

public class ResumeStateCodecTests
{
    [Fact]
    public void Record_RoundTripsWithoutPersistingSignedUrl()
    {
        const string url = "https://cdn.test/media?signature=synthetic-secret&deadline=123#ignored";
        var hash = DownloadResumeState.SourceHash(url);
        var state = new DownloadResumeState("selected-track", hash, 0, 3, 4, true, 4,
            Convert.ToHexString(SHA256.HashData("DATA"u8)), new("\"v1\"", null));
        var json = JsonSerializer.Serialize(state, DownloadResumeStateJsonContext.Default.DownloadResumeState);
        Assert.Equal(state, DownloadResumeState.Parse(json));
        Assert.DoesNotContain("synthetic-secret", json);
        Assert.DoesNotContain("https://", json);
        Assert.Equal(hash, DownloadResumeState.SourceHash(url[..url.IndexOf('#')]));
        Assert.NotEqual(hash, DownloadResumeState.SourceHash(url.Replace("deadline=123", "deadline=456")));
        Assert.Equal(hash, DownloadResumeState.Scope(url, null));
    }

    [Theory]
    [InlineData("{not-json")]
    [InlineData("null")]
    [InlineData("{}")]
    public void InvalidRecord_IsNotTrusted(string text) => Assert.Null(DownloadResumeState.Parse(text));

    [Theory]
    [InlineData(2, 4, true)]
    [InlineData(1, 2, true)]
    [InlineData(1, 5, false)]
    public void UnknownVersionOrImpossibleCompletion_IsNotTrusted(int version, long length, bool complete)
    {
        var state = new DownloadResumeState("track", "source", 0, 3, 4, complete, length,
            new string('A', 64), new(null, null)) { Version = version };
        Assert.Null(DownloadResumeState.Parse(JsonSerializer.Serialize(state,
            DownloadResumeStateJsonContext.Default.DownloadResumeState)));
    }

    [Fact]
    public void LegacyTwoLineValidator_RemainsReadableButDoesNotInventResourceIdentity()
    {
        var text = Convert.ToBase64String(Encoding.UTF8.GetBytes("\"legacy\"")) + "\n\n";
        Assert.Equal(new DownloadResumeValidator("\"legacy\"", null), DownloadResumeValidator.Parse(text));
        Assert.Null(DownloadResumeState.Parse(text));
        Assert.Null(DownloadResumeValidator.Parse("broken\ninvalid-date\n"));
    }

    [Fact]
    public void TrackScopeAndRangeMustMatchEvenWhenLengthAndValidatorMatch()
    {
        var state = new DownloadResumeState("video-720-avc", "source", 0, 3, 4, true, 4,
            new string('A', 64), new("\"shared\"", null));
        Assert.True(state.MatchesRange("video-720-avc", 0, 3, 4));
        Assert.False(state.MatchesRange("video-1080-hevc", 0, 3, 4));
        Assert.False(state.MatchesRange("video-720-avc", 1, 3, 4));
        Assert.False(state.MatchesRange("video-720-avc", 0, 3, 5));
    }

    [Fact]
    public void LastModifiedOnly_IsUnknownWhenAResponseGainsAStrongEntityTag()
    {
        var date = DateTimeOffset.Parse("2026-10-05T00:00:00Z");
        var old = new DownloadResumeValidator(null, date);
        var current = new DownloadResumeValidator("\"new-entity\"", date);
        Assert.False(old.Matches(current));
        Assert.False(old.KnownChanged(current));
        using var response = new HttpResponseMessage(System.Net.HttpStatusCode.PartialContent)
        {
            Content = new ByteArrayContent([])
        };
        response.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue("\"new-entity\"");
        response.Content.Headers.LastModified = date;
        Assert.False(old.Matches(response));
        Assert.True(old.KnownChanged(new(null, date.AddSeconds(1))));
        Assert.True(current.KnownChanged(new("\"different\"", date)));
        Assert.False(current.KnownChanged(old));
    }
}
