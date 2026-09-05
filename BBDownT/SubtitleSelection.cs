using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using static BBDownT.Core.Entity.Entity;

namespace BBDownT;

internal static class SubtitleSelection
{
    internal static string? ValidateLanguage(string? value) => value is null
        || value.Split(',').All(code => Regex.IsMatch(code.Trim(), @"^[a-zA-Z]{2,8}(-[a-zA-Z0-9]{1,8})*$"))
        ? null : "字幕语言无效，请输入逗号分隔的语言代码，如 zh,en 或 zh-Hans。";

    internal static string? ValidatePolicy(string? value) => value is null
        || value.Trim().ToLowerInvariant() is "exclude" or "include" or "prefer-human" or "only"
        ? null : "字幕AI策略无效，可选 exclude/include/prefer-human/only。";

    internal static string? ValidateOptions(MyOption option) =>
        ValidateLanguage(option.SubtitleLanguage) ?? ValidatePolicy(option.AiSubtitlePolicy);

    internal static List<Subtitle> Filter(IReadOnlyList<Subtitle> subtitles, MyOption option)
    {
        if (ValidateOptions(option) is { } error) throw new ArgumentException(error);
        var languages = option.SubtitleLanguage?.Split(',', StringSplitOptions.TrimEntries);
        var candidates = subtitles.Where(s => languages is null || languages.Any(code => Matches(s.lan, code))).ToList();
        var policy = option.AiSubtitlePolicy?.Trim().ToLowerInvariant() ?? (option.SkipAi ? "exclude" : "include");
        // Unknown sources remain eligible but do not displace an AI track as confirmed CC.
        var ccLanguages = candidates.Where(s => s.type == 0).Select(s => Family(s.lan)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return candidates.Where(s => policy switch
        {
            "exclude" => !s.IsAi,
            "only" => s.IsAi,
            "prefer-human" => !s.IsAi || !ccLanguages.Contains(Family(s.lan)),
            _ => true
        }).ToList();
    }

    private static string Family(string language) =>
        (language.StartsWith("ai-", StringComparison.OrdinalIgnoreCase) ? language[3..] : language).Split('-')[0];

    private static bool Matches(string language, string code) =>
        string.Equals(language, code, StringComparison.OrdinalIgnoreCase)
        || (!code.Contains('-') && string.Equals(Family(language), code, StringComparison.OrdinalIgnoreCase));

    internal static List<Subtitle> Choose(IReadOnlyList<Subtitle> subtitles, MyOption option, TextReader input, TextWriter output)
    {
        var selected = Filter(subtitles, option);
        if (option.OnlyShowInfo || option.Interactive)
        {
            var displayed = option.OnlyShowInfo ? subtitles : selected;
            output.WriteLine($"字幕: 发现 {subtitles.Count} 条，符合筛选 {selected.Count} 条");
            for (var i = 0; i < displayed.Count; i++)
            {
                var s = displayed[i];
                var kind = s.IsAi ? (s.aiType == 1 ? "AI翻译" : "AI") : s.type == 0 ? "普通(CC)" : "来源未知";
                output.WriteLine($"  {i + 1}. {s.lan} | {s.lanDoc ?? s.lan} | {kind} | {(selected.Contains(s) ? "已选" : "未选")}");
            }
        }
        if (subtitles.Count == 0) output.WriteLine("未获取到可用字幕。");
        else if (selected.Count == 0) output.WriteLine("没有符合语言和AI策略的字幕。");
        if (!option.Interactive || option.OnlyShowInfo || selected.Count == 0) return selected;

        while (true)
        {
            output.Write("请选择字幕(1起始，逗号/范围/ALL，NONE跳过，回车保留以上选择): ");
            var answer = input.ReadLine();
            if (string.IsNullOrWhiteSpace(answer)) return selected;
            if (answer.Trim().Equals("NONE", StringComparison.OrdinalIgnoreCase)) return [];
            try
            {
                var indices = PageSelectionParser.Parse(answer, selected.Count);
                return indices is null ? selected : indices.Select(index => selected[int.Parse(index) - 1]).ToList();
            }
            catch (ArgumentException)
            {
                output.WriteLine($"字幕序号无效，请输入 1-{selected.Count}、范围、ALL 或 NONE。");
            }
        }
    }
}
