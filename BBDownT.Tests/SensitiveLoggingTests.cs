using BBDownT.Core;

namespace BBDownT.Tests;

public class SensitiveLoggingTests
{
    [Fact]
    public void CommandLineLogSanitizer_RedactsSeparatedAndInlineSecrets()
    {
        var log = CommandLineLogSanitizer.Format(
        [
            "video", "--cookie", "SESSDATA=cookie-secret", "-token", "token-secret",
            "--api-token=api-secret", "--work-dir", "/downloads"
        ]);

        Assert.DoesNotContain("cookie-secret", log);
        Assert.DoesNotContain("token-secret", log);
        Assert.DoesNotContain("api-secret", log);
        Assert.Contains("--work-dir /downloads", log);
    }

    [Theory]
    [InlineData("{\"Cookie\":\"cookie-secret\",\"AccessToken\":\"token-secret\"}")]
    [InlineData("Authorization: Bearer authorization-secret")]
    [InlineData("access_key=query-secret&qn=127")]
    public void LoggerRedaction_RemovesKnownSecretShapes(string input)
    {
        var log = Logger.RedactSensitiveText(input);

        Assert.DoesNotContain("secret", log);
        Assert.Contains("<redacted>", log);
    }

    [Fact]
    public void SanitizeText_RedactsSecretsEmbeddedInDiagnostics()
    {
        var diagnostic = "option --cookie SESSDATA=secret-value is invalid";

        var sanitized = CommandLineLogSanitizer.SanitizeText(diagnostic);

        Assert.DoesNotContain("secret-value", sanitized);
        Assert.Contains("<redacted>", sanitized);
    }
}
