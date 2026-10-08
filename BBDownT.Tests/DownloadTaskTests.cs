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

    [Fact]
    public void SetStream_ReplacesTheRecordOfARetriedPageInsteadOfAppending()
    {
        var task = new DownloadTask("1", "BV1xx", 1);

        task.SetStream("1/100/1", "P1 · WEB · 4K 超清 AVC · 192K M4A");
        task.SetStream("1/101/2", "P2 · WEB · 4K 超清 AVC · 192K M4A");
        // 分P 1 下载失败后重试，又选了一次流
        task.SetStream("1/100/1", "P1 · WEB · 1080P 高清 AVC · 192K M4A");
        // 空间批量任务里另一个视频的 P1
        task.SetStream("2/200/1", "P1 · WEB · 720P 高清 HEVC · 132K M4A");

        Assert.Equal(
            ["P1 · WEB · 1080P 高清 AVC · 192K M4A", "P2 · WEB · 4K 超清 AVC · 192K M4A", "P1 · WEB · 720P 高清 HEVC · 132K M4A"],
            task.CreateSnapshot().Streams);
    }

    [Fact]
    public void AddSavePath_RecordsAbsolutePathsResolvedAgainstTheTaskWorkingDirectory()
    {
        var task = new DownloadTask("1", "BV1xx", 1);

        task.AddSavePath("video.mp4");
        task.AddSavePath("/data/downloads/list.txt");

        Assert.Equal([Path.GetFullPath("video.mp4"), Path.GetFullPath("/data/downloads/list.txt")], task.CreateSnapshot().SavePaths);
    }
}
