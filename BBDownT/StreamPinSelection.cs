using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using BBDownT.Core.Entity;
using static BBDownT.Core.Entity.Entity;

namespace BBDownT;

/// <summary>
/// 按解析结果精确指定音视频流(VideoStream/AudioStream)。
/// 只调整已排序列表的首位：找不到时保持画质/编码优先级的排序结果，并返回提示。
/// </summary>
internal static partial class StreamPinSelection
{
    internal static string? ValidateVideo(string? value) =>
        string.IsNullOrWhiteSpace(value) || VideoStreamRegex().IsMatch(value.Trim())
            ? null
            : "VideoStream（--video-stream）格式应为 画质代码[:编码]，如 120:HEVC、80:AVC、64";

    internal static string? ValidateAudio(string? value) =>
        string.IsNullOrWhiteSpace(value) || AudioStreamRegex().IsMatch(value.Trim())
            ? null
            : "AudioStream（--audio-stream）应为音频流ID数字，如 30280";

    internal static string? ValidateOptions(MyOption option) =>
        ValidateVideo(option.VideoStream) ?? ValidateAudio(option.AudioStream);

    /// <summary>
    /// 视频流在界面与请求中的标识：画质代码:编码，如 "120:HEVC"；
    /// 认不出编码(UNKNOWN)时只用画质代码，保证这个值总能通过 VideoStream 的格式校验
    /// </summary>
    internal static string VideoKey(Video video)
    {
        var codec = video.codecs?.ToUpperInvariant();
        return codec is "AVC" or "HEVC" or "AV1" ? $"{video.id}:{codec}" : video.id;
    }

    internal static (string Qn, string? Codec)? ParseVideo(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || ValidateVideo(value) is not null) return null;
        var parts = value.Trim().Split(':', 2);
        return (parts[0], parts.Length > 1 ? parts[1].ToUpperInvariant() : null);
    }

    /// <summary>
    /// 把指定的流移动到列表首位，返回未能满足的指定项提示(全部满足或未指定时为空)
    /// </summary>
    internal static List<string> Apply(MyOption option, ParsedResult parsedResult)
    {
        var misses = new List<string>();
        if (ParseVideo(option.VideoStream) is { } wanted && parsedResult.VideoTracks.Count > 0)
        {
            var index = parsedResult.VideoTracks.FindIndex(v =>
                v.id == wanted.Qn && (wanted.Codec is null || string.Equals(v.codecs, wanted.Codec, StringComparison.OrdinalIgnoreCase)));
            if (index >= 0) MoveToFront(parsedResult.VideoTracks, index);
            else misses.Add($"没有找到指定的视频流 {option.VideoStream!.Trim()}，已按画质/编码优先级选择 {VideoKey(parsedResult.VideoTracks[0])}");
        }

        var audioId = option.AudioStream?.Trim();
        if (!string.IsNullOrEmpty(audioId) && parsedResult.AudioTracks.Count > 0)
        {
            var index = parsedResult.AudioTracks.FindIndex(a => a.id == audioId);
            if (index >= 0)
            {
                MoveToFront(parsedResult.AudioTracks, index);
                // 背景音与配音按同一序号下载，保持与主音轨一致
                if (parsedResult.BackgroundAudioTracks.Count > 0)
                {
                    var bg = parsedResult.BackgroundAudioTracks.FindIndex(a => a.id == audioId);
                    if (bg >= 0) MoveToFront(parsedResult.BackgroundAudioTracks, bg);
                }
            }
            else misses.Add($"没有找到指定的音频流 {audioId}，已选择 {parsedResult.AudioTracks[0].id}");
        }
        return misses;
    }

    private static void MoveToFront<T>(List<T> list, int index)
    {
        if (index <= 0) return;
        var item = list[index];
        list.RemoveAt(index);
        list.Insert(0, item);
    }

    /// <summary>
    /// B站常见音频流ID对应的名称；未知ID返回null
    /// </summary>
    internal static string? AudioLabel(string id) => id switch
    {
        "30216" => "64K",
        "30232" => "132K",
        "30280" => "192K",
        "30250" => "杜比全景声",
        "30251" => "Hi-Res无损",
        _ => null
    };

    /// <summary>
    /// 普通音轨的标称档位(192K > 132K > 64K)，码率相同时用来排序；其他ID为0
    /// </summary>
    internal static int AudioNominalRank(string id) => id switch
    {
        "30280" => 3,
        "30232" => 2,
        "30216" => 1,
        _ => 0
    };

    [GeneratedRegex(@"^\d{1,4}(:(AVC|HEVC|AV1))?$", RegexOptions.IgnoreCase)]
    private static partial Regex VideoStreamRegex();

    [GeneratedRegex(@"^\d{1,8}$")]
    private static partial Regex AudioStreamRegex();
}
