using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using BBDownT.Core;
using BBDownT.Core.Util;
using static BBDownT.Core.Logger;

namespace BBDownT;

internal static class BBDownTCookieRefreshUtil
{
    private const string CookieInfoUrl = "https://passport.bilibili.com/x/passport-login/web/cookie/info";
    private const string RefreshCookieUrl = "https://passport.bilibili.com/x/passport-login/web/cookie/refresh";
    private const string ConfirmRefreshUrl = "https://passport.bilibili.com/x/passport-login/web/confirm/refresh";
    private const string CorrespondUrlPrefix = "https://www.bilibili.com/correspond/1/";

    private const string RefreshPublicKey = """
-----BEGIN PUBLIC KEY-----
MIGfMA0GCSqGSIb3DQEBAQUAA4GNADCBiQKBgQDLgd2OAkcGVtoE3ThUREbio0Eg
Uc/prcajMKXvkCKFCWhJYJcLkcM2DKKcSeFpD/j6Boy538YXnR6VhcuUJOhH2x71
nzPjfdTcqMz7djHum0qSZA0AyCBDABUqCrfNgCiJ00Ra7GmRj+YCK1NJEuewlb40
JNrRuoEUXpabUzGB8QIDAQAB
-----END PUBLIC KEY-----
""";

    private static readonly Regex RefreshCsrfRegex = new("<div id=\"1-name\">(.+?)</div>", RegexOptions.Compiled);
    private static readonly SemaphoreSlim RefreshLock = new(1, 1);

    public static async Task TryRefreshCookieAsync(string? cookieFilePath)
    {
        if (string.IsNullOrWhiteSpace(Config.COOKIE))
        {
            return;
        }

        await RefreshLock.WaitAsync();
        try
        {
            var cookies = ParseCookieHeader(Config.COOKIE);
            if (!HasValue(cookies, "SESSDATA") || !HasValue(cookies, "bili_jct"))
            {
                return;
            }

            if (!await ShouldRefreshCookieAsync(Config.COOKIE))
            {
                LogDebug("Cookie无需刷新");
                return;
            }

            var oldRefreshToken = GetRefreshToken(cookies);
            if (string.IsNullOrWhiteSpace(oldRefreshToken))
            {
                LogWarn("当前Cookie需要刷新，但缺少ac_time_value，请重新执行 BBDownT login 后再试。");
                return;
            }

            Log("检测到Cookie需要刷新，正在自动刷新...");
            var refreshCsrf = await GetRefreshCsrfAsync(Config.COOKIE);
            var refreshResult = await RefreshCookieAsync(Config.COOKIE, cookies["bili_jct"], refreshCsrf, oldRefreshToken);
            await ConfirmRefreshAsync(refreshResult.CookieHeader, refreshResult.BiliJct, oldRefreshToken);

            Config.COOKIE = refreshResult.CookieHeader;
            if (!string.IsNullOrWhiteSpace(cookieFilePath))
            {
                await File.WriteAllTextAsync(cookieFilePath, Config.COOKIE);
            }

            Log("Cookie刷新成功");
        }
        catch (Exception ex)
        {
            LogWarn($"自动刷新Cookie失败，将继续使用当前Cookie: {ex.Message}");
        }
        finally
        {
            RefreshLock.Release();
        }
    }

    public static string NormalizeLoginCookie(string loginUrl, string? refreshToken)
    {
        var queryStart = loginUrl.IndexOf('?');
        var query = queryStart >= 0 ? loginUrl[(queryStart + 1)..] : loginUrl;
        var fragmentStart = query.IndexOf('#');
        if (fragmentStart >= 0)
        {
            query = query[..fragmentStart];
        }

        var cookies = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in query.Split('&', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var separator = part.IndexOf('=');
            if (separator <= 0)
            {
                continue;
            }

            var name = part[..separator].Trim();
            var value = part[(separator + 1)..].Trim();
            if (name.Equals("refresh_token", StringComparison.OrdinalIgnoreCase))
            {
                refreshToken ??= value;
                continue;
            }

            cookies[name] = value.Replace(",", "%2C");
        }

        if (!string.IsNullOrWhiteSpace(refreshToken))
        {
            cookies["ac_time_value"] = refreshToken.Replace(",", "%2C");
        }

        return SerializeCookieHeader(cookies);
    }

    private static async Task<bool> ShouldRefreshCookieAsync(string cookieHeader)
    {
        using var request = CreateRequest(HttpMethod.Get, CookieInfoUrl, cookieHeader);
        using var response = await HTTPUtil.AppHttpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = doc.RootElement;
        if (!root.TryGetProperty("code", out var codeElement) || codeElement.GetInt32() != 0)
        {
            var message = root.TryGetProperty("message", out var messageElement) ? messageElement.ToString() : "未知错误";
            throw new InvalidOperationException($"检查Cookie刷新状态失败: {message}");
        }

        return root.GetProperty("data").GetProperty("refresh").GetBoolean();
    }

    private static async Task<string> GetRefreshCsrfAsync(string cookieHeader)
    {
        using var request = CreateRequest(HttpMethod.Get, CorrespondUrlPrefix + GenerateCorrespondPath(), WithCookieValue(cookieHeader, "buvid3", Guid.NewGuid().ToString()));
        using var response = await HTTPUtil.AppHttpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            throw new InvalidOperationException("correspondPath过期或错误");
        }
        response.EnsureSuccessStatusCode();

        var html = await response.Content.ReadAsStringAsync();
        var match = RefreshCsrfRegex.Match(html);
        if (!match.Success)
        {
            throw new InvalidOperationException("获取refresh_csrf失败");
        }

        return WebUtility.HtmlDecode(match.Groups[1].Value);
    }

    private static async Task<RefreshCookieResult> RefreshCookieAsync(string cookieHeader, string biliJct, string refreshCsrf, string oldRefreshToken)
    {
        using var request = CreateRequest(HttpMethod.Post, RefreshCookieUrl, WithCookieValue(cookieHeader, "buvid3", Guid.NewGuid().ToString()));
        request.Content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["csrf"] = biliJct,
            ["refresh_csrf"] = refreshCsrf,
            ["refresh_token"] = oldRefreshToken,
            ["source"] = "main_web"
        });

        using var response = await HTTPUtil.AppHttpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        if (!root.TryGetProperty("code", out var codeElement) || codeElement.GetInt32() != 0)
        {
            var message = root.TryGetProperty("message", out var messageElement) ? messageElement.ToString() : "未知错误";
            throw new InvalidOperationException($"刷新Cookie失败: {message}");
        }

        var responseCookies = ExtractSetCookies(response);
        var newRefreshToken = root.GetProperty("data").GetProperty("refresh_token").GetString();
        if (string.IsNullOrWhiteSpace(newRefreshToken))
        {
            throw new InvalidOperationException("刷新接口未返回新的refresh_token");
        }

        var mergedCookies = ParseCookieHeader(cookieHeader);
        foreach (var (name, value) in responseCookies)
        {
            mergedCookies[name] = value;
        }
        mergedCookies["ac_time_value"] = newRefreshToken;
        mergedCookies.Remove("refresh_token");

        if (!mergedCookies.TryGetValue("bili_jct", out var newBiliJct) || string.IsNullOrWhiteSpace(newBiliJct))
        {
            throw new InvalidOperationException("刷新响应缺少bili_jct");
        }

        return new RefreshCookieResult(SerializeCookieHeader(mergedCookies), newBiliJct);
    }

    private static async Task ConfirmRefreshAsync(string cookieHeader, string newBiliJct, string oldRefreshToken)
    {
        using var request = CreateRequest(HttpMethod.Post, ConfirmRefreshUrl, cookieHeader);
        request.Content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["csrf"] = newBiliJct,
            ["refresh_token"] = oldRefreshToken
        });

        using var response = await HTTPUtil.AppHttpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = doc.RootElement;
        if (root.TryGetProperty("code", out var codeElement) && codeElement.GetInt32() != 0)
        {
            var message = root.TryGetProperty("message", out var messageElement) ? messageElement.ToString() : "未知错误";
            throw new InvalidOperationException($"确认Cookie刷新失败: {message}");
        }
    }

    private static HttpRequestMessage CreateRequest(HttpMethod method, string url, string cookieHeader)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.TryAddWithoutValidation("User-Agent", HTTPUtil.UserAgent);
        request.Headers.TryAddWithoutValidation("Referer", "https://www.bilibili.com");
        request.Headers.TryAddWithoutValidation("Cookie", cookieHeader);
        request.Headers.CacheControl = CacheControlHeaderValue.Parse("no-cache");
        request.Headers.Connection.Clear();
        return request;
    }

    private static string GenerateCorrespondPath()
    {
        using var rsa = RSA.Create();
        rsa.ImportFromPem(RefreshPublicKey);
        var encrypted = rsa.Encrypt(
            System.Text.Encoding.UTF8.GetBytes($"refresh_{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}"),
            RSAEncryptionPadding.OaepSHA256);
        return Convert.ToHexString(encrypted).ToLowerInvariant();
    }

    private static Dictionary<string, string> ParseCookieHeader(string cookieHeader)
    {
        var cookies = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in cookieHeader.Split([';', '&'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var separator = part.IndexOf('=');
            if (separator <= 0)
            {
                continue;
            }

            cookies[part[..separator].Trim()] = part[(separator + 1)..].Trim();
        }

        return cookies;
    }

    private static Dictionary<string, string> ExtractSetCookies(HttpResponseMessage response)
    {
        var cookies = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!response.Headers.TryGetValues("Set-Cookie", out var values))
        {
            return cookies;
        }

        foreach (var value in values)
        {
            var firstPart = value.Split(';', 2, StringSplitOptions.TrimEntries)[0];
            var separator = firstPart.IndexOf('=');
            if (separator <= 0)
            {
                continue;
            }

            cookies[firstPart[..separator].Trim()] = firstPart[(separator + 1)..].Trim();
        }

        return cookies;
    }

    private static string WithCookieValue(string cookieHeader, string name, string value)
    {
        var cookies = ParseCookieHeader(cookieHeader);
        cookies[name] = value;
        return SerializeCookieHeader(cookies);
    }

    private static bool HasValue(IReadOnlyDictionary<string, string> cookies, string name)
    {
        return cookies.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value);
    }

    private static string? GetRefreshToken(IReadOnlyDictionary<string, string> cookies)
    {
        if (cookies.TryGetValue("ac_time_value", out var acTimeValue) && !string.IsNullOrWhiteSpace(acTimeValue))
        {
            return acTimeValue;
        }

        return cookies.TryGetValue("refresh_token", out var refreshToken) && !string.IsNullOrWhiteSpace(refreshToken)
            ? refreshToken
            : null;
    }

    private static string SerializeCookieHeader(IEnumerable<KeyValuePair<string, string>> cookies)
    {
        return string.Join(";", cookies
            .Where(cookie => !string.IsNullOrWhiteSpace(cookie.Key))
            .Select(cookie => $"{cookie.Key}={cookie.Value}"));
    }

    private record RefreshCookieResult(string CookieHeader, string BiliJct);
}
