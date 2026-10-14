using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using static BBDownT.Core.Entity.Entity;
using static BBDownT.BBDownTUtil;
using static BBDownT.Core.Logger;
using System.Linq;
using System.Text.RegularExpressions;
using BBDownT.Core;
using BBDownT.Core.Entity;
using BBDownT.Core.Util;
using static BBDownT.BBDownTDownloadUtil;

namespace BBDownT;

internal partial class Program
{
    /// <summary>
    /// 兼容旧版本命令行参数并给出警告
    /// </summary>
    /// <param name="myOption"></param>
    internal static void HandleDeprecatedOptions(MyOption myOption)
    {
        if (myOption.AddDfnSubfix)
        {
            LogWarn("--add-dfn-subfix 已被弃用, 建议使用 --file-pattern/-F 或 --multi-file-pattern/-M 来自定义输出文件名格式");
            if (string.IsNullOrEmpty(myOption.FilePattern) && string.IsNullOrEmpty(myOption.MultiFilePattern))
            {
                SinglePageDefaultSavePath += "[<dfn>]";
                MultiPageDefaultSavePath += "[<dfn>]";
                LogWarn($"已切换至 -F \"{SinglePageDefaultSavePath}\" -M \"{MultiPageDefaultSavePath}\"");
            }
        }
        if (myOption.Aria2cProxy != "")
        {
            LogWarn("--aria2c-proxy 已被弃用, 请使用 --aria2c-args 来设置aria2c代理, 本次执行已添加该代理");
            myOption.Aria2cArgs += $" --all-proxy=\"{myOption.Aria2cProxy}\"";
        }
        if (myOption.OnlyHevc)
        {
            LogWarn("--only-hevc/-hevc 已被弃用, 请使用 --encoding-priority 来设置编码优先级, 本次执行已将hevc设置为最高优先级");
            myOption.EncodingPriority = "hevc";
        }
        if (myOption.OnlyAvc)
        {
            LogWarn("--only-avc/-avc 已被弃用, 请使用 --encoding-priority 来设置编码优先级, 本次执行已将avc设置为最高优先级");
            myOption.EncodingPriority = "avc";
        }
        if (myOption.OnlyAv1)
        {
            LogWarn("--only-av1/-av1 已被弃用, 请使用 --encoding-priority 来设置编码优先级, 本次执行已将av1设置为最高优先级");
            myOption.EncodingPriority = "av1";
        }
        if (myOption.NoPaddingPageNum)
        {
            LogWarn("--no-padding-page-num 已被弃用, 建议使用 --file-pattern/-F 或 --multi-file-pattern/-M 来自定义输出文件名格式");
            if (string.IsNullOrEmpty(myOption.FilePattern) && string.IsNullOrEmpty(myOption.MultiFilePattern))
            {
                MultiPageDefaultSavePath = MultiPageDefaultSavePath
                    .Replace("<pageNumberWithZero:2>", "<pageNumber>")
                    .Replace("<pageNumberWithZero>", "<pageNumber>");
                LogWarn($"已切换至 -M \"{MultiPageDefaultSavePath}\"");
            }
        }
        if (myOption.BandwithAscending)
        {
            LogWarn("--bandwith-ascending 已被弃用, 建议使用 --video-ascending 与 --audio-ascending 来指定视频或音频是否升序, 本次执行已将视频与音频均设为升序");
            myOption.VideoAscending = true;
            myOption.AudioAscending = true;
        }
    }

    /// <summary>
    /// 解析用户指定的编码优先级
    /// </summary>
    /// <param name="myOption"></param>
    /// <returns></returns>
    internal static Dictionary<string, byte> ParseEncodingPriority(MyOption myOption, out string firstEncoding)
    {
        var encodingPriority = new Dictionary<string, byte>();
        firstEncoding = "";
        if (myOption.EncodingPriority != null)
        {
            var encodingPriorityTemp = myOption.EncodingPriority
                .ToUpper()
                .Replace('，', ',')
                .Replace("-", string.Empty)
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(s => !string.IsNullOrEmpty(s)).ToList();
            byte index = 0;
            firstEncoding = encodingPriorityTemp.FirstOrDefault() ?? "";
            foreach (string encoding in encodingPriorityTemp)
            {
                if (encodingPriority.ContainsKey(encoding))
                    continue;
                encodingPriority[encoding] = index;
                index++;
            }
        }
        return encodingPriority;
    }

    private static BBDownTDanmakuFormat[] ParseDownloadDanmakuFormats(MyOption myOption)
        => string.IsNullOrEmpty(myOption.DownloadDanmakuFormats)
            ? BBDownTDanmakuFormatInfo.DefaultFormats
            : SplitDanmakuFormats(myOption.DownloadDanmakuFormats).Select(BBDownTDanmakuFormatInfo.FromFormatName).ToArray();

    internal static string? ValidateDanmakuFormats(string? value)
    {
        if (string.IsNullOrEmpty(value)) return null;
        var formats = SplitDanmakuFormats(value);
        return formats.Length > 0 && formats.All(BBDownTDanmakuFormatInfo.AllFormatNames.Contains)
            ? null : $"弹幕格式无效，可选 {string.Join('/', BBDownTDanmakuFormatInfo.AllFormatNames)}，多个用逗号分隔。";
    }

    private static string[] SplitDanmakuFormats(string value)
        => value.Replace("，", ",").ToLower().Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

    /// <summary>
    /// 解析用户输入的清晰度规格优先级
    /// </summary>
    /// <param name="myOption"></param>
    /// <returns></returns>
    internal static Dictionary<string, int> ParseDfnPriority(MyOption myOption)
    {
        var dfnPriority = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        if (myOption.DfnPriority != null)
        {
            var dfnPriorityTemp = myOption.DfnPriority.Replace("，", ",").Split(',').Select(s => s.ToUpper().Trim()).Where(s => !string.IsNullOrEmpty(s));
            int index = 0;
            foreach (string dfn in dfnPriorityTemp)
            {
                if (dfnPriority.ContainsKey(dfn)) { continue; }
                dfnPriority[dfn] = index;
                index++;
            }
        }
        return dfnPriority;
    }

    /// <summary>
    /// 寻找并设置所需的二进制文件
    /// </summary>
    /// <param name="myOption"></param>
    /// <exception cref="Exception"></exception>
    internal static void FindBinaries(MyOption myOption)
    {
        if (SpaceBatchDownload.IsExportOnly(myOption)) return;
        if (ExistingFile(myOption.FFmpegPath) is { } ffmpegPath)
        {
            BBDownTMuxer.FFMPEG = ffmpegPath;
        }

        if (ExistingFile(myOption.Mp4boxPath) is { } mp4boxPath)
        {
            BBDownTMuxer.MP4BOX = mp4boxPath;
        }

        if (ExistingFile(myOption.Aria2cPath) is { } aria2cPath)
        {
            BBDownTAria2c.ARIA2C = aria2cPath;
        }
        //寻找ffmpeg或mp4box
        if (NeedsMuxer(myOption))
        {
            if (myOption.UseMP4box)
            {
                if (ResolveMp4box() is null)
                    throw new Exception("找不到可执行的mp4box文件");
            }
            else
            {
                BBDownTMuxer.FFMPEG = ExistingFile(BBDownTMuxer.FFMPEG) ?? FindExecutable("ffmpeg")
                    ?? throw new Exception("找不到可执行的ffmpeg文件");
            }
        }

        //寻找aria2c
        if (myOption.UseAria2c)
        {
            BBDownTAria2c.ARIA2C = ExistingFile(BBDownTAria2c.ARIA2C) ?? FindExecutable("aria2c")
                ?? throw new Exception("找不到可执行的aria2c文件");
        }
    }

    internal static string? ResolveMp4box()
    {
        var binPath = ExistingFile(BBDownTMuxer.MP4BOX)
            ?? FindExecutable("MP4Box") ?? FindExecutable("mp4box") ?? FindExecutable("MP4box");
        if (binPath is not null) BBDownTMuxer.MP4BOX = binPath;
        return binPath;
    }

    private static string? ExistingFile(string? path)
        => !string.IsNullOrEmpty(path) && File.Exists(path) ? Path.GetFullPath(path) : null;

    internal static bool NeedsMuxer(MyOption myOption)
    {
        return !(myOption.SkipMux
            || SpaceBatchDownload.IsExportOnly(myOption)
            || myOption.OnlyShowInfo
            || myOption.CoverOnly
            || myOption.SubOnly
            || myOption.DanmakuOnly);
    }

    internal static string? ValidateWorkOptions(MyOption option)
        => SpaceBatchDownload.ValidateOptions(option)
            ?? SubtitleSelection.ValidateOptions(option)
            ?? ValidateExclusiveOptions(option)
            ?? AudioLanguageSelection.ValidateOptions(option)
            ?? (option.UseIntlApi && (option.DownloadDanmaku || option.DanmakuOnly)
                ? "国际站弹幕暂不支持，请去掉 --download-danmaku 或 --danmaku-only" : null)
            ?? ValidateDanmakuFormats(option.DownloadDanmakuFormats);

    internal static string? ValidateExclusiveOptions(MyOption option)
    {
        if (option.CoverOnly && option.SkipCover) return "--cover-only 与 --skip-cover 不能同时使用";
        if (option.SubOnly && option.SkipSubtitle) return "--sub-only 与 --skip-subtitle 不能同时使用";
        if (new[] { option.SubOnly, option.DanmakuOnly, option.CoverOnly }.Count(selected => selected) > 1)
            return "--sub-only、--danmaku-only、--cover-only 只能指定其中一个";
        return null;
    }

    internal static DownloadConfig ArtifactDownloadConfig(MyOption option, DownloadTask? relatedTask = null)
        => new() { RestrictedOutputRoot = option.RestrictedOutputRoot, RelatedTask = relatedTask };

    internal static async Task DownloadCoverForMuxAsync(MyOption option, string url, string path,
        Func<string, string, DownloadConfig, Task>? download = null)
    {
        try
        {
            await (download ?? ((source, destination, settings) => DownloadFileAsync(source, destination, settings)))(
                url, path, ArtifactDownloadConfig(option));
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException
            || NetworkRetry.IsTransient(ex))
        {
            LogWarn($"封面下载失败, 将不嵌入封面: {ErrorText.Describe(ex)}");
        }
    }

    internal static string GetCoverForMux(MyOption option, string path, Func<string, bool>? exists = null)
        => !option.SkipCover && (exists ?? File.Exists)(path) ? path : "";

    /// <summary>
    /// 处理有冲突的选项
    /// </summary>
    /// <param name="myOption"></param>
    internal static void HandleConflictingOptions(MyOption myOption)
    {
        //手动选择时不能隐藏流
        if (myOption.Interactive)
        {
            myOption.HideStreams = false;
        }
        // 同时指定时保留两类流并跳过混流，等价于下载原始音视频流。
        if (myOption.AudioOnly && myOption.VideoOnly)
        {
            myOption.SkipMux = true;
        }
    }

    /// <summary>
    /// 设置用户输入的自定义工作目录
    /// </summary>
    /// <param name="myOption"></param>
    private static void ChangeWorkingDir(MyOption myOption)
    {
        if (!string.IsNullOrEmpty(myOption.WorkDir))
        {
            //解释环境变量
            myOption.WorkDir = Environment.ExpandEnvironmentVariables(myOption.WorkDir);
            var dir = Path.GetFullPath(myOption.WorkDir);
            OutputPathPolicy.ResolveArtifact(dir, myOption.RestrictedOutputRoot);
            if (!Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }
            //设置工作目录
            Environment.CurrentDirectory = dir;
            LogDebug("切换工作目录至：{0}", dir);
        }
    }

    /// <summary>
    /// 加载用户的认证信息（cookie或token）
    /// </summary>
    /// <param name="myOption"></param>
    private static string? LoadCredentials(MyOption myOption)
    {
        var loaded = IntlCookieStore.Load(Config.COOKIE, APP_DIR, myOption.UseIntlApi);
        string? webCookieFilePath = loaded.FilePath;
        if (webCookieFilePath is not null)
        {
            Log(myOption.UseIntlApi ? "加载本地国际站 Cookie..." : "加载本地cookie...");
            LogDebug("文件路径：{0}", webCookieFilePath);
            Config.COOKIE = loaded.Cookie;
        }
        if (!myOption.UseIntlApi && myOption.UseTvApi) LoadTokenFile(APP_DIR, "BBDownTTV.data");
        if (!myOption.UseIntlApi && myOption.UseAppApi) LoadTokenFile(APP_DIR, "BBDownTApp.data");

        if (!string.IsNullOrEmpty(Config.COOKIE))
        {
            AuthenticatedWebProfileStore.Configure(APP_DIR);
        }

        return webCookieFilePath;
    }

    internal static void LoadTokenFile(string directory, string fileName)
    {
        var path = Path.Combine(directory, fileName);
        if (!string.IsNullOrEmpty(Config.TOKEN) || !File.Exists(path)) return;
        Log("加载本地token...");
        LogDebug("文件路径：{0}", path);
        Config.TOKEN = File.ReadAllText(path).Replace("access_token=", "").Trim();
    }

    private static object fileLock = new object();
    public static void SaveAidToFile(string archiveKey)
    {
        lock (fileLock)
        {
            string filePath = Path.Combine(APP_DIR, "BBDownT.archives");
            LogDebug("文件路径：{0}", filePath);
            File.AppendAllText(filePath, $"{archiveKey}|");
        }
    }

    public static bool CheckAidFromFile(string archiveKey)
    {
        lock (fileLock)
        {
            string filePath = Path.Combine(APP_DIR, "BBDownT.archives");
            if (!File.Exists(filePath)) return false;
            LogDebug("文件路径：{0}", filePath);
            var text = File.ReadAllText(filePath);
            return text.Split('|').Any(item => item == archiveKey);
        }
    }

    /// <summary>
    /// 获取选中的分P列表
    /// </summary>
    /// <param name="myOption"></param>
    /// <param name="vInfo"></param>
    /// <returns></returns>
    internal static List<string>? GetSelectedPages(MyOption myOption, VInfo vInfo)
    {
        List<string>? selectedPages = null;
        List<Page> pagesInfo = vInfo.PagesInfo;
        string selectPage = myOption.SelectPage;

        if (!myOption.SelectPageSpecified && string.IsNullOrWhiteSpace(selectPage))
        {
            //如果用户没有选择分P, 根据epid或query param来确定某一集
            if (!string.IsNullOrEmpty(vInfo.Index))
            {
                selectedPages = [vInfo.Index];
                Log("程序已自动选择你输入的集数, 如果要下载其他集数请自行指定分P(如可使用-p ALL代表全部)");
            }
            else if (GetQueryString("p", myOption.Url.Split('#')[0]) is { Length: > 0 } urlPage)
            {
                selectedPages = PageSelectionParser.Parse(urlPage, pagesInfo.Count);
                Log("程序已自动选择你输入的集数, 如果要下载其他集数请自行指定分P(如可使用-p ALL代表全部)");
            }
        }
        else
        {
            selectedPages = PageSelectionParser.Parse(selectPage, pagesInfo.Count);
        }

        return selectedPages;
    }

    /// <summary>
    /// 处理CDN域名
    /// </summary>
    /// <param name="myOption"></param>
    /// <param name="video"></param>
    /// <param name="audio"></param>
    internal static void HandlePcdn(MyOption myOption, Video? selectedVideo, Audio? selectedAudio)
    {
        // International WEB URLs can be signed for the returned CDN host.
        // Only an explicitly supplied host may override that choice.
        if (myOption.UseIntlApi && string.IsNullOrEmpty(myOption.UposHost)) return;
        if (myOption.ForceReplaceHost && !myOption.AllowPcdn && string.IsNullOrEmpty(myOption.UposHost))
            myOption.UposHost = BACKUP_HOST;

        if (myOption.UposHost == "")
        {
            //处理PCDN
            if (!myOption.AllowPcdn)
            {
                var pcdnReg = PcdnRegex();
                if (selectedVideo != null && pcdnReg.IsMatch(selectedVideo.baseUrl))
                {
                    LogWarn($"检测到视频流为PCDN, 尝试强制替换为{BACKUP_HOST}……");
                    selectedVideo.baseUrl = pcdnReg.Replace(selectedVideo.baseUrl, $"://{BACKUP_HOST}/");
                }
                if (selectedAudio != null && pcdnReg.IsMatch(selectedAudio.baseUrl))
                {
                    LogWarn($"检测到音频流为PCDN, 尝试强制替换为{BACKUP_HOST}……");
                    selectedAudio.baseUrl = pcdnReg.Replace(selectedAudio.baseUrl, $"://{BACKUP_HOST}/");
                }
            }

            var akamReg = AkamRegex();
            if (selectedVideo != null && Config.AREA != "" && selectedVideo.baseUrl.Contains("akamaized.net"))
            {
                LogWarn($"检测到视频流为外国源, 尝试强制替换为{BACKUP_HOST}……");
                selectedVideo.baseUrl = akamReg.Replace(selectedVideo.baseUrl, $"://{BACKUP_HOST}/");
            }
            if (selectedAudio != null && Config.AREA != "" && selectedAudio.baseUrl.Contains("akamaized.net"))
            {
                LogWarn($"检测到音频流为外国源, 尝试强制替换为{BACKUP_HOST}……");
                selectedAudio.baseUrl = akamReg.Replace(selectedAudio.baseUrl, $"://{BACKUP_HOST}/");
            }
        }
        else
        {
            if (selectedVideo != null)
            {
                LogWarn($"尝试将视频流强制替换为{myOption.UposHost}……");
                selectedVideo.baseUrl = UposRegex().Replace(selectedVideo.baseUrl, $"://{myOption.UposHost}/");
            }
            if (selectedAudio != null)
            {
                LogWarn($"尝试将音频流强制替换为{myOption.UposHost}……");
                selectedAudio.baseUrl = UposRegex().Replace(selectedAudio.baseUrl, $"://{myOption.UposHost}/");
            }
        }
    }

    /// <summary>
    /// 打印解析到的各个轨道信息
    /// </summary>
    /// <param name="parsedResult"></param>
    /// <param name="pageDur"></param>
    private static void PrintAllTracksInfo(ParsedResult parsedResult, int pageDur, bool onlyShowInfo)
    {
        if (parsedResult.BackgroundAudioTracks.Any() && parsedResult.RoleAudioList.Any())
        {
            Log($"共计{parsedResult.BackgroundAudioTracks.Count}条背景音频流.");
            int index = 0;
            foreach (var a in parsedResult.BackgroundAudioTracks)
            {
                int pDur = pageDur == 0 ? a.dur : pageDur;
                LogColor($"{index++}. [{a.codecs}] [{a.bandwith} kbps] [~{FormatFileSize(pDur * a.bandwith * 1024 / 8)}]", false);
            }
            Log($"共计{parsedResult.RoleAudioList.Count}条配音, 每条包含{parsedResult.RoleAudioList[0].audio.Count}条配音流.");
            index = 0;
            foreach (var a in parsedResult.RoleAudioList[0].audio)
            {
                int pDur = pageDur == 0 ? a.dur : pageDur;
                LogColor($"{index++}. [{a.codecs}] [{a.bandwith} kbps] [~{FormatFileSize(pDur * a.bandwith * 1024 / 8)}]", false);
            }
        }
        //展示所有的音视频流信息
        if (parsedResult.VideoTracks.Any())
        {
            Log($"共计{parsedResult.VideoTracks.Count}条视频流.");
            int index = 0;
            foreach (var v in parsedResult.VideoTracks)
            {
                int pDur = pageDur == 0 ? v.dur : pageDur;
                var size = v.size > 0 ? v.size : pDur * v.bandwith * 1024 / 8;
                LogColor($"{index++}. [{v.dfn}] [{v.res}] [{v.codecs}] [{v.fps}] [{v.bandwith} kbps] [~{FormatFileSize(size)}]".Replace("[] ", ""), false);
                if (onlyShowInfo) Console.WriteLine(v.baseUrl);
            }
        }
        if (parsedResult.AudioTracks.Any())
        {
            Log($"共计{parsedResult.AudioTracks.Count}条音频流.");
            int index = 0;
            foreach (var a in parsedResult.AudioTracks)
            {
                int pDur = pageDur == 0 ? a.dur : pageDur;
                LogColor($"{index++}. [{a.codecs}] [{a.bandwith} kbps] [~{FormatFileSize(pDur * a.bandwith * 1024 / 8)}]", false);
                if (onlyShowInfo) Console.WriteLine(a.baseUrl);
            }
        }
    }

    private static void PrintSelectedTrackInfo(Video? selectedVideo, Audio? selectedAudio, int pageDur)
    {
        if (selectedVideo != null)
        {
            int pDur = pageDur == 0 ? selectedVideo.dur : pageDur;
            var size = selectedVideo.size > 0 ? selectedVideo.size : pDur * selectedVideo.bandwith * 1024 / 8;
            LogColor($"[视频] [{selectedVideo.dfn}] [{selectedVideo.res}] [{selectedVideo.codecs}] [{selectedVideo.fps}] [{selectedVideo.bandwith} kbps] [~{FormatFileSize(size)}]".Replace("[] ", ""), false);
        }
        if (selectedAudio != null)
        {
            int pDur = pageDur == 0 ? selectedAudio.dur : pageDur;
            LogColor($"[音频] [{selectedAudio.codecs}] [{selectedAudio.bandwith} kbps] [~{FormatFileSize(pDur * selectedAudio.bandwith * 1024 / 8)}]", false);
        }
    }

    /// <summary>
    /// 引导用户进行手动选择轨道
    /// </summary>
    /// <param name="parsedResult"></param>
    /// <param name="vIndex"></param>
    /// <param name="aIndex"></param>
    private static void SelectTrackManually(ParsedResult parsedResult, ref int vIndex, ref int aIndex)
    {
        if (parsedResult.VideoTracks.Any())
        {
            Log("请选择一条视频流(输入序号): ", false);
            Console.ForegroundColor = ConsoleColor.Cyan;
            vIndex = ParseSelectionIndex(Console.ReadLine(), parsedResult.VideoTracks.Count);
            Console.ResetColor();
        }
        if (parsedResult.AudioTracks.Any())
        {
            Log("请选择一条音频流(输入序号): ", false);
            Console.ForegroundColor = ConsoleColor.Cyan;
            aIndex = ParseSelectionIndex(Console.ReadLine(), parsedResult.AudioTracks.Count);
            Console.ResetColor();
        }
    }

    internal static bool TryRestoreTrackChoice(ParsedResult result, Video? video, Audio? audio, ref int vIndex, ref int aIndex)
    {
        var videoIndex = video is null ? 0 : result.VideoTracks.FindIndex(track => track.id == video.id && track.codecs == video.codecs);
        var audioIndex = audio is null ? 0 : result.AudioTracks.FindIndex(track => track.id == audio.id && track.codecs == audio.codecs);
        if (videoIndex < 0 || audioIndex < 0) return false;
        vIndex = videoIndex;
        aIndex = audioIndex;
        return true;
    }

    internal static int ParseSelectionIndex(string? input, int itemCount)
    {
        return int.TryParse(input, out var index) && index >= 0 && index < itemCount
            ? index
            : 0;
    }

    /// <summary>
    /// 下载轨道
    /// </summary>
    /// <returns></returns>
    internal static string GetTrackResumeIdentity(Page page, string apiType, string role,
        Video? video = null, Audio? audio = null, string? variant = null)
    {
        var fields = new[]
        {
            apiType, page.aid, page.cid, page.epid, role, variant ?? "",
            video?.id ?? audio?.id ?? "", video?.codecs ?? audio?.codecs ?? "",
            video?.res ?? "", video?.fps ?? "",
            (video?.bandwith ?? audio?.bandwith ?? 0).ToString(System.Globalization.CultureInfo.InvariantCulture)
        };
        var identity = string.Concat(fields.Select(value => value.Length + ":" + value));
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(identity))).ToLowerInvariant();
    }

    private static async Task DownloadTrackAsync(string url, string destPath, DownloadConfig downloadConfig,
        bool video, string resourceIdentity)
    {
        downloadConfig = downloadConfig with { ResourceIdentity = resourceIdentity };
        if (downloadConfig.MultiThread && !url.Contains("-cmcc-"))
        {
            var downloadedClips = await MultiThreadDownloadFileAsync(url, destPath, downloadConfig);
            if (downloadedClips.Length > 0)
            {
                Log($"合并{(video ? "视频" : "音频")}分片...");
                await MergeTrackClipsAsync(downloadedClips, destPath, downloadConfig);
            }
        }
        else
        {
            if (downloadConfig.MultiThread && url.Contains("-cmcc-"))
            {
                LogWarn("检测到cmcc域名cdn, 已经禁用多线程");
                downloadConfig.ForceHttp = false;
            }
            await DownloadFileAsync(url, destPath, downloadConfig);
        }
        DeleteStaleTrackFiles(OutputPathPolicy.ResolveArtifact(destPath, downloadConfig.RestrictedOutputRoot));
    }

    internal static void DeleteEmptyDownloadDirectory(string path)
    {
        if (Directory.Exists(path) && !Directory.EnumerateFileSystemEntries(path).Any())
            Directory.Delete(path);
    }

    [GeneratedRegex("://.*:\\d+/")]
    private static partial Regex PcdnRegex();
    [GeneratedRegex("://.*akamaized\\.net/")]
    private static partial Regex AkamRegex();
    [GeneratedRegex("://[^/]+/")]
    private static partial Regex UposRegex();
}
