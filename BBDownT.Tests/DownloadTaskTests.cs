namespace BBDownT.Tests;

public class DownloadTaskTests
{
    [Theory]
    [InlineData(1024, 100, 100, 0)]
    [InlineData(1024, 101, 100, 0)]
    [InlineData(2048, 100, 102, 1024)]
    public void CalculateDownloadSpeed_AlwaysReturnsFiniteRate(
        double downloadedBytes,
        long startedAt,
        long finishedAt,
        double expected)
    {
        var speed = DownloadTask.CalculateDownloadSpeed(downloadedBytes, startedAt, finishedAt);

        Assert.Equal(expected, speed);
        Assert.True(double.IsFinite(speed));
    }
}
