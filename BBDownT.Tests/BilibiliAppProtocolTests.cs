using BBDownT.Core;
using BBDownT.Core.Protobuf;

namespace BBDownT.Tests;

public class BilibiliAppProtocolTests
{
    [Fact]
    public void Payload_RequestsCompatibilityProfileMaximumQuality()
    {
        var framed = AppHelper.GetPayload(
            1,
            2,
            64,
            PlayViewReq.Types.CodeType.Code265);

        var request = PlayViewReq.Parser.ParseFrom(AppHelper.ReadMessage(framed));

        Assert.Equal(127, BilibiliAppProtocol.MaximumQuality);
        Assert.Equal(127, request.Qn);
    }

    [Fact]
    public void Headers_UseCompatibilityProfileAuthority()
    {
        var headers = AppHelper.GetHeader("");

        Assert.Equal("grpc.biliapi.net", BilibiliAppProtocol.GrpcAuthority);
        Assert.Equal("grpc.biliapi.net", headers["Host"]);
    }

    [Fact]
    public void Endpoints_MatchCurrentCompatibilityAssumptions()
    {
        Assert.Equal(
            "https://grpc.biliapi.net/bilibili.app.playurl.v1.PlayURL/PlayView",
            BilibiliAppProtocol.UgcPlayViewEndpoint);
        Assert.Equal(
            "https://app.bilibili.com/bilibili.pgc.gateway.player.v2.PlayURL/PlayView",
            BilibiliAppProtocol.PgcPlayViewEndpoint);
    }
}
