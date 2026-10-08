using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BBDownT.Core;
using BBDownT.Core.Entity;
using BBDownT.Core.Util;
using static BBDownT.Core.Entity.Entity;

namespace BBDownT;

/// <summary>
/// 服务器“解析预览”：执行与下载任务相同的解析，返回可选的音视频流，不下载、不写下载目录。
/// 与下载队列并发运行，因此不读写全局登录态：只使用请求里显式给出的Cookie/Token或数据目录里保存的登录，
/// 并通过 Config.UseCredentials 固定在本次解析的异步流程内(解析相关的Host、Area也固定为默认值)。
/// 列表类链接(UP主空间、收藏夹、合集、系列)会逐页拉取整个列表，直接拒绝。
/// 只接受B站的链接：其他网址不会由服务器代为请求。
/// 取消令牌经 HTTPUtil.FlowCancellation 传给每个HTTP请求，超时或客户端断开后立即中止。
/// </summary>
internal sealed class VideoParseService(VideoParseService.Dependencies deps)
{
    internal const int MaxListedPages = 1000;

    internal sealed record Dependencies(
        Func<string, Task<string>> GetAvId,
        Func<MyOption, string, Task<(string Aid, VInfo Info, string ApiType)>> FetchInfo,
        Func<MyOption, string, Page, string, Task<ParsedResult>> FetchTracks,
        Func<MyOption, Page, Task<List<Subtitle>>> FetchSubtitles,
        Func<Task<WebAccount>> GetWebAccount,
        Func<string, string?> ReadDataFile,
        Action ConfigureWebProfile);

    internal static VideoParseService Default { get; } = new(new Dependencies(
        BBDownTUtil.GetAvIdAsync,
        Program.FetchVideoInfoAsync,
        FetchTracksAsync,
        (option, page) => SubUtil.GetSubtitlesAsync(page.DownloadId, page.cid, page.epid, page.index, option.UseIntlApi),
        WebAccount.FetchAsync,
        name =>
        {
            var path = Path.Combine(Program.APP_DIR, name);
            return File.Exists(path) ? File.ReadAllText(path) : null;
        },
        // 与下载任务一致：带Cookie的请求使用数据目录里保存的浏览器请求配置
        () => AuthenticatedWebProfileStore.Configure(Program.APP_DIR)));

    internal async Task<ParseResponse> ParseAsync(ParseRequest request, CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        using var cancellation = HTTPUtil.UseCancellation(cancellationToken);
        // 解析过程可能修改选项(如互动视频回退WEB)，不影响调用方
        var option = (ParseRequest)request.CloneOption();
        var requestedApi = Program.GetApiType(option);

        // 只用请求里显式给出的Cookie，或数据目录里保存的登录(网页 BBDownT.data，国际站 BBDownTIntl.data)；
        // 不借用全局Config.COOKIE，它可能是另一个正在运行的任务自带的Cookie
        var cookie = (!string.IsNullOrWhiteSpace(option.Cookie) ? option.Cookie
            : deps.ReadDataFile(option.UseIntlApi ? IntlCookieStore.FileName : "BBDownT.data") ?? "").Trim();
        if (option.UseIntlApi && cookie.Length > 0) cookie = IntlCookieStore.Normalize(cookie);
        var tvToken = deps.ReadDataFile("BBDownTTV.data");
        var appToken = deps.ReadDataFile("BBDownTApp.data");
        // 与下载任务相同：国际站优先于APP/TV接口，不使用TV/APP的access_token
        var token = option.UseIntlApi ? "" : NormalizeToken(!string.IsNullOrWhiteSpace(option.AccessToken) ? option.AccessToken
            : option.UseTvApi ? tvToken : option.UseAppApi ? appToken : null);

        using var _ = Config.UseCredentials(cookie, token, option.UseIntlApi);
        if (!string.IsNullOrEmpty(cookie)) deps.ConfigureWebProfile();

        // 认不出的网址会落到 GetAvIdAsync 的兜底分支(抓取网页再按番剧页面解析)，不能让服务器代为请求任意网址
        if (!IsSupportedInput(option.Url)) throw new ArgumentException(UnrecognizedInputMessage);
        string aidOri;
        try { aidOri = await deps.GetAvId(option.Url); }
        catch (Exception e) when (e.Message == "输入有误"
            || e is JsonException or KeyNotFoundException or InvalidOperationException or FormatException or IndexOutOfRangeException)
        {
            // 兜底分支把B站的其他页面(如活动页)当作番剧页面解析失败；NativeAOT下这些异常的消息只剩资源键
            Logger.LogDebug("解析预览无法识别输入: {0}", e.GetType().Name);
            throw new ArgumentException(UnrecognizedInputMessage);
        }
        if (string.IsNullOrEmpty(aidOri)) throw new ArgumentException(UnrecognizedInputMessage);
        if (ListLinkName(aidOri) is { } listName) throw new ParseRejectedException(ListLinkMessage(listName));

        // null 表示没能查到登录状态(网络错误等)，此时不给出登录相关的提示
        WebAccount? account;
        // 国际站Cookie不用于B站网页登录状态
        try { account = option.UseIntlApi ? WebAccount.Anonymous : await deps.GetWebAccount(); }
        catch (Exception e)
        {
            Logger.LogDebug("解析预览获取账号状态失败: {0}", e.Message);
            account = null;
        }

        var (aid, info, apiType) = await deps.FetchInfo(option, aidOri);
        if (info.PagesInfo.Count == 0) throw new InvalidDataException("没有获取到分P信息");
        ValidateSelectPage(option, info);
        // 与下载任务相同的默认分P(链接里的p=或集数，否则全部)；null 表示全部
        var selectedPages = Program.GetSelectedPages(option, info, option.Url);
        var page = ChooseStreamsPage(info, request.Page, selectedPages);

        var encodingPriority = Program.ParseEncodingPriority(option, out var firstEncoding);
        var dfnPriority = Program.ParseDfnPriority(option);

        var tracks = option.SubOnly ? new ParsedResult()
            : await deps.FetchTracks(option, Program.GetIntlPlaybackId(info, aidOri), page, firstEncoding ?? "");

        List<Subtitle> subtitles = [];
        if (!option.SkipSubtitle)
        {
            try { subtitles = await deps.FetchSubtitles(option, page); }
            catch (Exception e) { Logger.LogDebug("解析预览获取字幕失败: {0}", e.Message); }
        }
        // 字幕接口的错误(包括取消)会被吞掉，这里确认没有被取消，避免把不完整的结果当作成功返回
        cancellationToken.ThrowIfCancellationRequested();

        var web = account ?? WebAccount.Anonymous;
        var authenticated = option.UseIntlApi ? true
            : option.UseTvApi || option.UseAppApi ? !string.IsNullOrEmpty(token)
            : web.IsLogin;
        var accountInfo = new ParseAccount(web.IsLogin, web.UserName, web.IsVip, web.VipLabel,
            !string.IsNullOrWhiteSpace(tvToken), !string.IsNullOrWhiteSpace(appToken), authenticated);

        var downloadPages = selectedPages?
            .Select(p => int.TryParse(p, out var index) ? index : 0)
            .Where(index => index > 0)
            .ToList();
        return Build(option, info, aid, page, tracks, dfnPriority, encodingPriority,
            subtitles, accountInfo, cookie, requestedApi, apiType, stopwatch.ElapsedMilliseconds, account is not null, downloadPages);
    }

    internal const string UnrecognizedInputMessage =
        "无法识别的链接或编号。支持 BV/av/ep/ss/md 号、b23.tv 短链、bilibili.com 视频、番剧、课程链接和 bilibili.tv、biliintl.com 国际站番剧链接。";

    /// <summary>
    /// http(s) 链接只接受 B站网站域名(<see cref="BBDownTUtil.IsBilibiliHost"/>)和 b23.tv、bili.im 短链；BV号等非链接输入交给 GetAvIdAsync 判断
    /// </summary>
    internal static bool IsSupportedInput(string? input)
    {
        var value = input?.Trim() ?? "";
        if (!value.StartsWith("http", StringComparison.OrdinalIgnoreCase)) return true;
        return BBDownTUtil.IsShortLinkUri(value)
            || (Uri.TryCreate(value, UriKind.Absolute, out var uri)
                && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
                && BBDownTUtil.IsBilibiliHost(uri.Host));
    }

    /// <summary>
    /// 列表类链接：对应的Fetcher会逐页拉取整个列表(可能几十上百个请求，且不能取消)，解析预览不处理
    /// </summary>
    internal static string? ListLinkName(string aidOri) =>
        aidOri.StartsWith("mid", StringComparison.Ordinal) ? "UP主空间"
        : aidOri.StartsWith("favId", StringComparison.Ordinal) ? "收藏夹"
        : aidOri.StartsWith("listBizId", StringComparison.Ordinal) ? "合集"
        : aidOri.StartsWith("seriesBizId", StringComparison.Ordinal) ? "系列"
        : null;

    internal static string ListLinkMessage(string listName) =>
        $"{listName}链接是视频列表，解析预览不会逐页读取整个列表。请直接点「下载」添加任务"
        + (listName == "UP主空间"
            ? "（默认只导出投稿清单 TXT，勾选「高级选项 → UP主空间链接：下载全部投稿」才会逐个下载）"
            : "（按「画质」等设置下载列表里的每个视频）")
        + "；想先看画质，请粘贴其中单个视频的链接再解析。";

    /// <summary>
    /// 拿到分P数后检查分P参数：与下载任务的规则相同，出错时给出分P数和写法示例
    /// </summary>
    internal static void ValidateSelectPage(MyOption option, VInfo info)
    {
        if (string.IsNullOrWhiteSpace(option.SelectPage)) return;
        try
        {
            PageSelectionParser.Parse(option.SelectPage, info.PagesInfo.Count);
        }
        catch (ArgumentException)
        {
            throw new ArgumentException(PageSelectionParser.DescribeInvalidSelection(option.SelectPage.Trim(), info.PagesInfo.Count));
        }
    }

    private static async Task<ParsedResult> FetchTracksAsync(MyOption option, string aidOri, Page page, string firstEncoding)
    {
        Task<ParsedResult> Fetch(string? language) => Parser.ExtractTracksAsync(aidOri, page.aid, page.cid, page.epid,
            option.UseTvApi, option.UseIntlApi, option.UseAppApi, firstEncoding, "0", language);
        var language = AudioLanguageSelection.Normalize(option.AudioLanguage);
        return language is null ? await Fetch(null) : await AudioLanguageSelection.FetchAsync(language, Fetch);
    }

    private static string NormalizeToken(string? token) =>
        string.IsNullOrWhiteSpace(token) ? "" : token.Trim().Replace("access_token=", "");

    /// <summary>
    /// 列流使用的分P：显式Page > 下载时默认/指定的第一个分P > P1
    /// </summary>
    internal static Page ChooseStreamsPage(VInfo info, int? requestedPage, List<string>? selectedPages)
    {
        if (info.PagesInfo.Count == 0) throw new InvalidDataException("没有获取到分P信息");
        if (requestedPage is { } wanted)
        {
            return info.PagesInfo.FirstOrDefault(p => p.index == wanted)
                ?? throw new ArgumentException($"分P {wanted} 不存在，共 {info.PagesInfo.Count} 个分P");
        }
        var first = selectedPages?.FirstOrDefault();
        return (first is null ? null : info.PagesInfo.FirstOrDefault(p => p.index.ToString() == first))
            ?? info.PagesInfo[0];
    }

    internal static ParseResponse Build(MyOption option, VInfo info, string aid, Page page, ParsedResult tracks,
        Dictionary<string, int> dfnPriority, Dictionary<string, byte> encodingPriority,
        List<Subtitle> subtitles, ParseAccount account, string cookie, string requestedApi, string apiType, long elapsedMs,
        bool accountKnown = true, List<int>? downloadPages = null)
    {
        var progressive = tracks.Clips.Count > 0;
        var duration = page.dur;

        // 任务会选择的流：与 DownloadPageAsync 相同的排序 + 精确指定
        var sortedVideos = Program.SortTracks([.. tracks.VideoTracks], dfnPriority, encodingPriority, option.VideoAscending, option.EncodingPriorityFirst);
        var sortedAudios = Program.SortTracks([.. tracks.AudioTracks], encodingPriority, option.AudioAscending);
        var preview = new ParsedResult { VideoTracks = sortedVideos, AudioTracks = sortedAudios };
        StreamPinSelection.Apply(option, preview);
        var defaultVideo = option.AudioOnly && !option.VideoOnly ? null : preview.VideoTracks.FirstOrDefault();
        var defaultAudio = option.VideoOnly && !option.AudioOnly ? null : preview.AudioTracks.FirstOrDefault();

        var videos = tracks.VideoTracks
            .OrderByDescending(v => int.TryParse(v.id, out var id) ? id : 0)
            .ThenBy(v => CodecOrder(v.codecs))
            .ThenByDescending(v => v.bandwith)
            .Select(v => ToVideo(v, duration, progressive))
            .GroupBy(v => v.Key).Select(g => g.First())
            .ToList();
        var audios = tracks.AudioTracks
            .OrderBy(a => AudioKind(a.id) == "normal" ? 1 : 0)
            .ThenByDescending(a => a.bandwith)
            .ThenByDescending(a => StreamPinSelection.AudioNominalRank(a.id))
            .Select(a => ToAudio(a, duration))
            .GroupBy(a => a.Id).Select(g => g.First())
            .ToList();

        var qualities = ReadAcceptQualities(tracks.WebJsonString, videos);
        var steinGateFallback = requestedApi == "TV" && apiType != "TV";
        var hints = BuildHints(option, info, account, cookie, apiType, steinGateFallback, tracks, videos, qualities, accountKnown);

        var first = info.PagesInfo[0];
        return new ParseResponse(
            option.Url, aid, string.IsNullOrEmpty(first.aid) || !long.TryParse(first.aid, out _) ? null : first.bvid,
            info.Title, string.IsNullOrEmpty(info.Pic) ? first.cover : info.Pic, info.PubTime,
            first.ownerName, Kind(aid, info), apiType, requestedApi, account, hints,
            info.PagesInfo.Count,
            info.PagesInfo.Take(MaxListedPages)
                .Select(p => new ParsePage(p.index, p.title, p.dur, p.aid, p.cid, p.index == page.index)).ToList(),
            page.index, tracks.IsPreviewOnly, progressive, videos, audios,
            defaultVideo is null ? null : StreamPinSelection.VideoKey(defaultVideo),
            defaultAudio?.id,
            qualities,
            tracks.AudioLanguages.Select(l => new ParseAudioLanguage(l.Code, l.Title, l.IsAi)).ToList(),
            subtitles.Select(s => new ParseSubtitle(s.lan, s.lanDoc ?? s.lan, s.IsAi)).ToList(),
            elapsedMs,
            downloadPages);
    }

    internal static List<ParseHint> BuildHints(MyOption option, VInfo info, ParseAccount account, string cookie, string apiType,
        bool steinGateFallback, ParsedResult tracks, List<ParseVideoStream> videos, List<ParseQuality> qualities,
        bool accountKnown = true)
    {
        var hints = new List<ParseHint>();
        if (GuestApiHint(option, account.ApiAuthenticated) is { } tokenWarning)
            hints.Add(new("warn", option.UseTvApi ? "tv-no-token" : "app-no-token", tokenWarning));
        if (!accountKnown)
            hints.Add(new("info", "account-unknown", "没能查到B站登录状态（网络或接口异常），下面关于登录和大会员的判断可能不准确。"));
        if (apiType == "WEB" && accountKnown)
        {
            if (!account.WebLoggedIn && string.IsNullOrEmpty(cookie))
                hints.Add(new("warn", "not-logged-in", "未登录B站账号：WEB 接口只能获取较低画质，点右上角「登录B站」扫码后可获取 1080P 及以上画质。"));
            else if (!account.WebLoggedIn)
                hints.Add(new("warn", "cookie-invalid", "保存的B站登录状态已失效。下载时会自动尝试刷新，若仍失败再扫码登录。"));
            var vipOnly = qualities.Where(q => q.NeedVip && !q.Available).Select(q => q.Label).ToList();
            if (account.WebLoggedIn && !account.IsVip && vipOnly.Count > 0)
                hints.Add(new("info", "need-vip", $"该视频还有 {string.Join("、", vipOnly)}，需要大会员才能获取。"));
        }
        if (apiType == "APP" && account.ApiAuthenticated && videos.Count > 0)
        {
            var codecs = string.Join("/", videos.Select(v => v.Codec).Distinct());
            hints.Add(new("info", "app-one-codec", $"APP 接口每次只返回一种编码（当前 {codecs}），可在「高级选项 → 视频编码优先」里切换后重新解析。"));
        }
        if (steinGateFallback)
            hints.Add(new("info", "steingate-web", "互动视频不支持 TV 接口，已改用 WEB 接口解析。"));
        if (tracks.IsPreviewOnly)
            hints.Add(new("warn", "preview-only", "当前接口只返回试看片段，下载会被拒绝；请检查登录状态和会员权限。"));
        if (tracks.Clips.Count > 0)
            hints.Add(new("info", "progressive", "该接口返回的是音视频合并流（FLV/MP4 分段），只能按最高可用画质下载，无法单独选择音视频流。"));
        if (videos.Count > 0 && videos.All(v => int.TryParse(v.Id, out var id) && id <= 32) && hints.All(h => h.Level != "warn"))
            hints.Add(new("warn", "low-quality", "只获取到 480P 及以下的画质，可能是接口受限；可尝试切换到 WEB 接口或重新登录后再解析。"));
        if (info.PagesInfo.Count > 1)
            hints.Add(new("info", "streams-one-page", "画质列表来自当前分P；其他分P若没有所选的流，会按画质/编码优先级自动回退。"));
        return hints;
    }

    /// <summary>
    /// 网页上显示的 APP/TV 游客提示。与命令行日志(Program.MissingApiTokenWarning)的内容相同，
    /// 但不提命令行操作(logintv)：网页和App里没有对应的入口
    /// </summary>
    internal static string? GuestApiHint(MyOption option, bool authenticated)
    {
        if (authenticated || !(option.UseAppApi || option.UseTvApi)) return null;
        return option.UseTvApi
            ? "TV 接口没有登录凭证（网页扫码登录对它无效），按未登录身份解析：拿不到 1080P 及以上画质（未登录通常最高 720P）。需要高画质请改用 WEB 接口。"
            : "APP 接口没有登录凭证（网页扫码登录对它无效），按未登录身份解析：通常最高 480P，且一次只返回一种编码。需要高画质请改用 WEB 接口。";
    }

    /// <summary>
    /// 从播放接口响应读取该视频声明的画质档位(support_formats/accept_quality)，用于说明“为什么没有4K”
    /// </summary>
    internal static List<ParseQuality> ReadAcceptQualities(string webJson, List<ParseVideoStream> videos)
    {
        var available = videos.Select(v => v.Id).ToHashSet();
        var result = new List<ParseQuality>();
        if (string.IsNullOrWhiteSpace(webJson)) return result;
        try
        {
            using var document = JsonDocument.Parse(webJson);
            var root = Parser.SelectResponseRoot(document.RootElement);
            if (root.TryGetProperty("support_formats", out var formats) && formats.ValueKind == JsonValueKind.Array)
            {
                foreach (var f in formats.EnumerateArray())
                {
                    if (!f.TryGetProperty("quality", out var q)) continue;
                    var id = q.ToString();
                    var label = Config.qualitys.TryGetValue(id, out var known) ? known
                        : f.TryGetProperty("new_description", out var d) ? d.GetString() ?? id : id;
                    result.Add(new ParseQuality(id, label, available.Contains(id),
                        f.TryGetProperty("need_login", out var nl) && nl.ValueKind == JsonValueKind.True,
                        f.TryGetProperty("need_vip", out var nv) && nv.ValueKind == JsonValueKind.True));
                }
            }
            else if (root.TryGetProperty("accept_quality", out var accept) && accept.ValueKind == JsonValueKind.Array)
            {
                foreach (var q in accept.EnumerateArray())
                {
                    var id = q.ToString();
                    result.Add(new ParseQuality(id, Config.qualitys.GetValueOrDefault(id, id), available.Contains(id), false, false));
                }
            }
        }
        catch (JsonException) { }
        return result;
    }

    private static ParseVideoStream ToVideo(Video v, int pageDur, bool progressive)
    {
        var dur = pageDur == 0 ? v.dur : pageDur;
        var exact = v.size > 0;
        var size = exact ? (long)v.size : EstimateSize(dur, v.bandwith);
        var kbps = v.bandwith > 0 ? v.bandwith : (progressive && v.dur > 0 ? (long)(v.size / 1024 / v.dur * 8) : 0);
        return new ParseVideoStream(StreamPinSelection.VideoKey(v), v.id, v.dfn, v.codecs, v.res, v.fps, kbps, size, !exact);
    }

    private static ParseAudioStream ToAudio(Audio a, int pageDur)
    {
        var dur = pageDur == 0 ? a.dur : pageDur;
        return new ParseAudioStream(a.id, StreamPinSelection.AudioLabel(a.id) ?? $"{a.bandwith} kbps", a.codecs,
            a.bandwith, EstimateSize(dur, a.bandwith), AudioKind(a.id));
    }

    /// <summary>
    /// 与命令行 -info 的估算一致：时长 × 码率(kbps) × 1024 / 8
    /// </summary>
    internal static long EstimateSize(int durationSeconds, long kbps) => (long)durationSeconds * kbps * 1024 / 8;

    private static int CodecOrder(string codec) => codec switch { "AVC" => 0, "HEVC" => 1, "AV1" => 2, _ => 3 };

    internal static string AudioKind(string id) => id switch { "30250" => "dolby", "30251" => "hires", _ => "normal" };

    private static string Kind(string aid, VInfo info) =>
        info.IsCheese ? "cheese" : info.IsBangumi ? "bangumi" : info.PagesInfo.Select(p => p.aid).Distinct().Count() > 1 ? "list" : "video";
}

/// <summary>
/// 输入合法但不适合解析预览(如UP主空间链接)，返回422
/// </summary>
internal sealed class ParseRejectedException(string message) : Exception(message);
