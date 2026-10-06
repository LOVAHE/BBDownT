namespace BBDownT.Tests;

public class ProgressBarTests
{
    [Fact]
    public void RestoredCompletionDoesNotCountAsTransferAndFinalBytesAreRecordedOnDispose()
    {
        var task = new DownloadTask("episode", "https://example.test/episode", 0);
        using (var progress = new ProgressBar(task))
        {
            progress.ReportDownload(0.6, 0, "校验 0.00%");
            Assert.Equal(0.6, task.CreateSnapshot().Progress);
            Assert.Equal(0, task.CreateSnapshot().TotalDownloadedBytes);
            progress.ReportDownload(0.7, 10);
        }
        Assert.Equal(0.7, task.CreateSnapshot().Progress);
        Assert.Equal(10, task.CreateSnapshot().TotalDownloadedBytes);
    }

    [Theory]
    [InlineData(double.NaN, 0)]
    [InlineData(double.PositiveInfinity, 0)]
    [InlineData(double.NegativeInfinity, 0)]
    [InlineData(-0.1, 0)]
    [InlineData(0.5, 0.5)]
    [InlineData(1.1, 1)]
    public void NormalizeProgress_AlwaysReturnsFiniteUnitInterval(double input, double expected)
    {
        var progress = ProgressBar.NormalizeProgress(input);

        Assert.Equal(expected, progress);
        Assert.True(double.IsFinite(progress));
    }
}
