namespace BBDownT.Tests;

public class DownloadTaskConcurrencyTests
{
    [Fact]
    public async Task CreateSnapshot_IsStableDuringConcurrentUpdates()
    {
        var task = new DownloadTask("1", "BV1xx411c7mD", 100);

        var writers = Enumerable.Range(0, 4).Select(worker => Task.Run(() =>
        {
            for (var index = 0; index < 250; index++)
            {
                task.ReportProgress(index / 250d);
                task.ReportDownloadedBytes(1);
                task.AddSavePath($"{worker}-{index}");
            }
        }));
        var reader = Task.Run(() =>
        {
            for (var index = 0; index < 1000; index++)
            {
                var snapshot = task.CreateSnapshot();
                Assert.True(snapshot.SavePaths.Count <= 1000);
            }
        });

        await Task.WhenAll(writers.Append(reader));

        var finalSnapshot = task.CreateSnapshot();
        Assert.Equal(1000, finalSnapshot.SavePaths.Count);
        Assert.Equal(1000, finalSnapshot.TotalDownloadedBytes);
    }
}
