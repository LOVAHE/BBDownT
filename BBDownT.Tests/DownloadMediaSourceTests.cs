namespace BBDownT.Tests;

public class DownloadMediaSourceTests
{
    private const string Url = "https://upos-sz-mirrorcoso1.bilivideo.com/upgcxcode/01/02/123/123-1-30080.m4s"
        + "?e=object-proof&platform=pc&mid=0&deadline=123&trid=trace-a&upsig=synthetic-secret-a"
        + "&uparams=e%2Cplatform%2Cmid&hdnts=auth-a";

    [Fact]
    public void RotatingAuthenticationAndSignedFieldOrder_KeepTheObjectHash()
    {
        var refreshed = Url.Replace("deadline=123", "deadline=456").Replace("trid=trace-a", "trid=trace-b")
            .Replace("upsig=synthetic-secret-a", "upsig=synthetic-secret-b").Replace("hdnts=auth-a", "hdnts=auth-b")
            .Replace("uparams=e%2Cplatform%2Cmid", "uparams=mid,platform,e");
        var hash = DownloadMediaSource.CreateObjectHash(Url);

        Assert.NotNull(hash);
        Assert.Matches("^[A-F0-9]{64}$", hash);
        Assert.Equal(hash, DownloadMediaSource.CreateObjectHash(refreshed));
        Assert.NotEqual(DownloadResumeState.SourceHash(Url), DownloadResumeState.SourceHash(refreshed));
    }

    [Fact]
    public void QueryOrderAndFragment_DoNotChangeTheObjectHash()
    {
        var split = Url.Split('?');
        var shuffled = split[0] + "?" + string.Join("&", split[1].Split('&').Reverse()) + "#fragment";

        Assert.Equal(DownloadMediaSource.CreateObjectHash(Url), DownloadMediaSource.CreateObjectHash(shuffled));
    }

    [Theory]
    [InlineData("upos-sz-mirrorcoso1.bilivideo.com", "upos-hz-mirrorakam.akamaized.net")]
    [InlineData("https://", "http://")]
    [InlineData(".com/upgcxcode/", ".com:8443/upgcxcode/")]
    [InlineData("123-1-30080.m4s", "123-1-30064.m4s")]
    [InlineData("/01/", "/%30%31/")]
    [InlineData("/upgcxcode/", "/iupxcodeboss/")]
    [InlineData("e=object-proof", "e=other-proof")]
    [InlineData("platform=pc", "platform=android")]
    [InlineData("mid=0", "mid=1")]
    [InlineData("uparams=e%2Cplatform%2Cmid", "uparams=e%2Cplatform")]
    [InlineData("platform=pc", "platform=p%63")]
    public void OriginPathAndSemanticQueryChanges_ChangeTheObjectHash(string before, string after)
    {
        var changed = DownloadMediaSource.CreateObjectHash(Url.Replace(before, after));

        Assert.NotNull(changed);
        Assert.NotEqual(DownloadMediaSource.CreateObjectHash(Url), changed);
    }

    [Theory]
    [InlineData("f")]
    [InlineData("gen")]
    [InlineData("logo")]
    [InlineData("nbs")]
    [InlineData("oi")]
    [InlineData("orderid")]
    [InlineData("os")]
    [InlineData("uipk")]
    [InlineData("bvc")]
    [InlineData("unknown")]
    public void AdditionalQueryValues_ArePreserved(string field)
    {
        Assert.NotEqual(DownloadMediaSource.CreateObjectHash(Url + "&" + field + "=first"),
            DownloadMediaSource.CreateObjectHash(Url + "&" + field + "=second"));
    }

    [Theory]
    [InlineData("upos-sz-mirrorcoso1.bilivideo.com", "cdn.test")]
    [InlineData("upos-sz-mirrorcoso1.bilivideo.com", "upos-sz-mirrorcoso1.bilivideo.com.evil.test")]
    [InlineData("upos-sz-mirrorcoso1.bilivideo.com", "bilivideo.com")]
    [InlineData("upos-sz-mirrorcoso1.bilivideo.com", "unknown.bilivideo.com")]
    [InlineData("https://", "ftp://")]
    [InlineData("https://", "https://user@")]
    [InlineData("/upgcxcode/01/", "/upgcxcode//")]
    [InlineData("/upgcxcode/01/", "/upgcxcode/../")]
    [InlineData("/upgcxcode/01/", "/upgcxcode/%2e%2e/")]
    [InlineData("/upgcxcode/01/", "/upgcxcode/01%2f02/")]
    [InlineData("/upgcxcode/01/", "/upgcxcode/%00/")]
    [InlineData("/upgcxcode/", "/other/")]
    [InlineData(".m4s?", ".html?")]
    [InlineData("platform=pc", "Platform=pc")]
    [InlineData("platform=pc", "plat%66orm=pc")]
    [InlineData("platform=pc", "platform=pc&platform=android")]
    [InlineData("platform=pc", "platform=pc&&extra=1")]
    [InlineData("platform=pc", "platform=p%ZZ")]
    [InlineData("platform=pc", "platform=p%")]
    [InlineData("platform=pc", "platform=p%6")]
    [InlineData("platform=pc", "platform=p c")]
    [InlineData("deadline=123", "deadline=")]
    [InlineData("trid=trace-a", "trid=")]
    [InlineData("upsig=synthetic-secret-a", "upsig=")]
    [InlineData("uparams=e%2Cplatform%2Cmid", "uparams=")]
    [InlineData("uparams=e%2Cplatform%2Cmid", "uparams=e,platform,missing")]
    [InlineData("uparams=e%2Cplatform%2Cmid", "uparams=e,platform,platform")]
    [InlineData("uparams=e%2Cplatform%2Cmid", "uparams=e,Platform,mid")]
    [InlineData("uparams=e%2Cplatform%2Cmid", "uparams=e,,mid")]
    public void UnrecognizedOrAmbiguousSource_FallsBack(string before, string after)
        => Assert.Null(DownloadMediaSource.CreateObjectHash(Url.Replace(before, after)));

    [Theory]
    [InlineData("uparams")]
    [InlineData("upsig")]
    [InlineData("deadline")]
    [InlineData("trid")]
    public void MissingSignatureMetadata_FallsBack(string field)
    {
        var split = Url.Split('?');
        var missing = split[0] + "?" + string.Join("&", split[1].Split('&')
            .Where(part => !part.StartsWith(field + "=", StringComparison.Ordinal)));

        Assert.Null(DownloadMediaSource.CreateObjectHash(missing));
    }

    [Theory]
    [InlineData("upos-sz-mirrorcoso1.bilivideo.cn")]
    [InlineData("cn-hk-eq-bcache-16.bilivideo.com")]
    [InlineData("upos-bstar1-mirrorakam.akamaized.net")]
    [InlineData("upos-hz-mirrorakam.akamaized.net")]
    public void KnownProviderHost_IsRecognized(string host)
        => Assert.NotNull(DownloadMediaSource.CreateObjectHash(Url.Replace("upos-sz-mirrorcoso1.bilivideo.com", host)));

    [Theory]
    [InlineData(".mp4")]
    [InlineData(".flv")]
    public void KnownMediaExtension_IsRecognized(string extension)
        => Assert.NotNull(DownloadMediaSource.CreateObjectHash(Url.Replace(".m4s", extension)));

    [Fact]
    public void CustomHostRequiresExplicitOptIn_AndRetainsAllOtherGuards()
    {
        var custom = Url.Replace("upos-sz-mirrorcoso1.bilivideo.com", "media.example.test");

        Assert.Null(DownloadMediaSource.CreateObjectHash(custom));
        Assert.NotNull(DownloadMediaSource.CreateObjectHash(custom, allowCustomHost: true));
        Assert.NotEqual(DownloadMediaSource.CreateObjectHash(Url), DownloadMediaSource.CreateObjectHash(custom, true));
        Assert.Null(DownloadMediaSource.CreateObjectHash(custom.Replace("/upgcxcode/", "/other/"), true));
        Assert.Null(DownloadMediaSource.CreateObjectHash(custom.Replace("platform=pc", "platform=pc&platform=pc"), true));
    }

    [Fact]
    public void UnsignedHexQnDyeid_IsRecognizedAsRotatingTrace()
    {
        var first = Url + "&qn_dyeid=" + new string('a', 32);
        var second = Url + "&qn_dyeid=" + new string('b', 32);

        Assert.Equal(DownloadMediaSource.CreateObjectHash(first), DownloadMediaSource.CreateObjectHash(second));
        Assert.Equal(DownloadMediaSource.CreateObjectHash(Url), DownloadMediaSource.CreateObjectHash(first));
    }

    [Theory]
    [InlineData("short")]
    [InlineData("gggggggggggggggggggggggggggggggg")]
    public void UnexpectedUnsignedQnDyeid_RemainsPartOfTheObjectHash(string value)
        => Assert.NotEqual(DownloadMediaSource.CreateObjectHash(Url), DownloadMediaSource.CreateObjectHash(Url + "&qn_dyeid=" + value));

    [Fact]
    public void SignedQnDyeid_RemainsPartOfTheObjectHash()
    {
        var signed = Url.Replace("uparams=e%2Cplatform%2Cmid", "uparams=e,platform,mid,qn_dyeid");

        Assert.NotEqual(DownloadMediaSource.CreateObjectHash(signed + "&qn_dyeid=" + new string('a', 32)),
            DownloadMediaSource.CreateObjectHash(signed + "&qn_dyeid=" + new string('b', 32)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void VerifiedStorageRoutesAndUnsignedDeviceIdentifiers_KeepTheTargetObject(bool customHost)
    {
        var original = Url + "&os=cosovbv&buvid=first-device";
        if (customHost) original = original.Replace("upos-sz-mirrorcoso1.bilivideo.com", "media.example.test");
        var next = original.Replace("os=cosovbv", "os=akam").Replace("buvid=first-device", "buvid=second-device");

        var hash = DownloadMediaSource.CreateObjectHash(original, customHost);
        Assert.NotNull(hash);
        Assert.Equal(hash, DownloadMediaSource.CreateObjectHash(next, customHost));
        Assert.NotEqual(hash, DownloadMediaSource.CreateObjectHash(next.Replace("os=akam", "os=unverified-storage"), customHost));
    }

    [Fact]
    public void DeviceIdentifiersDeclaredInSignedScope_RemainScoped()
    {
        var url = Url.Replace("uparams=e%2Cplatform%2Cmid", "uparams=e,platform,mid,buvid") + "&buvid=first-device";
        Assert.NotEqual(DownloadMediaSource.CreateObjectHash(url),
            DownloadMediaSource.CreateObjectHash(url.Replace("buvid=first-device", "buvid=second-device")));
    }
}
