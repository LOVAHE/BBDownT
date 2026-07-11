namespace BBDownT.Core;

/// <summary>
/// Current Bilibili app compatibility assumptions.
/// Keep changes here literal-regression-tested because these upstream values may drift.
/// </summary>
internal static class BilibiliAppProtocol
{
    public const string UgcPlayViewEndpoint =
        "https://grpc.biliapi.net/bilibili.app.playurl.v1.PlayURL/PlayView";

    public const string PgcPlayViewEndpoint =
        "https://app.bilibili.com/bilibili.pgc.gateway.player.v2.PlayURL/PlayView";

    public const string GrpcAuthority = "grpc.biliapi.net";
    public const long MaximumQuality = 127;
}
