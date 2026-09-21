using System.Net.Http;
using BBDownT.Core;
using BBDownT.Core.Util;

namespace BBDownT.Tests;

public class BrowserRequestProfileTests
{
    [Fact]
    public void Create_ProducesConsistentChromiumHeaders()
    {
        var profile = BrowserRequestProfile.CreateChromium(new Random(20260921));
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.bilibili.com/x/web-interface/nav");

        profile.Apply(request);

        Assert.True(profile.IsValid());
        var major = profile.UserAgent.Split("Chrome/", 2)[1].Split('.', 2)[0];
        Assert.Contains($"\"Chromium\";v=\"{major}\"", profile.SecChUa!);
        Assert.Contains($"\"Google Chrome\";v=\"{major}\"", profile.SecChUa!);
        Assert.Equal(profile.UserAgent, string.Join(' ', request.Headers.GetValues("User-Agent")));
        Assert.Equal(profile.SecChUa, Assert.Single(request.Headers.GetValues("sec-ch-ua")));
        Assert.Equal("?0", Assert.Single(request.Headers.GetValues("sec-ch-ua-mobile")));
        Assert.Equal($"\"{profile.Platform}\"", Assert.Single(request.Headers.GetValues("sec-ch-ua-platform")));
        Assert.Contains(profile.Locale, profile.AcceptLanguage);
        Assert.False(string.IsNullOrWhiteSpace(profile.TimeZoneId));
    }

    [Theory]
    [InlineData(BrowserRequestProfile.Firefox)]
    [InlineData(BrowserRequestProfile.Safari)]
    public void NonChromiumProfiles_UseCompleteNativeHeadersWithoutClientHints(string family)
    {
        var random = new Random(20260921);
        var profile = family == BrowserRequestProfile.Firefox
            ? BrowserRequestProfile.CreateFirefox(random)
            : BrowserRequestProfile.CreateSafari(random);
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.bilibili.com/x/web-interface/nav");

        profile.Apply(request);

        Assert.True(profile.IsValid());
        Assert.Equal(family, profile.BrowserFamily);
        Assert.False(request.Headers.Contains("sec-ch-ua"));
        Assert.False(request.Headers.Contains("sec-ch-ua-mobile"));
        Assert.False(request.Headers.Contains("sec-ch-ua-platform"));
        Assert.True(request.Headers.Contains("Accept-Language"));
        Assert.True(request.Headers.Contains("Accept-Encoding"));
    }

    [Fact]
    public void Create_RandomizesAcrossCoherentBrowserLocaleAndPlatformProfiles()
    {
        var random = new Random(20260921);
        var profiles = Enumerable.Range(0, 500)
            .Select(_ => BrowserRequestProfile.Create(random))
            .ToArray();

        Assert.All(profiles, profile => Assert.True(profile.IsValid()));
        Assert.Contains(profiles, profile => profile.BrowserFamily == BrowserRequestProfile.Chromium);
        Assert.Contains(profiles, profile => profile.BrowserFamily == BrowserRequestProfile.Firefox);
        Assert.Contains(profiles, profile => profile.BrowserFamily == BrowserRequestProfile.Safari);
        Assert.True(profiles.Select(profile => profile.Locale).Distinct().Count() >= 5);
        Assert.True(profiles.Select(profile => profile.TimeZoneId).Distinct().Count() >= 5);
        Assert.True(profiles.Where(profile => profile.BrowserFamily != BrowserRequestProfile.Safari)
            .Select(profile => profile.Platform).Distinct().Count() >= 3);
        Assert.All(profiles.Where(profile => profile.BrowserFamily == BrowserRequestProfile.Chromium), profile =>
        {
            var major = int.Parse(profile.UserAgent.Split("Chrome/", 2)[1].Split('.', 2)[0]);
            Assert.InRange(major, 150, 152);
        });
        Assert.All(profiles.Where(profile => profile.BrowserFamily == BrowserRequestProfile.Firefox), profile =>
        {
            var major = int.Parse(profile.UserAgent.Split("Firefox/", 2)[1].Split('.', 2)[0]);
            Assert.InRange(major, 154, 156);
        });
        Assert.All(profiles.Where(profile => profile.BrowserFamily == BrowserRequestProfile.Safari), profile =>
        {
            var minor = int.Parse(profile.UserAgent.Split("Version/26.", 2)[1].Split(' ', 2)[0]);
            Assert.InRange(minor, 4, 6);
        });
    }

    [Fact]
    public void ApplyWebRequestHeaders_UsesBrowserProfileOnlyForLoggedInRequests()
    {
        var originalCookie = Config.COOKIE;
        var originalProfile = HTTPUtil.AuthenticatedBrowserProfile;
        var profile = BrowserRequestProfile.CreateChromium(new Random(7));
        try
        {
            HTTPUtil.ConfigureAuthenticatedBrowserProfile(profile);
            Config.COOKIE = "SESSDATA=test;bili_jct=test";
            using var authenticated = new HttpRequestMessage(HttpMethod.Get, "https://api.bilibili.com/x/web-interface/nav");
            HTTPUtil.ApplyWebRequestHeaders(authenticated, authenticated.RequestUri!.ToString());

            Assert.Equal(profile.UserAgent, string.Join(' ', authenticated.Headers.GetValues("User-Agent")));
            Assert.True(authenticated.Headers.Contains("sec-ch-ua"));
            Assert.True(authenticated.Headers.Contains("Cookie"));
            Assert.Equal("same-site", Assert.Single(authenticated.Headers.GetValues("Sec-Fetch-Site")));

            Config.COOKIE = string.Empty;
            using var anonymous = new HttpRequestMessage(HttpMethod.Get, "https://api.bilibili.com/x/web-interface/nav");
            HTTPUtil.ApplyWebRequestHeaders(anonymous, anonymous.RequestUri!.ToString());

            Assert.False(anonymous.Headers.Contains("sec-ch-ua"));
            Assert.False(anonymous.Headers.Contains("Cookie"));
        }
        finally
        {
            Config.COOKIE = originalCookie;
            HTTPUtil.ConfigureAuthenticatedBrowserProfile(originalProfile);
        }
    }

    [Fact]
    public void RiskControlRetry_KeepsLoggedInBrowserProfileStable()
    {
        var originalCookie = Config.COOKIE;
        var originalProfile = HTTPUtil.AuthenticatedBrowserProfile;
        try
        {
            var profile = BrowserRequestProfile.Create(new Random(9));
            HTTPUtil.ConfigureAuthenticatedBrowserProfile(profile);
            Config.COOKIE = "SESSDATA=test;bili_jct=test";

            Assert.False(HTTPUtil.PrepareRiskControlRetry());
            Assert.Equal(profile, HTTPUtil.AuthenticatedBrowserProfile);
        }
        finally
        {
            Config.COOKIE = originalCookie;
            HTTPUtil.ConfigureAuthenticatedBrowserProfile(originalProfile);
        }
    }

    [Fact]
    public void Store_PersistsAndReloadsTheSameProfile()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"bbd-web-profile-{Guid.NewGuid():N}");
        var path = Path.Combine(directory, AuthenticatedWebProfileStore.FileName);
        try
        {
            var first = AuthenticatedWebProfileStore.LoadOrCreate(directory);
            var second = AuthenticatedWebProfileStore.LoadOrCreate(directory);

            Assert.True(File.Exists(path));
            Assert.Equal(first, second);
        }
        finally
        {
            File.Delete(path);
            Directory.Delete(directory);
        }
    }
}
