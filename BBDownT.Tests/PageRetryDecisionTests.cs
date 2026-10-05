using System.Net.Sockets;

namespace BBDownT.Tests;

public class PageRetryDecisionTests
{
    [Fact]
    public void TransientNetworkError_GetsLongBackoffBudget()
    {
        var error = new HttpRequestException("不知道这样的主机。 (api.bilibili.com:443)", new SocketException(11001));
        var waits = new List<int>();
        for (var used = 0; used < Program.PageTransientMaxRetries; used++)
        {
            var decision = Program.DecidePageRetry(error, 0, used);
            Assert.True(decision.ShouldRetry);
            waits.Add(decision.DelayMilliseconds);
        }

        Assert.Equal(new[] { 5000, 10000, 20000, 40000, 60000, 60000 }, waits);
        Assert.False(Program.DecidePageRetry(error, 0, Program.PageTransientMaxRetries).ShouldRetry);
    }

    [Fact]
    public void OrdinaryError_KeepsTwoQuickRetries()
    {
        var error = new InvalidDataException("解析此分P失败");

        Assert.Equal(3000, Program.DecidePageRetry(error, 0, 0).DelayMilliseconds);
        Assert.True(Program.DecidePageRetry(error, 1, 0).ShouldRetry);
        Assert.False(Program.DecidePageRetry(error, Program.PageOrdinaryMaxRetries, 0).ShouldRetry);
    }

    [Fact]
    public void BothBudgetsAreCountedSeparately()
    {
        // 普通额度已用尽, 突然断网仍应按长退避继续等链路恢复
        Assert.True(Program.DecidePageRetry(new HttpRequestException("链路中断"), Program.PageOrdinaryMaxRetries, 0)
            .ShouldRetry);
        // 网络额度用尽后不再无限等待, 交回上层报失败
        Assert.False(Program.DecidePageRetry(new HttpRequestException("链路中断"), 0, Program.PageTransientMaxRetries)
            .ShouldRetry);
    }

    [Fact]
    public void DescribeFailure_AddsRootCauseWithoutStackTrace()
    {
        var error = new Exception("分片 70 下载失败",
            new HttpRequestException("不知道这样的主机。 (api.bilibili.com:443)"));

        var text = Program.DescribeFailure(error);

        Assert.Contains("分片 70 下载失败", text);
        Assert.Contains("根因: HttpRequestException: 不知道这样的主机", text);
        Assert.DoesNotContain("at BBDownT", text);
    }

    [Fact]
    public void DescribeFailure_KeepsSingleLayerMessageUntouched()
    {
        var error = new HttpRequestException("不知道这样的主机。 (api.bilibili.com:443)");

        Assert.Equal(error.Message, Program.DescribeFailure(error));
    }

    [Fact]
    public void DescribeFailureHint_NetworkFailureExplainsHowToResume()
    {
        var error = new Exception("P1 下载失败", new HttpRequestException("不知道这样的主机。", new SocketException(11001)));

        Assert.Contains("续传", Program.DescribeFailureHint(error));
    }

    [Fact]
    public void DescribeFailureHint_KeepsUpgradeTextForOtherErrors()
    {
        Assert.Contains("升级", Program.DescribeFailureHint(new InvalidOperationException("boom")));
    }
}
