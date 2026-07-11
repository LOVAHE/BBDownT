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

    private static async Task<string> GetPlayJsonAsync(string encoding, string aidOri, string aid, string cid, string epId, bool tvApi, bool intl, bool appApi, string qn = "0")
    {
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
            apiBuilder.Append($"&wts={GetTimeStamp(true)}");
            api = prefix + (bangumi ? apiBuilder.ToString() : WbiSign(apiBuilder.ToString()));
        }

        //课程接口
        if (cheese) api = api.Replace("/pgc/", "/pugv/");

        //Console.WriteLine(api);
        string webJson = await GetWebSourceAsync(api);
        //以下情况从网页源代码尝试解析
        if (webJson.Contains("\"大会员专享限制\""))
        {
            Log("此视频需要大会员，您大概率需要登录一个有大会员的账号才可以下载，尝试从网页源码解析");
            string webUrl = "https://www.bilibili.com/bangumi/play/ep" + epId;
            string webSource = await GetWebSourceAsync(webUrl);
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
    {
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
                requestedQn),
            (requestedQn, code) => GetPlayJsonAsync(aid, cid, epId, requestedQn, code));
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
        Func<string, string, Task<string>> fetchIntlVariant)
    {
        ParsedResult parsedResult = new();

        //调用解析
        parsedResult.WebJsonString = await fetchPrimary(qn);

        LogDebug(parsedResult.WebJsonString);

        var data = ParseJsonRoot(parsedResult.WebJsonString);

        //intl接口
        if (IsIntlResponse(data))
        {
            PlayResponseMapper.MapIntl(
                data,
                parsedResult,
                url => BaseUrlRegex().IsMatch(url));

            parsedResult.WebJsonString = await fetchIntlVariant(qn, "1");
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
                parsedResult.WebJsonString = await fetchPrimary(GetMaxQn());
                data = ParseJsonRoot(parsedResult.WebJsonString);
                root = SelectResponseRoot(data);
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
            parsedResult.WebJsonString = await fetchPrimary(GetMaxQn());
            data = ParseJsonRoot(parsedResult.WebJsonString);
            root = SelectResponseRoot(data);
            PlayResponseMapper.MapDurl(root, parsedResult);
        }

        // 番剧片头片尾转分段信息, 预计效果: 正片? -> 片头 -> 正片 -> 片尾
        if (bangumi)
        {
            PlayResponseMapper.MapClipInfo(root, parsedResult);
        }

        return parsedResult;
    }

    /// <summary>
    /// 编码转换
    /// </summary>
    /// <param name="code"></param>
    /// <returns></returns>
    private static string GetVideoCodec(string code) => PlayResponseMapper.GetVideoCodec(code);

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
