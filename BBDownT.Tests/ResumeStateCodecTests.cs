using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace BBDownT.Tests;

public class ResumeStateCodecTests
{
    [Theory]
    [InlineData("\"same\"", "\"same\"", true)]
    [InlineData("\"first\"", "\"second\"", false)]
    [InlineData("W/\"same\"", "W/\"same\"", false)]
    [InlineData("*", "*", false)]
    [InlineData(null, null, false)]
    public void VersionEquivalence_IgnoresOptionalDateOnlyWithSameStrongTag(string? first, string? second, bool expected)
    {
        var withDate = new DownloadResumeValidator(first, DateTimeOffset.Parse("2026-10-06T00:00:00Z"));
        var withoutDate = new DownloadResumeValidator(second, null);
        Assert.Equal(expected, withDate.SameVersionAs(withoutDate));
        Assert.Equal(expected, withoutDate.SameVersionAs(withDate));
        Assert.True(withDate.SameVersionAs(withDate));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BareCdnTag_IsNormalizedOnlyForRecognizedMedia(bool recognized)
    {
        using var response = new HttpResponseMessage(System.Net.HttpStatusCode.PartialContent)
        { Content = new ByteArrayContent([]) };
        response.Headers.TryAddWithoutValidation("ETag", "b61d09ee67e4ba9f273afea2bbe248b1");
        var validator = DownloadResumeValidator.FromResponse(response, recognized);

        Assert.Equal(recognized, validator.HasStrongEntityTag);
        if (!recognized) return;
        Assert.Equal("\"b61d09ee67e4ba9f273afea2bbe248b1\"", validator.EntityTag);
        using var request = new HttpRequestMessage();
        validator.Apply(request);
        Assert.Equal(validator.EntityTag, request.Headers.IfRange?.ToString());
    }

    [Theory]
    [InlineData("W/\"weak\"")]
    [InlineData("*")]
    [InlineData("invalid")]
    [InlineData("zzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzz")]
    public void AmbiguousOrWeakCdnTag_DoesNotBecomeStrong(string tag)
    {
        using var response = new HttpResponseMessage(System.Net.HttpStatusCode.PartialContent)
        { Content = new ByteArrayContent([]) };
        response.Headers.TryAddWithoutValidation("ETag", tag);
        Assert.False(DownloadResumeValidator.FromResponse(response, true).HasStrongEntityTag);
    }

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

    [Fact]
    public void ObjectHash_RoundTripsWithoutPersistingSignedUrl()
    {
        const string url = "https://upos-sz-mirrorcoso1.bilivideo.com/upgcxcode/01/123.m4s"
            + "?e=object&deadline=123&trid=synthetic-trace&upsig=synthetic-secret&uparams=e";
        var state = new DownloadResumeState("selected-track", DownloadResumeState.SourceHash(url), 0, 3, 4, true, 4,
            new string('A', 64), new("\"opaque\"", null))
        {
            SourceObjectHash = DownloadMediaSource.CreateObjectHash(url)
        };
        var json = JsonSerializer.Serialize(state, DownloadResumeStateJsonContext.Default.DownloadResumeState);

        Assert.NotNull(state.SourceObjectHash);
        Assert.Equal(state, DownloadResumeState.Parse(json));
        Assert.Contains("SourceObjectHash", json);
        Assert.DoesNotContain("synthetic-secret", json);
        Assert.DoesNotContain("synthetic-trace", json);
        Assert.DoesNotContain("https://", json);
        Assert.Equal(1, state.Version);
    }

    [Fact]
    public void LegacyRecord_OnlyMatchesItsExactSourceUri()
    {
        const string url = "https://cdn.test/media?signature=original";
        var state = new DownloadResumeState("selected-track", DownloadResumeState.SourceHash(url), 0, 3, 4, true, 4,
            new string('A', 64), new("\"shared-opaque\"", null));
        var json = JsonSerializer.Serialize(state, DownloadResumeStateJsonContext.Default.DownloadResumeState);
        var parsed = Assert.IsType<DownloadResumeState>(DownloadResumeState.Parse(json));

        Assert.DoesNotContain("SourceObjectHash", json);
        Assert.Null(parsed.SourceObjectHash);
        Assert.True(parsed.MatchesSource(url, null));
        Assert.False(parsed.MatchesSource(url.Replace("original", "refreshed"), new string('B', 64)));
    }

    [Theory]
    [InlineData("\"opaque\"", true)]
    [InlineData("W/\"opaque\"", false)]
    [InlineData("*", false)]
    [InlineData("invalid-unquoted-tag", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void CrossUriMatching_RequiresTheObjectHashAndAStrongEntityTag(string? entityTag, bool matches)
    {
        const string oldUrl = "https://cdn.test/media?signature=original";
        const string newUrl = "https://cdn.test/media?signature=refreshed";
        var objectHash = new string('B', 64);
        var state = new DownloadResumeState("selected-track", DownloadResumeState.SourceHash(oldUrl), 0, 3, 4, true, 4,
            new string('A', 64), new(entityTag, DateTimeOffset.Parse("2026-10-05T00:00:00Z")))
        {
            SourceObjectHash = objectHash
        };

        Assert.True(state.MatchesSource(oldUrl, null));
        Assert.Equal(matches, state.MatchesSource(newUrl, objectHash));
        Assert.False(state.MatchesSource(newUrl, null));
        Assert.False(state.MatchesSource(newUrl, ""));
        Assert.False(state.MatchesSource(newUrl, new string('C', 64)));
    }

    [Fact]
    public void GenericUris_DoNotBecomeEquivalentBecauseTheyShareAnOpaqueEntityTag()
    {
        const string oldUrl = "https://cdn.test/media?signature=original";
        const string newUrl = "https://cdn.test/media?signature=refreshed";
        var state = new DownloadResumeState("selected-track", DownloadResumeState.SourceHash(oldUrl), 0, 3, 4, true, 4,
            new string('A', 64), new("\"shared-opaque\"", null));

        Assert.Null(DownloadMediaSource.CreateObjectHash(oldUrl));
        Assert.Null(DownloadMediaSource.CreateObjectHash(newUrl));
        Assert.False(state.MatchesSource(newUrl, DownloadMediaSource.CreateObjectHash(newUrl)));
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
