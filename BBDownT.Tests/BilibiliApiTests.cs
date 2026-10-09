using System.Text.Json;
using BBDownT.Core.Util;

namespace BBDownT.Tests;

public class BilibiliApiTests
{
    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement;

    [Fact]
    public void ReadPayload_ReturnsTheDataOfASuccessfulAnswer()
    {
        var payload = BilibiliApi.ReadPayload(Parse("{\"code\":0,\"data\":{\"bvid\":\"BV1xx411c7mD\"}}"), "获取视频信息");

        Assert.Equal("BV1xx411c7mD", payload.GetProperty("bvid").GetString());
    }

    [Theory]
    [InlineData("{\"code\":-404,\"message\":\"啥都木有\"}", "获取视频信息失败：啥都木有（错误码 -404）", -404)]
    [InlineData("{\"code\":-403,\"message\":\"-403\"}", "获取视频信息失败：接口拒绝了请求（错误码 -403）", -403)]
    [InlineData("{\"code\":\"-10403\"}", "获取视频信息失败：接口拒绝了请求（错误码 -10403）", -10403)]
    [InlineData("{\"code\":0,\"data\":null}", "获取视频信息失败：接口没有返回数据", 0)]
    [InlineData("{\"code\":0}", "获取视频信息失败：接口没有返回数据", 0)]
    public void ReadPayload_ReportsRefusalsAndMissingData(string json, string message, int code)
    {
        var error = Assert.Throws<BilibiliApiException>(() => BilibiliApi.ReadPayload(Parse(json), "获取视频信息"));

        Assert.Equal(message, error.Message);
        Assert.Equal(code, error.Code);
    }

    [Fact]
    public void ReadPayload_ReadsTheResultPropertyOfPgcAnswers()
    {
        var payload = BilibiliApi.ReadPayload(Parse("{\"code\":0,\"result\":{\"season_id\":1}}"), "获取番剧信息", "result");

        Assert.Equal(1, payload.GetProperty("season_id").GetInt32());
    }

    [Fact]
    public void GetError_IgnoresTextThatIsNotJson()
    {
        Assert.Null(BilibiliApi.GetError("<html>502</html>", "获取播放地址"));
        Assert.Null(BilibiliApi.GetError("{\"code\":0,\"data\":{}}", "获取播放地址"));
        Assert.Equal(-10403, BilibiliApi.GetError("{\"code\":-10403,\"message\":\"大会员专享\"}", "获取播放地址")!.Code);
    }

    [Theory]
    [InlineData(-412, true)]
    [InlineData(-352, true)]
    [InlineData(-799, true)]
    [InlineData(-509, true)]
    [InlineData(-404, false)]
    [InlineData(-101, false)]
    public void RateLimitCodes_AreTheOnlyRetryableRefusals(int code, bool rateLimited)
    {
        Assert.Equal(rateLimited, BilibiliApi.IsRateLimited(code));
    }
}
