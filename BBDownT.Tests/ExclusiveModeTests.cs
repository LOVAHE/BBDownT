namespace BBDownT.Tests;

public class ExclusiveModeTests
{
    [Fact]
    public void NeedsMuxer_ReturnsTrueForNormalDownload()
    {
        Assert.True(Program.NeedsMuxer(new MyOption()));
    }

    [Theory]
    [InlineData("skip-mux")]
    [InlineData("info")]
    [InlineData("cover")]
    [InlineData("subtitle")]
    [InlineData("danmaku")]
    public void NeedsMuxer_ReturnsFalseForNonMuxingModes(string mode)
    {
        var option = new MyOption
        {
            SkipMux = mode == "skip-mux",
            OnlyShowInfo = mode == "info",
            CoverOnly = mode == "cover",
            SubOnly = mode == "subtitle",
            DanmakuOnly = mode == "danmaku"
        };

        Assert.False(Program.NeedsMuxer(option));
    }
}
