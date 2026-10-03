using System.Text.Json;
using BBDownT.Core.Entity;
using BBDownT.Core.Util;
using static BBDownT.Core.Entity.Entity;

namespace BBDownT.Core;

internal static class PlayResponseMapper
{
    // Current BiliBili WEB and yt-dlp's BiliIntl extractor both consume
    // playurl.video[].video_resource and playurl.audio_resource[].
    internal static void MapIntlWeb(JsonElement playurl, ParsedResult result, Func<string, bool> isExcludedUrl)
    {
        var duration = playurl.TryGetProperty("duration", out var time) ? (int)(time.GetDouble() / 1000) : 0;
        if (playurl.TryGetProperty("video", out var streams) && streams.ValueKind == JsonValueKind.Array)
        {
            foreach (var stream in streams.EnumerateArray())
            {
                if (!stream.TryGetProperty("video_resource", out var resource) || resource.ValueKind != JsonValueKind.Object
                    || ReadText(resource, "url").Length == 0) continue;
                var quality = ReadText(resource, "quality");
                var description = stream.TryGetProperty("stream_info", out var info) && info.ValueKind == JsonValueKind.Object
                    ? ReadText(info, "desc_words") : "";
                if (quality.Length == 0 && info.ValueKind == JsonValueKind.Object) quality = ReadText(info, "quality");
                if (description.Length == 0) description = Config.qualitys.GetValueOrDefault(quality, quality);
                var video = new Video
                {
                    id = quality,
                    dfn = description,
                    dur = duration,
                    baseUrl = SelectPreferredUrl(resource, isExcludedUrl, "url"),
                    bandwith = (long)(ReadNumber(resource, "bandwidth") / 1000),
                    codecs = ReadIntlVideoCodec(resource),
                    size = ReadNumber(resource, "size"),
                    res = resource.TryGetProperty("width", out var width) && resource.TryGetProperty("height", out var height)
                        ? $"{width}x{height}" : "",
                    fps = ReadText(resource, "frame_rate")
                };
                if (!result.VideoTracks.Contains(video)) result.VideoTracks.Add(video);
            }
        }
        if (playurl.TryGetProperty("audio_resource", out var audios) && audios.ValueKind == JsonValueKind.Array)
        {
            foreach (var resource in audios.EnumerateArray())
            {
                if (resource.ValueKind != JsonValueKind.Object || ReadText(resource, "url").Length == 0) continue;
                var id = ReadText(resource, "quality");
                if (id.Length == 0) id = ReadText(resource, "id");
                var codec = ReadText(resource, "codecs");
                var audio = new Audio
                {
                    id = id,
                    dfn = id,
                    dur = duration,
                    baseUrl = SelectPreferredUrl(resource, isExcludedUrl, "url"),
                    bandwith = (long)(ReadNumber(resource, "bandwidth") / 1000),
                    codecs = codec switch { "mp4a.40.2" or "mp4a.40.5" => "M4A", "ec-3" => "E-AC-3", "fLaC" => "FLAC", _ => codec }
                };
                if (!result.AudioTracks.Contains(audio)) result.AudioTracks.Add(audio);
            }
        }
    }

    private static string ReadText(JsonElement node, string property)
        => node.TryGetProperty(property, out var value) && value.ValueKind != JsonValueKind.Null ? value.ToString() : "";

    private static double ReadNumber(JsonElement node, string property)
        => node.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetDouble() : 0;

    private static string ReadIntlVideoCodec(JsonElement resource)
    {
        var codec = GetVideoCodec(ReadText(resource, "codec_id"));
        if (codec != "UNKNOWN") return codec;
        var name = ReadText(resource, "codecs");
        if (name.StartsWith("avc", StringComparison.OrdinalIgnoreCase)) return "AVC";
        if (name.StartsWith("hev1", StringComparison.OrdinalIgnoreCase) || name.StartsWith("hvc1", StringComparison.OrdinalIgnoreCase)) return "HEVC";
        if (name.StartsWith("av01", StringComparison.OrdinalIgnoreCase)) return "AV1";
        return "UNKNOWN";
    }

    internal static void MapIntl(
        JsonElement documentRoot,
        ParsedResult parsedResult,
        Func<string, bool> isExcludedUrl)
    {
        var videoInfo = documentRoot.GetProperty("data").GetProperty("video_info");
        var duration = videoInfo.GetProperty("timelength").GetInt32() / 1000;
        foreach (var stream in videoInfo.GetProperty("stream_list").EnumerateArray())
        {
            if (!stream.TryGetProperty("dash_video", out var dashVideo)
                || string.IsNullOrEmpty(dashVideo.GetProperty("base_url").ToString()))
            {
                continue;
            }
            var videoId = stream.GetProperty("stream_info").GetProperty("quality").ToString();
            var video = new Video
            {
                dur = duration,
                id = videoId,
                dfn = Config.qualitys[videoId],
                bandwith = Convert.ToInt64(dashVideo.GetProperty("bandwidth").ToString()) / 1000,
                baseUrl = SelectPreferredUrl(dashVideo, isExcludedUrl),
                codecs = GetVideoCodec(dashVideo.GetProperty("codecid").ToString()),
                size = dashVideo.TryGetProperty("size", out var sizeNode)
                    ? Convert.ToDouble(sizeNode.ToString())
                    : 0
            };
            if (!parsedResult.VideoTracks.Contains(video))
            {
                parsedResult.VideoTracks.Add(video);
            }
        }

        foreach (var node in videoInfo.GetProperty("dash_audio").EnumerateArray())
        {
            var audioId = node.GetProperty("id").ToString();
            var audio = new Audio
            {
                id = audioId,
                dfn = audioId,
                dur = duration,
                bandwith = Convert.ToInt64(node.GetProperty("bandwidth").ToString()) / 1000,
                baseUrl = SelectPreferredUrl(node, isExcludedUrl),
                codecs = "M4A"
            };
            if (!parsedResult.AudioTracks.Contains(audio))
            {
                parsedResult.AudioTracks.Add(audio);
            }
        }
    }

    internal static int GetDurationSeconds(JsonElement root)
    {
        var duration = 0;
        if (TryGetDash(root, out var dash)
            && dash.TryGetProperty("duration", out var dashDuration)
            && dashDuration.TryGetInt32(out var durationSeconds))
        {
            duration = durationSeconds;
        }
        if (root.TryGetProperty("timelength", out var timeLength)
            && timeLength.TryGetInt32(out var durationMilliseconds))
        {
            duration = durationMilliseconds / 1000;
        }
        return duration;
    }

    internal static void MapDashVideos(
        JsonElement root,
        ParsedResult parsedResult,
        int duration,
        bool tvApi,
        bool appApi,
        Func<string, bool> isExcludedUrl)
    {
        if (!TryGetDash(root, out var dash)
            || !dash.TryGetProperty("video", out var video)
            || video.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (var node in video.EnumerateArray())
        {
            var videoId = node.GetProperty("id").ToString();
            var mappedVideo = new Video
            {
                dur = duration,
                id = videoId,
                dfn = Config.qualitys[videoId],
                bandwith = Convert.ToInt64(node.GetProperty("bandwidth").ToString()) / 1000,
                baseUrl = SelectPreferredUrl(node, isExcludedUrl),
                codecs = GetVideoCodec(node.GetProperty("codecid").ToString()),
                size = node.TryGetProperty("size", out var sizeNode)
                    ? Convert.ToDouble(sizeNode.ToString())
                    : 0
            };
            if (!tvApi && !appApi)
            {
                mappedVideo.res = node.GetProperty("width") + "x" + node.GetProperty("height");
                mappedVideo.fps = node.GetProperty("frame_rate").ToString();
            }
            if (!parsedResult.VideoTracks.Contains(mappedVideo))
            {
                parsedResult.VideoTracks.Add(mappedVideo);
            }
        }
    }

    internal static void MapDashAudioAndDubbing(
        JsonElement documentRoot,
        JsonElement root,
        ParsedResult parsedResult,
        int duration,
        string aid,
        string cid,
        bool tvApi,
        bool appApi,
        bool bangumi,
        Func<string, bool> isExcludedUrl)
    {
        if (!TryGetDash(root, out var dash))
        {
            return;
        }
        var audio = new List<JsonElement>();
        var hasRegularAudio = dash.TryGetProperty("audio", out var regularAudio)
            && regularAudio.ValueKind == JsonValueKind.Array;
        if (hasRegularAudio)
        {
            audio.AddRange(regularAudio.EnumerateArray());
        }
        if (hasRegularAudio
            && !tvApi
            && dash.TryGetProperty("dolby", out var dolby)
            && dolby.ValueKind == JsonValueKind.Object
            && dolby.TryGetProperty("audio", out var dolbyAudio)
            && dolbyAudio.ValueKind == JsonValueKind.Array)
        {
            audio.AddRange(dolbyAudio.EnumerateArray());
        }
        if (hasRegularAudio
            && !tvApi
            && dash.TryGetProperty("flac", out var flac)
            && flac.ValueKind == JsonValueKind.Object
            && flac.TryGetProperty("audio", out var flacAudio)
            && flacAudio.ValueKind == JsonValueKind.Object)
        {
            audio.Add(flacAudio);
        }

        foreach (var node in audio)
        {
            parsedResult.AudioTracks.Add(MapAudio(node, duration, isExcludedUrl, normalizeCodec: true));
        }

        if (!appApi
            || !bangumi
            || !documentRoot.TryGetProperty("dubbing_info", out var dubbingInfo)
            || !dubbingInfo.TryGetProperty("background_audio", out var backgroundAudio)
            || backgroundAudio.ValueKind != JsonValueKind.Array
            || !dubbingInfo.TryGetProperty("role_audio_list", out var roleAudio)
            || roleAudio.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (var node in backgroundAudio.EnumerateArray())
        {
            parsedResult.BackgroundAudioTracks.Add(
                MapAudio(node, duration, isExcludedUrl, normalizeCodec: false));
        }
        foreach (var role in roleAudio.EnumerateArray())
        {
            var roleAudioTracks = role.GetProperty("audio")
                .EnumerateArray()
                .Select(node => MapAudio(node, duration, isExcludedUrl, normalizeCodec: false))
                .ToList();
            parsedResult.RoleAudioList.Add(new AudioMaterialInfo
            {
                title = role.GetProperty("title").ToString(),
                personName = role.GetProperty("person_name").ToString(),
                path = $"{PathSegmentSanitizer.Sanitize(aid)}/{PathSegmentSanitizer.Sanitize(aid)}.{PathSegmentSanitizer.Sanitize(cid)}.{PathSegmentSanitizer.Sanitize(role.GetProperty("audio_id").ToString())}.m4a",
                audio = roleAudioTracks
            });
        }
    }

    internal static void MapDurl(JsonElement root, ParsedResult parsedResult)
    {
        var quality = root.GetProperty("quality").ToString();
        var videoCodecid = root.GetProperty("video_codecid").ToString();
        double size = 0;
        double length = 0;

        foreach (var node in root.GetProperty("durl").EnumerateArray())
        {
            parsedResult.Clips.Add(node.GetProperty("url").ToString());
            size += node.GetProperty("size").GetDouble();
            length += node.GetProperty("length").GetDouble();
        }

        if (root.TryGetProperty("qn_extras", out var qnExtras))
        {
            parsedResult.Dfns.AddRange(qnExtras.EnumerateArray()
                .Select(node => node.GetProperty("qn").ToString()));
        }
        else if (root.TryGetProperty("accept_quality", out var acceptQuality))
        {
            parsedResult.Dfns.AddRange(acceptQuality.EnumerateArray()
                .Select(node => node.ToString())
                .Where(qn => !string.IsNullOrEmpty(qn)));
        }

        var video = new Video
        {
            id = quality,
            dfn = Config.qualitys[quality],
            baseUrl = string.Empty,
            codecs = GetVideoCodec(videoCodecid),
            dur = (int)length / 1000,
            size = size
        };
        if (!parsedResult.VideoTracks.Contains(video))
        {
            parsedResult.VideoTracks.Add(video);
        }
    }

    internal static void MapClipInfo(JsonElement root, ParsedResult parsedResult)
    {
        if (!root.TryGetProperty("clip_info_list", out var clipList)
            || clipList.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        parsedResult.ExtraPoints.AddRange(clipList.EnumerateArray().Select(clip => new ViewPoint
        {
            title = clip.GetProperty("toastText").ToString().Replace("即将跳过", ""),
            start = clip.GetProperty("start").GetInt32(),
            end = clip.GetProperty("end").GetInt32()
        }));
        parsedResult.ExtraPoints.Sort((left, right) => left.start.CompareTo(right.start));

        var pointsWithMainSegments = new List<ViewPoint>();
        var lastEnd = 0;
        foreach (var point in parsedResult.ExtraPoints)
        {
            if (lastEnd < point.start)
            {
                pointsWithMainSegments.Add(new ViewPoint
                {
                    title = "正片",
                    start = lastEnd,
                    end = point.start
                });
            }
            pointsWithMainSegments.Add(point);
            lastEnd = point.end;
        }
        parsedResult.ExtraPoints = pointsWithMainSegments;
    }

    internal static string GetVideoCodec(string code)
    {
        return code switch
        {
            "13" => "AV1",
            "12" => "HEVC",
            "7" => "AVC",
            _ => "UNKNOWN"
        };
    }

    internal static bool HasDashAudio(JsonElement root)
    {
        return TryGetDash(root, out var dash)
            && dash.TryGetProperty("audio", out var audio)
            && audio.ValueKind == JsonValueKind.Array;
    }

    private static Audio MapAudio(
        JsonElement node,
        int duration,
        Func<string, bool> isExcludedUrl,
        bool normalizeCodec)
    {
        var audioId = node.GetProperty("id").ToString();
        var codec = node.GetProperty("codecs").ToString();
        if (normalizeCodec)
        {
            codec = codec switch
            {
                "mp4a.40.2" or "mp4a.40.5" => "M4A",
                "ec-3" => "E-AC-3",
                "fLaC" => "FLAC",
                _ => codec
            };
        }
        return new Audio
        {
            id = audioId,
            dfn = audioId,
            dur = duration,
            bandwith = Convert.ToInt64(node.GetProperty("bandwidth").ToString()) / 1000,
            baseUrl = SelectPreferredUrl(node, isExcludedUrl),
            codecs = codec
        };
    }

    private static string SelectPreferredUrl(JsonElement node, Func<string, bool> isExcludedUrl, string urlProperty = "base_url")
    {
        var urls = new List<string> { node.GetProperty(urlProperty).ToString() };
        if (node.TryGetProperty("backup_url", out var backupUrl)
            && backupUrl.ValueKind == JsonValueKind.Array)
        {
            urls.AddRange(backupUrl.EnumerateArray().Select(item => item.ToString()));
        }
        return urls.FirstOrDefault(url => !isExcludedUrl(url), urls[0]);
    }

    private static bool TryGetDash(JsonElement root, out JsonElement dash)
    {
        dash = default;
        return root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty("dash", out dash)
            && dash.ValueKind == JsonValueKind.Object;
    }
}
