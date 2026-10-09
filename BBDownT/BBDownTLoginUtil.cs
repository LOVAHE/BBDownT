using QRCoder;
using System;
using System.IO;
using System.Threading.Tasks;
using static BBDownT.BBDownTUtil;
using static BBDownT.Core.Logger;
using System.Text;
using System.Text.Json;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Linq;
using BBDownT.Core.Util;

namespace BBDownT;

internal enum QrLoginStatus { Waiting, Scanned, Expired, Success }

internal static class BBDownTLoginUtil
{
    internal static async Task<QrLoginStatus> WaitForQrLoginAsync(
        Func<Task<QrLoginStatus>> poll, Func<TimeSpan, Task> delay, TimeSpan interval,
        Action onScanned, int maxPolls = int.MaxValue)
    {
        var scanned = false;
        for (var count = 0; count < maxPolls; count++)
        {
            await delay(interval);
            var status = await poll();
            switch (status)
            {
                case QrLoginStatus.Waiting: break;
                case QrLoginStatus.Scanned:
                    if (!scanned) { onScanned(); scanned = true; }
                    break;
                case QrLoginStatus.Expired:
                case QrLoginStatus.Success: return status;
                default: throw new InvalidOperationException("登录接口返回了未知扫码状态");
            }
        }
        return QrLoginStatus.Waiting;
    }

    internal static void PrintQrCode(Uri url)
    {
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(url.AbsoluteUri, QRCodeGenerator.ECCLevel.Q);
        PrintQrCode(data);
    }

    private static void PrintQrCode(QRCodeData data)
    {
        try
        {
            Console.ForegroundColor = ConsoleColor.Black;
            Console.BackgroundColor = ConsoleColor.White;
            foreach (var row in CreateQrRows(data)) Console.WriteLine(row);
        }
        finally { Console.ResetColor(); }
    }

    internal static string[] CreateQrRows(QRCodeData data)
    {
        var matrix = data.ModuleMatrix;
        var rows = new string[(matrix.Count + 1) / 2];
        for (var y = 0; y < matrix.Count; y += 2)
        {
            var row = new char[matrix.Count];
            for (var x = 0; x < matrix.Count; x++)
            {
                var top = matrix[y][x];
                var bottom = y + 1 < matrix.Count && matrix[y + 1][x];
                row[x] = top ? bottom ? '█' : '▀' : bottom ? '▄' : ' ';
            }
            rows[y / 2] = new string(row);
        }
        return rows;
    }

    private static async Task ShowQrCodeAsync(string url)
    {
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(url, QRCodeGenerator.ECCLevel.Q);
        using var png = new PngByteQRCode(data);
        await File.WriteAllBytesAsync("qrcode.png", png.GetGraphic(7));
        Log("生成二维码成功: qrcode.png, 请打开并扫描, 或扫描打印的二维码");
        PrintQrCode(data);
    }

    internal static async Task SaveLoginDataAsync(string directory, string fileName, string content,
        Func<string, string, Task>? write = null)
    {
        var path = Path.Combine(directory, fileName);
        if (write is not null) { await write(path, content); return; }
        var temporary = path + $".writing-{Guid.NewGuid():N}";
        var options = new FileStreamOptions
        {
            Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None,
            Options = FileOptions.Asynchronous
        };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        var created = false;
        try
        {
            await using (var stream = new FileStream(temporary, options))
            {
                created = true;
                await stream.WriteAsync(Encoding.UTF8.GetBytes(content));
                await stream.FlushAsync();
            }
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (created && File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static async Task<LoginStatusResult> GetLoginStatusAsync(string qrcodeKey)
    {
        string queryUrl = $"https://passport.bilibili.com/x/passport-login/web/qrcode/poll?qrcode_key={qrcodeKey}&source=main-fe-header";
        using var request = new HttpRequestMessage(HttpMethod.Get, queryUrl);
        HTTPUtil.ApplyWebRequestHeaders(request, queryUrl, sendCookie: false, forceAuthenticatedProfile: true);
        request.Headers.TryAddWithoutValidation("Referer", "https://www.bilibili.com/");
        request.Headers.CacheControl = CacheControlHeaderValue.Parse("no-cache");

        using var response = (await HTTPUtil.AppHttpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead)).EnsureSuccessStatusCode();
        var responseBody = await response.Content.ReadAsStringAsync();
        var setCookieHeaders = response.Headers.TryGetValues("Set-Cookie", out var values)
            ? values.ToArray()
            : [];
        return new LoginStatusResult(responseBody, setCookieHeaders);
    }

    public static async Task LoginWEB()
    {
        try
        {
            AuthenticatedWebProfileStore.Configure(Program.APP_DIR);
            Log("获取登录地址...");
            string loginUrl = "https://passport.bilibili.com/x/passport-login/web/qrcode/generate?source=main-fe-header";
            using var qrDocument = JsonDocument.Parse(await HTTPUtil.GetAuthenticatedWebSourceAsync(loginUrl));
            string url = BilibiliApi.ReadPayload(qrDocument.RootElement, "获取登录二维码").GetProperty("url").ToString();
            string qrcodeKey = GetQueryString("qrcode_key", url);
            Log("生成二维码...");
            await ShowQrCodeAsync(url);
            LoginStatusResult loginStatus = default;
            var status = await WaitForQrLoginAsync(async () =>
            {
                loginStatus = await GetLoginStatusAsync(qrcodeKey);
                using var document = JsonDocument.Parse(loginStatus.ResponseBody);
                return BilibiliApi.ReadPayload(document.RootElement, "查询扫码状态").GetProperty("code").GetInt32() switch
                {
                    86038 => QrLoginStatus.Expired,
                    86101 => QrLoginStatus.Waiting,
                    86090 => QrLoginStatus.Scanned,
                    _ => QrLoginStatus.Success
                };
            }, Task.Delay, TimeSpan.FromSeconds(1), () => Log("扫码成功, 请确认..."));
            if (status == QrLoginStatus.Expired) { LogColor("二维码已过期, 请重新执行登录指令."); return; }
            using var loginDoc = JsonDocument.Parse(loginStatus.ResponseBody);
            var loginData = BilibiliApi.ReadPayload(loginDoc.RootElement, "完成登录");
            string cc = loginData.GetProperty("url").ToString();
            string? refreshToken = loginData.TryGetProperty("refresh_token", out var refreshTokenElement)
                ? refreshTokenElement.GetString() : null;
            var cookie = BBDownTCookieRefreshUtil.NormalizeLoginCookie(cc, refreshToken, loginStatus.SetCookieHeaders);
            if (!BBDownTCookieRefreshUtil.HasRequiredLoginCookies(cookie))
                throw new InvalidOperationException("登录响应缺少SESSDATA或bili_jct，未覆盖现有Cookie文件。");
            await SaveLoginDataAsync(Program.APP_DIR, "BBDownT.data", cookie);
            Log("登录成功");
            File.Delete("qrcode.png");
        }
        catch (Exception e) { LogError(ErrorText.Describe(e)); }
    }

    public static async Task LoginTV()
    {
        try
        {
            string loginUrl = "https://passport.snm0516.aisee.tv/x/passport-tv-login/qrcode/auth_code";
            string pollUrl = "https://passport.bilibili.com/x/passport-tv-login/qrcode/poll";
            var parms = GetTVLoginParms();
            Log("获取登录地址...");
            byte[] responseArray = await (await HTTPUtil.AppHttpClient.PostAsync(loginUrl, new FormUrlEncodedContent(parms.ToDictionary()))).Content.ReadAsByteArrayAsync();
            string web = Encoding.UTF8.GetString(responseArray);
            using var qrDocument = JsonDocument.Parse(web);
            var qrData = BilibiliApi.ReadPayload(qrDocument.RootElement, "获取登录二维码");
            string url = qrData.GetProperty("url").ToString();
            string authCode = qrData.GetProperty("auth_code").ToString();
            Log("生成二维码...");
            await ShowQrCodeAsync(url);
            parms.Set("auth_code", authCode);
            parms.Set("ts", GetTimeStamp(true));
            parms.Remove("sign");
            parms.Add("sign", GetSign(ToQueryString(parms)));
            var status = await WaitForQrLoginAsync(async () =>
            {
                responseArray = await (await HTTPUtil.AppHttpClient.PostAsync(pollUrl, new FormUrlEncodedContent(parms.ToDictionary()))).Content.ReadAsByteArrayAsync();
                web = Encoding.UTF8.GetString(responseArray);
                using var document = JsonDocument.Parse(web);
                return document.RootElement.GetProperty("code").ToString() switch
                {
                    "86038" => QrLoginStatus.Expired,
                    "86039" => QrLoginStatus.Waiting,
                    _ => QrLoginStatus.Success
                };
            }, Task.Delay, TimeSpan.FromSeconds(1), () => Log("扫码成功, 请确认..."));
            if (status == QrLoginStatus.Expired) { LogColor("二维码已过期, 请重新执行登录指令."); return; }
            using var loginDoc = JsonDocument.Parse(web);
            string cc = BilibiliApi.ReadPayload(loginDoc.RootElement, "完成登录").GetProperty("access_token").ToString();
            await SaveLoginDataAsync(Program.APP_DIR, "BBDownTTV.data", "access_token=" + cc);
            Log("登录成功");
            File.Delete("qrcode.png");
        }
        catch (Exception e) { LogError(ErrorText.Describe(e)); }
    }

    private readonly record struct LoginStatusResult(string ResponseBody, string[] SetCookieHeaders);
}
