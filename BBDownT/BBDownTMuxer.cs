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
using System.Runtime.InteropServices;

namespace BBDownT;

static partial class BBDownTMuxer
{
    public static string FFMPEG = "ffmpeg";
    public static string MP4BOX = "mp4box";

    private static int RunExe(string app, IEnumerable<string> args, bool customBin = false)
    {
        int code = 0;
        Process p = new();
        p.StartInfo.FileName = app;
        foreach (var arg in args)
        {
            p.StartInfo.ArgumentList.Add(arg);
        }
        p.StartInfo.UseShellExecute = false;
        p.StartInfo.RedirectStandardError = true;
        p.StartInfo.CreateNoWindow = false;
        p.ErrorDataReceived += delegate (object sendProcess, DataReceivedEventArgs output) {
            if (!string.IsNullOrWhiteSpace(output.Data))
                Log(output.Data);
        };
        p.StartInfo.StandardErrorEncoding = Encoding.UTF8;
        p.Start();
        p.BeginErrorReadLine();
        p.WaitForExit();
        code = p.ExitCode;
        p.Close();
        p.Dispose();
        return code;
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

    private static int MuxByMp4box(string url, string videoPath, string audioPath, string outPath, string desc, string title, string author, string episodeId, string pic, string lang, List<Subtitle>? subs, bool audioOnly, bool videoOnly, List<ViewPoint>? points)
    {
        List<string> args = [];
        List<string> metaTags = [];
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
        }
        if (points != null && points.Any())
        {
            var meta = GetMp4boxMetaString(points);
            var metaFile = Path.Combine(Path.GetDirectoryName(string.IsNullOrEmpty(videoPath) ? audioPath : videoPath)!, "chapters");
            File.WriteAllText(metaFile, meta);
            args.AddRange(["-chap", metaFile]);
        }
        if (!string.IsNullOrEmpty(pic))
            metaTags.Add($"cover={pic}");
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
        metaTags.Add($"comment={url}");
        metaTags.Add($"artist={author}");

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

        //----分析完毕
        if (metaTags.Any())
        {
            args.AddRange(["-itags", "tool=:" + string.Join(':', metaTags)]);
        }
        args.AddRange(["-new", "--", outPath]);
        LogDebug("mp4box命令: {0}", FormatCommandForLog(args));
        return RunExe(MP4BOX, args, MP4BOX != "mp4box");
    }

    public static int MuxAV(bool useMp4box, string bvid, string videoPath, string audioPath, List<AudioMaterial> audioMaterial, string outPath, string desc = "", string title = "", string author = "", string episodeId = "", string pic = "", string lang = "", List<Subtitle>? subs = null, bool audioOnly = false, bool videoOnly = false, List<ViewPoint>? points = null, long pubTime = 0, bool simplyMux = false, bool isHevc = false)
    {
        if (audioOnly && audioPath != "")
            videoPath = "";
        if (videoOnly)
            audioPath = "";
        var url = $"https://www.bilibili.com/video/{bvid}/";

        if (useMp4box)
        {
            return MuxByMp4box(url, videoPath, audioPath, outPath, desc, title, author, episodeId, pic, lang, subs, audioOnly, videoOnly, points);
        }

        if (outPath.Contains('/') && ! Directory.Exists(Path.GetDirectoryName(outPath)))
            Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
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
            for (int i = 0; i < subs.Count; i++)
            {
                if(File.Exists(subs[i].path) && File.ReadAllText(subs[i].path!) != "")
                {
                    inputCount++;
                    inputArgs.AddRange(["-i", subs[i].path]);
                    metaArgs.AddRange([$"-metadata:s:s:{i}", $"title={GetSubtitleCode(subs[i].lan).Item2}", $"-metadata:s:s:{i}", $"language={GetSubtitleCode(subs[i].lan).Item1}"]);
                }
            }
        }

        if (!string.IsNullOrEmpty(pic))
            metaArgs.AddRange([$"-disposition:v:{(audioOnly ? "0" : "1")}", "attached_pic"]);
        if (points != null && points.Any())
        {
            var meta = GetFFmpegMetaString(points);
            var metaFile = Path.Combine(Path.GetDirectoryName(string.IsNullOrEmpty(videoPath) ? audioPath : videoPath)!, "chapters");
            File.WriteAllText(metaFile, meta);
            inputArgs.AddRange(["-i", metaFile, "-map_chapters", inputCount.ToString()]);
        }

        foreach (var i in Enumerable.Range(0, inputCount))
        {
            inputArgs.AddRange(["-map", i.ToString()]);
        }

        //----分析完毕
        List<string> args = ["-loglevel", Config.DEBUG_LOG ? "verbose" : "warning", "-y"];
        args.AddRange(inputArgs);
        args.AddRange(metaArgs);
        if (!simplyMux) {
            args.AddRange(["-metadata", $"title={(episodeId == "" ? title : episodeId)}"]);
            args.AddRange(["-metadata", $"comment={url}"]);
            if (lang != "") args.AddRange(["-metadata:s:a:0", $"language={lang}"]);
            if (!string.IsNullOrWhiteSpace(desc)) args.AddRange(["-metadata", $"description={desc}"]);
            if (!string.IsNullOrEmpty(author)) args.AddRange(["-metadata", $"artist={author}"]);
            if (episodeId != "") args.AddRange(["-metadata", $"album={title}"]);
            if (pubTime != 0) args.AddRange(["-metadata", $"creation_time={DateTimeOffset.FromUnixTimeSeconds(pubTime).ToString("yyyy-MM-ddTHH:mm:ss.ffffffZ")}"]);
        }
        args.AddRange(["-c:v", "copy", "-c:a", "copy"]);
        if (audioOnly && audioPath == "") args.Add("-vn");
        if (subs != null) args.AddRange(["-c:s", "mov_text"]);
        // fix macOS hev1, see https://discussions.apple.com/thread/253081863?sortBy=rank
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX) && isHevc) args.AddRange(["-tag:v:0", "hvc1"]);
        args.AddRange(["-movflags", "faststart", "-strict", "unofficial", "-strict", "-2", "-f", "mp4", "--", outPath]);

        LogDebug("ffmpeg命令: {0}", FormatCommandForLog(args));
        return RunExe(FFMPEG, args, FFMPEG != "ffmpeg");
    }

    public static void MergeFLV(string[] files, string outPath)
    {
        if (files.Length == 1)
        {
            File.Move(files[0], outPath);
        }
        else
        {
            foreach (var file in files)
            {
                var tmpFile = Path.Combine(Path.GetDirectoryName(file)!, Path.GetFileNameWithoutExtension(file) + ".ts");
                List<string> args = ["-loglevel", "warning", "-y", "-i", file, "-map", "0", "-c", "copy", "-f", "mpegts", "-bsf:v", "h264_mp4toannexb", tmpFile];
                LogDebug("ffmpeg命令: {0}", FormatCommandForLog(args));
                RunExe("ffmpeg", args);
                File.Delete(file);
            }
            var f = GetFiles(Path.GetDirectoryName(files[0])!, ".ts");
            CombineMultipleFilesIntoSingleFile(f, outPath);
            foreach (var s in f) File.Delete(s);
        }
    }
}
