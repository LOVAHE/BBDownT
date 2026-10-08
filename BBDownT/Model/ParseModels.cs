using System.Collections.Generic;

namespace BBDownT;

/// <summary>
/// POST /parse 的请求：与 add-task 相同的选项，另加要列出音视频流的分P序号(从1开始)
/// </summary>
internal class ParseRequest : ServeRequestOptions
{
    public int? Page { get; set; }
}

/// <summary>
/// 解析预览结果。Videos/Audios 来自 StreamsPage 这一个分P；DefaultVideo/DefaultAudio 是不指定流时任务会选择的流；
/// DownloadPages 是不指定分P时任务会下载的分P(null 表示全部)
/// </summary>
public sealed record ParseResponse(
    string Url,
    string Aid,
    string? Bvid,
    string Title,
    string? Pic,
    long PubTime,
    string? OwnerName,
    string Kind,
    string Api,
    string RequestedApi,
    ParseAccount Account,
    List<ParseHint> Hints,
    int PageCount,
    List<ParsePage> Pages,
    int StreamsPage,
    bool IsPreviewOnly,
    bool Progressive,
    List<ParseVideoStream> Videos,
    List<ParseAudioStream> Audios,
    string? DefaultVideo,
    string? DefaultAudio,
    List<ParseQuality> AcceptQualities,
    List<ParseAudioLanguage> AudioLanguages,
    List<ParseSubtitle> Subtitles,
    long ElapsedMs,
    List<int>? DownloadPages);

/// <summary>
/// 登录状态：WEB接口使用网页扫码得到的Cookie，TV/APP接口各自使用access_token
/// </summary>
public sealed record ParseAccount(
    bool WebLoggedIn,
    string? WebUserName,
    bool IsVip,
    string? VipLabel,
    bool TvTokenSaved,
    bool AppTokenSaved,
    bool ApiAuthenticated);

/// <summary>
/// Level: info/warn；Code 供前端决定是否显示“改用WEB重新解析”等操作
/// </summary>
public sealed record ParseHint(string Level, string Code, string Message);

public sealed record ParsePage(int Index, string Title, int Duration, string Aid, string Cid, bool Selected);

/// <summary>
/// Key = 画质代码:编码(如 120:HEVC)，下载时作为 VideoStream 提交
/// </summary>
public sealed record ParseVideoStream(
    string Key,
    string Id,
    string Quality,
    string Codec,
    string? Resolution,
    string? Fps,
    long BandwidthKbps,
    long Size,
    bool SizeIsEstimate);

/// <summary>
/// Id 下载时作为 AudioStream 提交；Kind: normal/dolby/hires
/// </summary>
public sealed record ParseAudioStream(
    string Id,
    string Label,
    string Codec,
    long BandwidthKbps,
    long Size,
    string Kind);

/// <summary>
/// 播放接口声明的该视频画质档位；NeedLogin/NeedVip 来自 support_formats(仅WEB接口提供)
/// </summary>
public sealed record ParseQuality(string Id, string Label, bool Available, bool NeedLogin, bool NeedVip);

public sealed record ParseAudioLanguage(string Code, string Title, bool IsAi);

public sealed record ParseSubtitle(string Lan, string Title, bool IsAi);
