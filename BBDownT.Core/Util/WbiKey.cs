using System.Text.Json;
using static BBDownT.Core.Logger;

namespace BBDownT.Core.Util;

internal static class WbiKey
{
    internal const string NavApi = "https://api.bilibili.com/x/web-interface/nav";

    private static readonly byte[] MixinKeyEncTab =
    [
        46, 47, 18, 2, 53, 8, 23, 32, 15, 50, 10, 31, 58, 3, 45, 35,
        27, 43, 5, 49, 33, 9, 42, 19, 29, 28, 14, 39, 12, 38, 41, 13
    ];

    internal static async Task EnsureAsync(Func<string, Task<string>> fetch)
    {
        if (Config.WBI != "") return;
        try
        {
            using var nav = JsonDocument.Parse(await fetch(NavApi));
            if (!TryUpdate(nav.RootElement))
                LogWarn("获取WBI签名密钥失败：接口未返回密钥，相关请求可能被拒绝");
        }
        catch (Exception error)
        {
            LogWarn($"获取WBI签名密钥失败：{ErrorText.Describe(error)}，相关请求可能被拒绝");
        }
    }

    internal static bool TryUpdate(JsonElement navRoot)
    {
        if (navRoot.ValueKind != JsonValueKind.Object
            || !navRoot.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object
            || !data.TryGetProperty("wbi_img", out var images) || images.ValueKind != JsonValueKind.Object
            || !images.TryGetProperty("img_url", out var imgUrl) || imgUrl.ValueKind != JsonValueKind.String
            || !images.TryGetProperty("sub_url", out var subUrl) || subUrl.ValueKind != JsonValueKind.String)
            return false;
        var orig = FileStem(imgUrl.GetString()!) + FileStem(subUrl.GetString()!);
        if (MixinKeyEncTab.Any(index => index >= orig.Length)) return false;
        Config.WBI = string.Concat(MixinKeyEncTab.Select(index => orig[index]));
        LogDebug("wbi: {0}", Config.WBI);
        return true;
    }

    private static string FileStem(string url)
    {
        url = url[(url.LastIndexOf('/') + 1)..];
        var dot = url.LastIndexOf('.');
        return dot < 0 ? url : url[..dot];
    }
}
