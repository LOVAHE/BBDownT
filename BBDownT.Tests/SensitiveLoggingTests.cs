using BBDownT.Core;
using BBDownT.Core.Util;

namespace BBDownT.Tests;

public class SensitiveLoggingTests
{
    [Fact]
    public void CommandLineLogSanitizer_RedactsSeparatedAndInlineSecrets()
    {
        var log = CommandLineLogSanitizer.Format(
        [
            "video", "--cookie", "SESSDATA=cookie-secret", "-token", "token-secret",
            "--api-token=api-secret", "--access-token:colon-secret", "--work-dir", "/downloads", "https://b23.tv/x"
        ]);

        Assert.DoesNotContain("cookie-secret", log);
        Assert.DoesNotContain("token-secret", log);
        Assert.DoesNotContain("api-secret", log);
        Assert.DoesNotContain("colon-secret", log);
        Assert.Contains("https://b23.tv/x", log);
        Assert.Contains("--work-dir /downloads", log);
    }

    [Theory]
    [InlineData("{\"Cookie\":\"cookie-secret\",\"AccessToken\":\"token-secret\"}")]
    [InlineData("Authorization: Bearer authorization-secret")]
    [InlineData("access_key=query-secret&qn=127")]
    [InlineData("{\"refresh_token\":\"json-secret\",\"access_token\":\"json-secret\"}")]
    [InlineData("{\"Aria2cArgs\":\"--all-proxy=http://user:proxy-secret@proxy.test:8080\"}")]
    public void LoggerRedaction_RemovesKnownSecretShapes(string input)
    {
        var log = Logger.RedactSensitiveText(input);

        Assert.DoesNotContain("secret", log);
        Assert.Contains("<redacted>", log);
    }

    [Fact]
    public void SanitizeText_RedactsSecretsEmbeddedInDiagnostics()
    {
        var diagnostic = "option --cookie SESSDATA=secret-value --api-token:secret-value is invalid";

        var sanitized = CommandLineLogSanitizer.SanitizeText(diagnostic);

        Assert.Equal("option --cookie <redacted> --api-token:<redacted> is invalid", sanitized);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FailureBanner_PrintsTheHeaderDetailAndUpgradeHint(bool debug)
    {
        var error = new NullReferenceException("{\"Cookie\":\"cookie-secret\"}");
        var originalOutput = Console.Out;
        var originalDebug = Config.DEBUG_LOG;
        using var output = new StringWriter();
        try
        {
            Console.SetOut(output);
            Config.DEBUG_LOG = debug;

            var description = Program.PrintFailure(error, "BV1下载失败");

            var detail = debug ? Logger.RedactSensitiveText(error.ToString()) : ErrorText.Describe(error);
            var newLine = Environment.NewLine;
            Assert.Equal(ErrorText.Describe(error), description);
            Assert.Equal($"BV1下载失败{newLine}{detail}{newLine}请尝试升级到最新版本后重试!{newLine}", output.ToString());
            Assert.DoesNotContain("cookie-secret", output.ToString());
        }
        finally
        {
            Console.SetOut(originalOutput);
            Config.DEBUG_LOG = originalDebug;
        }
    }

    [Fact]
    public void EveryCredentialOptionAlias_IsRedactedByBothSanitizers()
    {
        var aliases = CommandLineInvoker.GetRootCommand(_ => Task.CompletedTask).Options
            .Where(option => option.Name is "cookie" or "access-token")
            .SelectMany(option => option.Aliases)
            .Append("--api-token")
            .ToArray();

        Assert.Equal(5, aliases.Length);
        foreach (var alias in aliases)
        {
            Assert.Equal($"{alias} <redacted>", CommandLineLogSanitizer.Format([alias, "secret-value"]));
            Assert.Equal($"{alias}=<redacted>", CommandLineLogSanitizer.Format([$"{alias}=secret-value"]));
            Assert.Equal($"{alias} <redacted>", CommandLineLogSanitizer.SanitizeText($"{alias} secret-value"));
            Assert.Equal($"{alias}:<redacted>", CommandLineLogSanitizer.SanitizeText($"{alias}:\"secret value\""));
        }
    }
}
