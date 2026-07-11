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
}
