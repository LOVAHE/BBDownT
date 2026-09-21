using System.Net.Http;

namespace BBDownT.Core.Util;

internal sealed record BrowserRequestProfile(
    int SchemaVersion,
    string BrowserFamily,
    string Platform,
    string Locale,
    string TimeZoneId,
    string UserAgent,
    string? SecChUa,
    string? SecChUaMobile,
    string? SecChUaPlatform,
    string AcceptLanguage,
    string AcceptEncoding)
{
    internal const int CurrentSchemaVersion = 4;
    internal const string Chromium = "chromium";
    internal const string Firefox = "firefox";
    internal const string Safari = "safari";

    private static readonly LocaleProfile[] Locales =
    [
        new("zh-CN", "Asia/Shanghai", "zh-CN,zh;q=0.9,en;q=0.8"),
        new("zh-TW", "Asia/Taipei", "zh-TW,zh;q=0.9,en;q=0.8"),
        new("zh-HK", "Asia/Hong_Kong", "zh-HK,zh;q=0.9,en;q=0.8"),
        new("ja-JP", "Asia/Tokyo", "ja-JP,ja;q=0.9,en-US;q=0.7,en;q=0.6"),
        new("en-US", "America/Los_Angeles", "en-US,en;q=0.9")
    ];

    internal static BrowserRequestProfile Create(Random random)
    {
        return random.Next(3) switch
        {
            0 => CreateChromium(random),
            1 => CreateFirefox(random),
            _ => CreateSafari(random)
        };
    }

    internal static BrowserRequestProfile CreateChromium(Random random)
    {
        int major = random.Next(150, 153);
        var locale = PickLocale(random);
        var platform = random.Next(3) switch
        {
            0 => new PlatformProfile("Windows", "Windows NT 10.0; Win64; x64"),
            1 => new PlatformProfile("macOS", "Macintosh; Intel Mac OS X 10_15_7"),
            _ => new PlatformProfile("Linux", "X11; Linux x86_64")
        };
        return new BrowserRequestProfile(
            CurrentSchemaVersion,
            Chromium,
            platform.Name,
            locale.Locale,
            locale.TimeZoneId,
            $"Mozilla/5.0 ({platform.UserAgentPlatform}) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/{major}.0.0.0 Safari/537.36",
            $"\"Not_A Brand\";v=\"99\", \"Chromium\";v=\"{major}\", \"Google Chrome\";v=\"{major}\"",
            "?0",
            $"\"{platform.Name}\"",
            locale.AcceptLanguage,
            "gzip, deflate, br");
    }

    internal static BrowserRequestProfile CreateFirefox(Random random)
    {
        int major = random.Next(154, 157);
        var locale = PickLocale(random);
        var platform = random.Next(3) switch
        {
            0 => new PlatformProfile("Windows", "Windows NT 10.0; Win64; x64"),
            1 => new PlatformProfile("macOS", "Macintosh; Intel Mac OS X 10.15"),
            _ => new PlatformProfile("Linux", "X11; Linux x86_64")
        };
        return new BrowserRequestProfile(
            CurrentSchemaVersion,
            Firefox,
            platform.Name,
            locale.Locale,
            locale.TimeZoneId,
            $"Mozilla/5.0 ({platform.UserAgentPlatform}; rv:{major}.0) Gecko/20100101 Firefox/{major}.0",
            null,
            null,
            null,
            locale.AcceptLanguage,
            "gzip, deflate, br");
    }

    internal static BrowserRequestProfile CreateSafari(Random random)
    {
        int minor = random.Next(4, 7);
        var locale = PickLocale(random);
        return new BrowserRequestProfile(
            CurrentSchemaVersion,
            Safari,
            "macOS",
            locale.Locale,
            locale.TimeZoneId,
            $"Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/26.{minor} Safari/605.1.15",
            null,
            null,
            null,
            locale.AcceptLanguage,
            "gzip, deflate, br");
    }

    internal bool IsValid()
    {
        if (SchemaVersion != CurrentSchemaVersion) return false;
        if (BrowserFamily is not (Chromium or Firefox or Safari)) return false;
        if (!Locales.Any(candidate => candidate.Locale == Locale
            && candidate.TimeZoneId == TimeZoneId
            && candidate.AcceptLanguage == AcceptLanguage))
            return false;
        if (Safari == BrowserFamily && Platform != "macOS") return false;

        var requiredValues = new[] { Platform, UserAgent, AcceptLanguage, AcceptEncoding };
        if (requiredValues.Any(IsUnsafeHeaderValue)) return false;

        bool hasAllClientHints = !string.IsNullOrWhiteSpace(SecChUa)
            && !string.IsNullOrWhiteSpace(SecChUaMobile)
            && !string.IsNullOrWhiteSpace(SecChUaPlatform);
        bool hasAnyClientHint = SecChUa is not null || SecChUaMobile is not null || SecChUaPlatform is not null;
        if (BrowserFamily == Chromium ? !hasAllClientHints : hasAnyClientHint) return false;
        if (hasAllClientHints && new[] { SecChUa!, SecChUaMobile!, SecChUaPlatform! }.Any(IsUnsafeHeaderValue))
            return false;
        if (BrowserFamily == Chromium && SecChUaPlatform != $"\"{Platform}\"") return false;

        using var request = new HttpRequestMessage();
        Apply(request);
        return request.Headers.Contains("User-Agent")
            && request.Headers.Contains("Accept-Language")
            && request.Headers.Contains("Accept-Encoding");
    }

    internal void Apply(HttpRequestMessage request)
    {
        request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
        if (BrowserFamily == Chromium)
        {
            request.Headers.TryAddWithoutValidation("sec-ch-ua", SecChUa);
            request.Headers.TryAddWithoutValidation("sec-ch-ua-mobile", SecChUaMobile);
            request.Headers.TryAddWithoutValidation("sec-ch-ua-platform", SecChUaPlatform);
        }
        request.Headers.TryAddWithoutValidation("Accept-Language", AcceptLanguage);
        request.Headers.TryAddWithoutValidation("Accept-Encoding", AcceptEncoding);
    }

    private static LocaleProfile PickLocale(Random random) => Locales[random.Next(Locales.Length)];

    private static bool IsUnsafeHeaderValue(string? value)
    {
        return string.IsNullOrWhiteSpace(value)
            || value.Contains('\r', StringComparison.Ordinal)
            || value.Contains('\n', StringComparison.Ordinal);
    }

    private readonly record struct LocaleProfile(string Locale, string TimeZoneId, string AcceptLanguage);
    private readonly record struct PlatformProfile(string Name, string UserAgentPlatform);
}
