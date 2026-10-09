namespace BBDownT.Tests;

public class Aria2cDownloadTests
{
    [Fact]
    public void EnsureAria2cDownloadSucceeded_AcceptsSuccessfulCompletedFile()
    {
        var path = Path.GetTempFileName();
        try
        {
            BBDownTDownloadUtil.EnsureAria2cDownloadSucceeded(0, path);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void EnsureAria2cDownloadSucceeded_RejectsNonZeroExitEvenWhenOldFileExists()
    {
        var path = Path.GetTempFileName();
        try
        {
            var error = Assert.Throws<InvalidOperationException>(
                () => BBDownTDownloadUtil.EnsureAria2cDownloadSucceeded(7, path));

            Assert.Contains("7", error.Message);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void EnsureAria2cDownloadSucceeded_RejectsMissingOutput()
    {
        var path = Path.Combine(Path.GetTempPath(), $"bbdownt-{Guid.NewGuid():N}");

        Assert.Throws<InvalidOperationException>(
            () => BBDownTDownloadUtil.EnsureAria2cDownloadSucceeded(0, path));
    }

    [Theory]
    [InlineData("", new string[] { })]
    [InlineData("-x16 -s16", new[] { "-x16", "-s16" })]
    [InlineData("--all-proxy=\"http://127.0.0.1:7890\"", new[] { "--all-proxy=http://127.0.0.1:7890" })]
    [InlineData("\"a b\"\tc", new[] { "a b", "c" })]
    [InlineData("a\\\"b", new[] { "a\"b" })]
    [InlineData("a\\\\\"b c\"", new[] { "a\\b c" })]
    [InlineData("C:\\dir\\ \"\"", new[] { "C:\\dir\\", "" })]
    [InlineData("\"x\"\"y\"", new[] { "x\"y" })]
    public void ExtraArguments_KeepTheirPreviousSplitting(string arguments, string[] expected)
    {
        Assert.Equal(expected, BBDownTAria2c.SplitArguments(arguments));
    }

    [Fact]
    public void UrlAndOutputNames_StaySingleArgumentsEvenWithQuotes()
    {
        const string url = "https://upos.example.test/v.m4s?a=\" --on-download-complete=x \"";
        const string path = "/in-memory/out \" -d /etc \"/name \" --log=/tmp/x \".mp4";

        var args = BBDownTAria2c.BuildDownloadArguments(url, path, "", international: false);

        Assert.Equal(new[] { url, "-d", Path.GetDirectoryName(path)!, "-o", Path.GetFileName(path) }, args.TakeLast(5));
        Assert.DoesNotContain(args, argument => argument.StartsWith("--on-download-complete") || argument.StartsWith("--log"));
    }

    [Fact]
    public void MissingExtraArguments_AddNothing()
    {
        Assert.Equal(BBDownTAria2c.BuildDownloadArguments("https://upos.example.test/v.m4s", "out.mp4", "", international: false),
            BBDownTAria2c.BuildDownloadArguments("https://upos.example.test/v.m4s", "out.mp4", null!, international: false));
    }
}
