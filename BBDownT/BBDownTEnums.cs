using System;
using System.Linq;

namespace BBDownT;

public enum BBDownTDanmakuFormat
{
    Xml,
    Ass,
}

public static class BBDownTDanmakuFormatInfo
{
    // 默认
    public static BBDownTDanmakuFormat[] DefaultFormats = [BBDownTDanmakuFormat.Xml, BBDownTDanmakuFormat.Ass];
    public static string[] DefaultFormatsNames = DefaultFormats.Select(f => f.ToString().ToLower()).ToArray();
    // 可选项
    public static string[] AllFormatNames = Enum.GetNames(typeof(BBDownTDanmakuFormat)).Select(f => f.ToLower()).ToArray();

    public static BBDownTDanmakuFormat FromFormatName(string formatName)
    {
        return formatName switch
        {
            "xml" => BBDownTDanmakuFormat.Xml,
            "ass" => BBDownTDanmakuFormat.Ass,
            _ => BBDownTDanmakuFormat.Xml,
        };
    }
}
