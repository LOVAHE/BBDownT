using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using static BBDownT.Core.Entity.Entity;
using static BBDownT.BBDownTUtil;
using static BBDownT.Core.Util.SubUtil;
using static BBDownT.Core.Logger;
using System.IO;
using BBDownT.Core;
using AudioMaterial = BBDownT.Core.Entity.Entity.AudioMaterial;

namespace BBDownT;

static partial class BBDownTMuxer
{
    public static string FFMPEG = "ffmpeg";
    public static string MP4BOX = "mp4box";

    internal static string GetChapterPath(string videoPath, string audioPath, string? restrictedOutputRoot, Func<string, bool>? isLink = null)
        => GetScratchPath(videoPath, audioPath, "chapters", restrictedOutputRoot, isLink);

    private static string GetScratchPath(string videoPath, string audioPath, string name, string? restrictedOutputRoot,
        Func<string, bool>? isLink = null)
    {
        var mediaPath = string.IsNullOrEmpty(videoPath) ? audioPath : videoPath;
        var path = Path.Combine(Path.GetDirectoryName(mediaPath)!, name);
        return OutputPathPolicy.ResolveArtifact(path, restrictedOutputRoot, isLink);
    }

    private static int RunExe(string app, IEnumerable<string> args)
    {
        using Process p = new();
        p.StartInfo.FileName = app;
        foreach (var arg in args)
        {
            p.StartInfo.ArgumentList.Add(arg);
        }
        p.StartInfo.UseShellExecute = false;
        p.StartInfo.RedirectStandardInput = true;
        p.StartInfo.RedirectStandardError = true;
        p.StartInfo.CreateNoWindow = false;
        p.ErrorDataReceived += (_, output) =>
        {
            if (!string.IsNullOrWhiteSpace(output.Data))
                Log(output.Data);
        };
        p.StartInfo.StandardErrorEncoding = Encoding.UTF8;
        p.Start();
        p.StandardInput.Close();
        p.BeginErrorReadLine();
        p.WaitForExit();
        return p.ExitCode;
    }

    private static string FormatCommandForLog(IEnumerable<string> args)
    {
        return string.Join(" ", args.Select(QuoteForLog));
    }

    private static string QuoteForLog(string arg)
    {
        if (arg.Length == 0)
        {
            return "\"\"";
        }

        return arg.Any(char.IsWhiteSpace) || arg.Contains('"')
            ? "\"" + arg.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\""
            : arg;
    }

    private static int MuxByMp4box(string videoPath, string audioPath, List<AudioMaterial> audioMaterial, string outPath, string desc, string title, string author, string episodeId, string pic, string lang, List<Subtitle>? subs, bool audioOnly, string? chapterFile, bool simplyMux, string? restrictedOutputRoot)
    {
        var tagsFile = GetScratchPath(videoPath, audioPath, "itags", restrictedOutputRoot);
        var args = Mp4boxArgs(videoPath, audioPath, audioMaterial, outPath, lang, subs, audioOnly, chapterFile, tagsFile);
        File.WriteAllText(tagsFile, string.Join("\n", Mp4boxTags(desc, title, author, episodeId, pic, simplyMux).Prepend("tool=")), new UTF8Encoding(false));
        LogDebug("mp4box命令: {0}", FormatCommandForLog(args));
        try
        {
            return RunExe(MP4BOX, args);
        }
        finally
        {
            File.Delete(tagsFile);
        }
    }

    internal static List<string> Mp4boxArgs(string videoPath, string audioPath, List<AudioMaterial> audioMaterial, string outPath, string lang, List<Subtitle>? subs, bool audioOnly, string? chapterFile, string tagsFile)
    {
        List<string> args = [];
        int nowId = 0;
        if (Config.DEBUG_LOG) args.Add("-v");
        args.AddRange(["-inter", "500", "-noprog"]);
        if (!string.IsNullOrEmpty(videoPath))
        {
            args.AddRange(["-add", $"{videoPath}#trackID={(audioOnly && audioPath == "" ? "2" : "1")}:name="]);
            nowId++;
        }
        if (!string.IsNullOrEmpty(audioPath))
        {
            args.AddRange(["-add", $"{audioPath}:lang={(lang == "" ? "und" : lang)}"]);
            nowId++;
            if (audioMaterial.Any()) args.AddRange(["-udta", $"{nowId}:type=name:str=原音频"]);
        }
        foreach (var audio in audioMaterial)
        {
            args.AddRange(["-add", audio.path]);
            nowId++;
            if (!string.IsNullOrWhiteSpace(audio.title)) args.AddRange(["-udta", $"{nowId}:type=name:str={audio.title}"]);
        }
        if (chapterFile != null)
            args.AddRange(["-chap", chapterFile]);

        if (subs != null)
        {
            for (int i = 0; i < subs.Count; i++)
            {
                if (File.Exists(subs[i].path) && File.ReadAllText(subs[i].path!) != "")
                {
                    nowId++;
                    args.AddRange(["-add", $"{subs[i].path}#trackID=1:name=:hdlr=sbtl:lang={GetSubtitleCode(subs[i].lan).Item1}"]);
                    args.AddRange(["-udta", $"{nowId}:type=name:str={GetSubtitleCode(subs[i].lan).Item2}"]);
                }
            }
        }

        args.AddRange(["-itags", tagsFile]);
        args.AddRange(["-new", "--", outPath]);
        return args;
    }

    internal static List<string> Mp4boxTags(string desc, string title, string author, string episodeId, string pic, bool simplyMux)
    {
        List<string> metaTags = [];
        if (!string.IsNullOrEmpty(pic))
            metaTags.Add($"cover={pic}");
        if (simplyMux) return metaTags;
        if (!string.IsNullOrEmpty(episodeId))
        {
            metaTags.Add($"album={title}");
            metaTags.Add($"title={episodeId}");
        }
        else
        {
            metaTags.Add($"title={title}");
        }
        metaTags.Add($"sdesc={desc}");
        metaTags.Add($"comment={desc}");
        metaTags.Add($"artist={author}");
        return metaTags;
    }

    public static int MuxAV(bool useMp4box, string videoPath, string audioPath, List<AudioMaterial> audioMaterial, string outPath, string desc = "", string title = "", string author = "", string episodeId = "", string pic = "", string lang = "", List<Subtitle>? subs = null, bool audioOnly = false, bool videoOnly = false, List<ViewPoint>? points = null, long pubTime = 0, bool simplyMux = false, bool isHevc = false, string? restrictedOutputRoot = null)
    {
        outPath = OutputPathPolicy.ResolveArtifact(outPath, restrictedOutputRoot);
        if (audioOnly && audioPath != "")
            videoPath = "";
        if (videoOnly)
            audioPath = "";
        string? chapterFile = null;
        try
        {
            if (points != null && points.Any())
            {
                chapterFile = GetChapterPath(videoPath, audioPath, restrictedOutputRoot);
                File.WriteAllText(chapterFile, useMp4box ? GetMp4boxMetaString(points) : GetFFmpegMetaString(points));
            }
            if (useMp4box)
            {
                return MuxByMp4box(videoPath, audioPath, audioMaterial, outPath, desc, title, author, episodeId, pic, lang, subs, audioOnly, chapterFile, simplyMux, restrictedOutputRoot);
            }

            var args = FFmpegArgs(videoPath, audioPath, audioMaterial, outPath, desc, title, author, episodeId, pic, lang, subs, audioOnly, videoOnly, chapterFile, pubTime, simplyMux, isHevc);
            LogDebug("ffmpeg命令: {0}", FormatCommandForLog(args));
            return RunExe(FFMPEG, args);
        }
        finally
        {
            if (chapterFile != null) File.Delete(chapterFile);
        }
    }

    internal static List<string> FFmpegArgs(string videoPath, string audioPath, List<AudioMaterial> audioMaterial, string outPath, string desc, string title, string author, string episodeId, string pic, string lang, List<Subtitle>? subs, bool audioOnly, bool videoOnly, string? chapterFile, long pubTime, bool simplyMux, bool isHevc)
    {
        //----分析并生成-i参数
        List<string> inputArgs = [];
        List<string> metaArgs = [];
        byte inputCount = 0;
        foreach (string path in new[] { videoPath, audioPath })
        {
            if (!string.IsNullOrEmpty(path))
            {
                inputCount++;
                inputArgs.AddRange(["-i", path]);
            }
        }

        if (audioMaterial.Any())
        {
            byte audioCount = 0;
            metaArgs.AddRange(["-metadata:s:a:0", "title=原音频"]);
            foreach (var audio in audioMaterial)
            {
                inputCount++;
                audioCount++;
                inputArgs.AddRange(["-i", audio.path]);
                if (!string.IsNullOrWhiteSpace(audio.title)) metaArgs.AddRange([$"-metadata:s:a:{audioCount}", $"title={audio.title}"]);
                if (!string.IsNullOrWhiteSpace(audio.personName)) metaArgs.AddRange([$"-metadata:s:a:{audioCount}", $"artist={audio.personName}"]);
            }
        }

        if (!string.IsNullOrEmpty(pic))
        {
            inputCount++;
            inputArgs.AddRange(["-i", pic]);
        }

        if (subs != null)
        {
            var subtitleStream = 0;
            for (int i = 0; i < subs.Count; i++)
            {
                if(File.Exists(subs[i].path) && File.ReadAllText(subs[i].path!) != "")
                {
                    inputCount++;
                    inputArgs.AddRange(["-i", subs[i].path]);
                    var (code, name) = GetSubtitleCode(subs[i].lan);
                    metaArgs.AddRange([$"-metadata:s:s:{subtitleStream}", $"title={name}", $"-metadata:s:s:{subtitleStream}", $"language={code}"]);
                    subtitleStream++;
                }
            }
        }

        if (!string.IsNullOrEmpty(pic))
            metaArgs.AddRange([$"-disposition:v:{(audioOnly ? "0" : "1")}", "attached_pic"]);
        if (chapterFile != null)
            inputArgs.AddRange(["-i", chapterFile, "-map_chapters", inputCount.ToString()]);

        foreach (var i in Enumerable.Range(0, inputCount))
        {
            inputArgs.AddRange(["-map", i.ToString()]);
        }

        //----分析完毕
        List<string> args = ["-loglevel", Config.DEBUG_LOG ? "verbose" : "warning", "-y"];
        args.AddRange(inputArgs);
        args.AddRange(metaArgs);
        // Keep input global/stream metadata disabled while copying chapter titles.
        args.AddRange(["-map_metadata:g", "-1", "-map_metadata:s", "-1"]);
        if (!simplyMux) {
            args.AddRange(["-metadata", $"title={(episodeId == "" ? title : episodeId)}"]);
            args.AddRange(["-metadata", $"comment={desc}"]);
            if (lang != "") args.AddRange(["-metadata:s:a:0", $"language={lang}"]);
            args.AddRange(["-metadata", $"description={desc}"]);
            if (!string.IsNullOrEmpty(author)) args.AddRange(["-metadata", $"artist={author}"]);
            if (episodeId != "") args.AddRange(["-metadata", $"album={title}"]);
            if (pubTime != 0) args.AddRange(["-metadata", $"creation_time={DateTimeOffset.FromUnixTimeSeconds(pubTime).ToString("yyyy-MM-ddTHH:mm:ss.ffffffZ")}"]);
        }
        args.AddRange(["-c:v", "copy", "-c:a", "copy"]);
        if (audioOnly && audioPath == "") args.Add("-vn");
        if (videoOnly && !audioOnly) args.Add("-an");
        if (subs != null) args.AddRange(["-c:s", "mov_text"]);
        // Apple players reject hev1, see https://discussions.apple.com/thread/253081863?sortBy=rank
        if (isHevc) args.AddRange(["-tag:v:0", "hvc1"]);
        args.AddRange(["-movflags", "faststart", "-strict", "-2", "-f", "mp4", "--", outPath]);
        return args;
    }

    public static void MergeFLV(string[] files, string outPath, Func<string, string, int>? convert = null)
    {
        if (files.Length == 0) throw new ArgumentException("没有可合并的分段", nameof(files));
        if (files.Length == 1)
        {
            if (!File.Exists(files[0]) || new FileInfo(files[0]).Length == 0)
                throw new IOException("合并分片未生成有效文件");
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath))!);
            File.Move(files[0], outPath, overwrite: true);
            MediaOutput.DeleteInput(files[0], outPath);
        }
        else
        {
            convert ??= ConvertFlvSegment;
            var converted = files.Select(file => Path.ChangeExtension(file, ".ts")).ToArray();
            try
            {
                for (var i = 0; i < files.Length; i++)
                {
                    if (convert(files[i], converted[i]) != 0 || !File.Exists(converted[i])
                        || new FileInfo(converted[i]).Length == 0)
                        throw new IOException($"分段转换失败：{Path.GetFileName(files[i])}");
                }
                CombineMultipleFilesIntoSingleFile(converted, outPath);
                foreach (var file in files) MediaOutput.DeleteInput(file, outPath);
            }
            finally
            {
                foreach (var file in converted)
                    if (File.Exists(file)) File.Delete(file);
            }
        }
    }

    private static int ConvertFlvSegment(string input, string output)
    {
        List<string> args = ["-loglevel", "warning", "-y", "-i", input, "-map", "0", "-c", "copy", "-f", "mpegts", "-bsf:v", "h264_mp4toannexb", output];
        LogDebug("ffmpeg命令: {0}", FormatCommandForLog(args));
        return RunExe(FFMPEG, args);
    }
}
