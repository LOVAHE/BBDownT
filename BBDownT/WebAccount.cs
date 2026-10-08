using System.Text.Json;
using System.Threading.Tasks;
using BBDownT.Core;
using BBDownT.Core.Util;

namespace BBDownT;

/// <summary>
/// WEB账号状态(来自 /x/web-interface/nav)，用于解析预览提示画质受限的原因
/// </summary>
internal sealed record WebAccount(bool IsLogin, string? UserName, bool IsVip, string? VipLabel)
{
    internal static readonly WebAccount Anonymous = new(false, null, false, null);

    internal static async Task<WebAccount> FetchAsync()
    {
        var source = await HTTPUtil.GetWebSourceAsync("https://api.bilibili.com/x/web-interface/nav");
        var account = Parse(source, out var wbi);
        // 与 CheckLogin 一致：nav 同时下发 WBI 签名所需的 key
        if (!string.IsNullOrEmpty(wbi)) Config.WBI = wbi;
        return account;
    }

    internal static WebAccount Parse(string navJson, out string? wbiKey)
    {
        wbiKey = null;
        using var document = JsonDocument.Parse(navJson);
        if (!document.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
            return Anonymous;
        if (data.TryGetProperty("wbi_img", out var wbiImg)
            && wbiImg.TryGetProperty("img_url", out var img) && img.GetString() is { } imgUrl
            && wbiImg.TryGetProperty("sub_url", out var sub) && sub.GetString() is { } subUrl)
        {
            wbiKey = BBDownTUtil.GetMixinKey(BBDownTUtil.RSubString(imgUrl) + BBDownTUtil.RSubString(subUrl));
        }
        var isLogin = data.TryGetProperty("isLogin", out var login) && login.ValueKind == JsonValueKind.True;
        if (!isLogin) return Anonymous;
        var name = data.TryGetProperty("uname", out var uname) ? uname.GetString() : null;
        var isVip = data.TryGetProperty("vipStatus", out var vipStatus) && vipStatus.TryGetInt32(out var status) && status == 1;
        string? label = null;
        if (data.TryGetProperty("vip_label", out var vipLabel) && vipLabel.ValueKind == JsonValueKind.Object
            && vipLabel.TryGetProperty("text", out var text))
        {
            label = text.GetString();
        }
        return new WebAccount(true, name, isVip, string.IsNullOrWhiteSpace(label) ? null : label);
    }
}
