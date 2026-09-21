using System.Text;
using System.Text.RegularExpressions;
using System.Text.Json;
using System.IO;
using static BBDownT.Core.Logger;
using static BBDownT.Core.Util.HTTPUtil;
using static BBDownT.Core.Entity.Entity;
using System.Security.Cryptography;
using BBDownT.Core.Entity;
using BBDownT.Core.Util;

namespace BBDownT.Core;

public static partial class Parser
{
    private const int RiskControlMaxAttempts = 3;
    private const int RiskControlRetryDelayMilliseconds = 1000;

    public static string WbiSign(string api)
    {
        return $"{api}&w_rid=" + string.Concat(MD5.HashData(Encoding.UTF8.GetBytes(api + Config.WBI)).Select(i => i.ToString("x2")).ToArray());
    }

    internal static bool IsIntlResponse(JsonElement documentRoot)
    {
        return documentRoot.TryGetProperty("data", out var data)
            && data.ValueKind == JsonValueKind.Object
            && data.TryGetProperty("video_info", out var videoInfo)
            && videoInfo.ValueKind == JsonValueKind.Object
            && videoInfo.TryGetProperty("stream_list", out var streamList)
            && streamList.ValueKind == JsonValueKind.Array;
    }

    internal static JsonElement SelectResponseRoot(JsonElement documentRoot)
    {
        if (documentRoot.TryGetProperty("result", out var result)
            && result.ValueKind == JsonValueKind.Object)
        {
            if (result.TryGetProperty("video_info", out var videoInfo)
                && videoInfo.ValueKind == JsonValueKind.Object)
            {
                return videoInfo;
            }

            return result;
        }

        if (documentRoot.TryGetProperty("data", out var data)
            && data.ValueKind == JsonValueKind.Object)
        {
            return data;
        }

        return documentRoot;
    }

    internal static async Task<string> GetPlayJsonAsync(string encoding, string aidOri, string aid, string cid, string epId, bool tvApi, bool intl, bool appApi, string qn = "0", string? audioLanguage = null, Func<string, Task<string>>? fetchWeb = null)
    {
        ValidateAudioLanguageMode(audioLanguage, tvApi, intl, appApi);
        LogDebug("aid={0},cid={1},epId={2},tvApi={3},IntlApi={4},appApi={5},qn={6}", aid, cid, epId, tvApi, intl, appApi, qn);

        if (intl) return await GetPlayJsonAsync(aid, cid, epId, qn);


        bool cheese = aidOri.StartsWith("cheese:");
        bool bangumi = cheese || aidOri.StartsWith("ep:");
        LogDebug("bangumi={0},cheese={1}", bangumi, cheese);

        if (appApi) return await AppHelper.DoReqAsync(aid, cid, epId, qn, bangumi, encoding, Config.TOKEN);

        string prefix = tvApi ? bangumi ? $"{Config.TVHOST}/pgc/player/api/playurltv" : $"{Config.TVHOST}/x/tv/playurl"
            : bangumi ? $"{Config.HOST}/pgc/player/web/v2/playurl" : "api.bilibili.com/x/player/wbi/playurl";
        prefix = $"https://{prefix}?";

        string api;
        if (tvApi)
        {
            StringBuilder apiBuilder = new();
            if (Config.TOKEN != "") apiBuilder.Append($"access_key={Config.TOKEN}&");
            apiBuilder.Append($"appkey=4409e2ce8ffd12b8&build=106500&cid={cid}&device=android");
            if (bangumi) apiBuilder.Append($"&ep_id={epId}&expire=0");
            apiBuilder.Append($"&fnval=4048&fnver=0&fourk=1&mid=0&mobi_app=android_tv_yst");
            apiBuilder.Append($"&object_id={aid}&platform=android&playurl_type=1&qn={qn}&ts={GetTimeStamp(true)}");
            api = $"{prefix}{apiBuilder}&sign={GetSign(apiBuilder.ToString(), false)}";
        }
        else
        {
            // 尝试提高可读性
            StringBuilder apiBuilder = new();
            apiBuilder.Append($"support_multi_audio=true&from_client=BROWSER&avid={aid}&cid={cid}&fnval=4048&fnver=0&fourk=1");
            if (Config.AREA != "") apiBuilder.Append($"&access_key={Config.TOKEN}&area={Config.AREA}");
            apiBuilder.Append($"&otype=json&qn={qn}");
            if (bangumi) apiBuilder.Append($"&module=bangumi&ep_id={epId}&session=");
            if (Config.COOKIE == "") apiBuilder.Append("&try_look=1");
            if (!string.IsNullOrEmpty(audioLanguage)) apiBuilder.Append($"&cur_language={Uri.EscapeDataString(audioLanguage)}");
            apiBuilder.Append($"&wts={GetTimeStamp(true)}");
            api = prefix + (bangumi ? apiBuilder.ToString() : WbiSign(apiBuilder.ToString()));
        }

        //课程接口
        if (cheese) api = api.Replace("/pgc/", "/pugv/");

        //Console.WriteLine(api);
        var fetch = fetchWeb ?? (url => GetWebSourceAsync(url));
        string webJson = await fetch(api);
        //以下情况从网页源代码尝试解析
        if (webJson.Contains("\"大会员专享限制\""))
        {
            Log("此视频需要大会员，您大概率需要登录一个有大会员的账号才可以下载，尝试从网页源码解析");
            string webUrl = "https://www.bilibili.com/bangumi/play/ep" + epId;
            string webSource = await fetch(webUrl);
            webJson = PlayerJsonRegex().Match(webSource).Groups[1].Value;
        }
        return webJson;
    }

    private static async Task<string> GetPlayJsonAsync(string aid, string cid, string epId, string qn, string code = "0")
    {
        bool isBiliPlus = Config.HOST != "api.bilibili.com";
        string api = $"https://{(isBiliPlus ? Config.HOST : "api.biliintl.com")}/intl/gateway/v2/ogv/playurl?";

        StringBuilder paramBuilder = new();
        if (Config.TOKEN != "") paramBuilder.Append($"access_key={Config.TOKEN}&");
        paramBuilder.Append($"aid={aid}");
        if (isBiliPlus) paramBuilder.Append($"&appkey=7d089525d3611b1c&area={(Config.AREA == "" ? "th" : Config.AREA)}");
        paramBuilder.Append($"&cid={cid}&ep_id={epId}&platform=android&prefer_code_type={code}&qn={qn}");
        if (isBiliPlus) paramBuilder.Append($"&ts={GetTimeStamp(true)}");

        paramBuilder.Append("&s_locale=zh_SG");
        string param = paramBuilder.ToString();
        api += (isBiliPlus ? $"{param}&sign={GetSign(param, true)}" : param);

        string webJson = await GetWebSourceAsync(api);
        return webJson;
    }

    public static Task<ParsedResult> ExtractTracksAsync(string aidOri, string aid, string cid, string epId, bool tvApi, bool intlApi, bool appApi, string encoding, string qn = "0")
        => ExtractTracksAsync(aidOri, aid, cid, epId, tvApi, intlApi, appApi, encoding, qn, null);

    public static Task<ParsedResult> ExtractTracksAsync(string aidOri, string aid, string cid, string epId, bool tvApi, bool intlApi, bool appApi, string encoding, string qn, string? audioLanguage)
    {
        ValidateAudioLanguageMode(audioLanguage, tvApi, intlApi, appApi);
        return ExtractTracksWithFetcherAsync(
            aidOri,
            aid,
            cid,
            epId,
            tvApi,
            intlApi,
            appApi,
            qn,
            requestedQn => GetPlayJsonAsync(
                encoding,
                aidOri,
                aid,
                cid,
                epId,
                tvApi,
                intlApi,
                appApi,
                requestedQn,
                audioLanguage),
            (requestedQn, code) => GetPlayJsonAsync(aid, cid, epId, requestedQn, code),
            audioLanguage);
    }

    private static void ValidateAudioLanguageMode(string? language, bool tvApi, bool intlApi, bool appApi)
    {
        if (!string.IsNullOrEmpty(language) && (tvApi || intlApi || appApi))
            throw new ArgumentException("配音语言选择目前仅支持默认 WEB 解析模式");
    }

    internal static async Task<ParsedResult> ExtractTracksWithFetcherAsync(
        string aidOri,
        string aid,
        string cid,
        string epId,
        bool tvApi,
        bool intlApi,
        bool appApi,
        string qn,
        Func<string, Task<string>> fetchPrimary,
        Func<string, string, Task<string>> fetchIntlVariant,
        string? requestedAudioLanguage = null,
        Func<int, Task>? riskControlDelay = null,
        Func<bool>? rotateUserAgent = null)
    {
        ParsedResult parsedResult = new();
        riskControlDelay ??= Task.Delay;
        rotateUserAgent ??= HTTPUtil.PrepareRiskControlRetry;

        Task<string> FetchPrimaryAsync(string requestedQn) =>
            FetchPlayResponseWithRiskControlRetryAsync(
                () => fetchPrimary(requestedQn), riskControlDelay, rotateUserAgent);

        Task<string> FetchIntlVariantAsync(string requestedQn, string code) =>
            FetchPlayResponseWithRiskControlRetryAsync(
                () => fetchIntlVariant(requestedQn, code), riskControlDelay, rotateUserAgent);

        //调用解析
        parsedResult.WebJsonString = await FetchPrimaryAsync(qn);

        LogDebug(parsedResult.WebJsonString);

        var data = ParseJsonRoot(parsedResult.WebJsonString);

        //intl接口
        if (IsIntlResponse(data))
        {
            PlayResponseMapper.MapIntl(
                data,
                parsedResult,
                url => BaseUrlRegex().IsMatch(url));

            parsedResult.WebJsonString = await FetchIntlVariantAsync(qn, "1");
            data = ParseJsonRoot(parsedResult.WebJsonString);
            if (IsIntlResponse(data))
            {
                PlayResponseMapper.MapIntl(
                    data,
                    parsedResult,
                    url => BaseUrlRegex().IsMatch(url));
                return parsedResult;
            }
        }
        var root = SelectResponseRoot(data);
        AudioLanguageMapper.Map(root, parsedResult, requestedAudioLanguage);

        bool bangumi = aidOri.StartsWith("ep:");

        if (root.TryGetProperty("dash", out var dashNode) && dashNode.ValueKind == JsonValueKind.Object) //dash
        {
            int pDur = PlayResponseMapper.GetDurationSeconds(root);
            var audioData = data;
            var audioRoot = root;

            PlayResponseMapper.MapDashVideos(
                root,
                parsedResult,
                pDur,
                tvApi,
                appApi,
                url => BaseUrlRegex().IsMatch(url));

            // 此处处理免二压视频，需要单独再请求一次。
            if (!appApi)
            {
                parsedResult.WebJsonString = await FetchPrimaryAsync(GetMaxQn());
                data = ParseJsonRoot(parsedResult.WebJsonString);
                root = SelectResponseRoot(data);
                AudioLanguageMapper.Map(root, parsedResult, requestedAudioLanguage);
                PlayResponseMapper.MapDashVideos(
                    root,
                    parsedResult,
                    pDur,
                    tvApi,
                    appApi,
                    url => BaseUrlRegex().IsMatch(url));
                if (PlayResponseMapper.HasDashAudio(root))
                {
                    audioData = data;
                    audioRoot = root;
                }
            }

            PlayResponseMapper.MapDashAudioAndDubbing(
                audioData,
                audioRoot,
                parsedResult,
                pDur,
                aid,
                cid,
                tvApi,
                appApi,
                bangumi,
                url => BaseUrlRegex().IsMatch(url));
        }
        else if (root.TryGetProperty("durl", out var durlNode) && durlNode.ValueKind == JsonValueKind.Array) //flv
        {
            //默认以最高清晰度解析
            parsedResult.WebJsonString = await FetchPrimaryAsync(GetMaxQn());
            data = ParseJsonRoot(parsedResult.WebJsonString);
            root = SelectResponseRoot(data);
            AudioLanguageMapper.Map(root, parsedResult, requestedAudioLanguage);
            PlayResponseMapper.MapDurl(root, parsedResult);
        }

        // 番剧片头片尾转分段信息, 预计效果: 正片? -> 片头 -> 正片 -> 片尾
        if (bangumi)
        {
            PlayResponseMapper.MapClipInfo(root, parsedResult);
        }

        return parsedResult;
    }

    internal static async Task<string> FetchPlayResponseWithRiskControlRetryAsync(
        Func<Task<string>> fetch,
        Func<int, Task> delay,
        Func<bool> rotateUserAgent)
    {
        string response = string.Empty;
        for (int attempt = 1; attempt <= RiskControlMaxAttempts; attempt++)
        {
            response = await fetch();
            if (!IsRiskControlVoucherResponse(response)) return response;

            if (attempt == RiskControlMaxAttempts)
            {
                LogWarn($"B站播放接口持续返回风控凭证(v_voucher)，已自动请求{RiskControlMaxAttempts}次。若本次解析失败，请稍后重试，或通过 --user-agent 指定浏览器User-Agent。");
                return response;
            }

            bool rotated = rotateUserAgent();
            LogWarn(rotated
                ? $"B站播放接口返回风控凭证(v_voucher)，正在更换User-Agent后重试... ({attempt}/{RiskControlMaxAttempts - 1})"
                : $"B站播放接口返回风控凭证(v_voucher)，正在重试... ({attempt}/{RiskControlMaxAttempts - 1})");
            await delay(RiskControlRetryDelayMilliseconds);
        }

        return response;
    }

    internal static bool IsRiskControlVoucherResponse(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.TryGetProperty("data", out var data)
                && data.ValueKind == JsonValueKind.Object
                && data.TryGetProperty("v_voucher", out var voucher)
                && voucher.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(voucher.GetString());
        }
        catch (JsonException)
        {
            return false;
        }
    }

    internal static JsonElement ParseJsonRoot(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static string GetMaxQn()
    {
        return Config.qualitys.Keys.First();
    }

    private static string GetTimeStamp(bool bflag)
    {
        DateTimeOffset ts = DateTimeOffset.Now;
        return bflag ? ts.ToUnixTimeSeconds().ToString() : ts.ToUnixTimeMilliseconds().ToString();
    }

    private static string GetSign(string parms, bool isBiliPlus)
    {
        string toEncode = parms + (isBiliPlus ? "acd495b248ec528c2eed1e862d393126" : "59b43e04ad6965f34319062b478f83dd");
        return string.Concat(MD5.HashData(Encoding.UTF8.GetBytes(toEncode)).Select(i => i.ToString("x2")).ToArray());
    }

    [GeneratedRegex("window.__playinfo__=([\\s\\S]*?)<\\/script>")]
    private static partial Regex PlayerJsonRegex();
    [GeneratedRegex("http.*:\\d+")]
    private static partial Regex BaseUrlRegex();
}
