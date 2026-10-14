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
    public static readonly BBDownTDanmakuFormat[] DefaultFormats = [BBDownTDanmakuFormat.Xml, BBDownTDanmakuFormat.Ass];
    public static readonly string[] DefaultFormatsNames = DefaultFormats.Select(f => f.ToString().ToLower()).ToArray();
    // 可选项
    public static readonly string[] AllFormatNames = Enum.GetNames(typeof(BBDownTDanmakuFormat)).Select(f => f.ToLower()).ToArray();

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
