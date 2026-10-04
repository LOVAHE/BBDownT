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

    internal static List<Subtitle> Filter(IReadOnlyList<Subtitle> subtitles, MyOption option) =>
        SelectDefaults(FilterCandidates(subtitles, option), option);

    internal static List<Subtitle> FilterCandidates(IReadOnlyList<Subtitle> subtitles, MyOption option)
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

    internal static List<Subtitle> SelectDefaults(IReadOnlyList<Subtitle> candidates, MyOption option)
    {
        if (!option.UseIntlApi) return candidates.ToList();

        var preferred = candidates.Where(s => s.FormatVariantGroup is not null)
            .GroupBy(s => (Group: s.FormatVariantGroup!, Language: s.lan.ToUpperInvariant()))
            .Select(group => group.OrderBy(s => OutputFormat(s) == "srt" ? 0 : 1).First())
            .ToHashSet();
        return candidates.Where(s => s.FormatVariantGroup is null || preferred.Contains(s)).ToList();
    }

    private static string Family(string language) =>
        (language.StartsWith("ai-", StringComparison.OrdinalIgnoreCase) ? language[3..] : language).Split('-')[0];

    private static bool Matches(string language, string code) =>
        string.Equals(language, code, StringComparison.OrdinalIgnoreCase)
        || (!code.Contains('-') && string.Equals(Family(language), code, StringComparison.OrdinalIgnoreCase));

    internal static string OutputFormat(Subtitle subtitle) =>
        Path.GetExtension(subtitle.path).Equals(".ass", StringComparison.OrdinalIgnoreCase)
        || (Path.GetExtension(subtitle.path).Length == 0 && subtitle.url.Split('?', '#')[0]
            .EndsWith(".ass", StringComparison.OrdinalIgnoreCase)) ? "ass" : "srt";

    internal static List<Subtitle> Choose(IReadOnlyList<Subtitle> subtitles, MyOption option, TextReader input,
        TextWriter output, Session? session = null)
    {
        var candidates = FilterCandidates(subtitles, option);
        var defaults = SelectDefaults(candidates, option);
        var selected = defaults;
        var reusedChoice = false;
        if (option.Interactive && !option.OnlyShowInfo && session is not null
            && session.TryApply(candidates, defaults, output, out var reused))
        {
            selected = reused;
            reusedChoice = true;
        }
        if (option.OnlyShowInfo || option.Interactive)
        {
            var displayed = option.OnlyShowInfo ? subtitles : reusedChoice ? selected : candidates;
            output.WriteLine($"字幕: 发现 {subtitles.Count} 条，符合筛选 {selected.Count} 条");
            for (var i = 0; i < displayed.Count; i++)
            {
                var s = displayed[i];
                var kind = s.IsAi ? (s.aiType == 1 ? "AI翻译" : "AI") : s.type == 0 ? "普通(CC)" : "来源未知";
                output.WriteLine($"  {i + 1}. {s.lan} | {s.lanDoc ?? s.lan} | {kind} | {OutputFormat(s).ToUpperInvariant()} | {(selected.Contains(s) ? "已选" : "未选")}");
            }
        }
        if (subtitles.Count == 0) output.WriteLine("未获取到可用字幕。");
        else if (selected.Count == 0) output.WriteLine("没有符合语言、AI策略和格式的字幕。");
        if (reusedChoice || !option.Interactive || option.OnlyShowInfo || selected.Count == 0) return selected;

        while (true)
        {
            output.Write("请选择字幕(1起始，逗号/范围/ALL，NONE跳过，回车保留以上选择): ");
            var answer = input.ReadLine();
            if (string.IsNullOrWhiteSpace(answer))
            {
                session?.Record(selected, flexibleFormat: true, allMatches: true);
                return selected;
            }
            if (answer.Trim().Equals("NONE", StringComparison.OrdinalIgnoreCase))
            {
                session?.Record([], flexibleFormat: false, allMatches: false);
                return [];
            }
            try
            {
                var indices = PageSelectionParser.Parse(answer, candidates.Count);
                var chosen = indices is null ? candidates : indices.Select(index => candidates[int.Parse(index) - 1]).ToList();
                session?.Record(chosen, flexibleFormat: false,
                    allMatches: answer.Trim().Equals("ALL", StringComparison.OrdinalIgnoreCase));
                return chosen;
            }
            catch (ArgumentException)
            {
                output.WriteLine($"字幕序号无效，请输入 1-{candidates.Count}、范围、ALL 或 NONE。");
            }
        }
    }

    internal sealed class Session
    {
        private List<Preference>? preferences;

        internal void Record(IReadOnlyList<Subtitle> selected, bool flexibleFormat, bool allMatches)
        {
            preferences = selected.GroupBy(s => (Source: SourceKey.From(s), Format: flexibleFormat ? "*" : OutputFormat(s)))
                .Select(group => new Preference(group.Key.Source, group.Key.Format, group.Count(), allMatches)).ToList();
        }

        internal bool TryApply(List<Subtitle> candidates, List<Subtitle> defaults, TextWriter output,
            out List<Subtitle> selected)
        {
            selected = [];
            if (preferences is null) return false;
            if (preferences.Count == 0)
            {
                output.WriteLine("沿用此前字幕选择：跳过字幕。");
                return true;
            }
            foreach (var preference in preferences)
            {
                var available = preference.Format == "*" ? defaults : candidates;
                var matches = available.Where(s => SourceKey.From(s) == preference.Source
                    && (preference.Format == "*" || OutputFormat(s) == preference.Format)).ToList();
                if (matches.Count == 0)
                {
                    var missingLabelMatches = available.Where(s =>
                    {
                        var source = SourceKey.From(s);
                        return source.SameKind(preference.Source) && (source.Label is null || preference.Source.Label is null)
                            && (preference.Format == "*" || OutputFormat(s) == preference.Format);
                    }).ToList();
                    var logicalTracks = missingLabelMatches.GroupBy(s => (object?)s.FormatVariantGroup ?? s).Count();
                    if (logicalTracks == 1) matches = missingLabelMatches;
                    else if (logicalTracks > 1)
                    {
                        output.WriteLine("字幕来源标签缺失且存在多条同语字幕，无法确定此前选择，请重新选择。");
                        selected = [];
                        return false;
                    }
                }
                if (!preference.AllMatches && matches.Count > preference.Count)
                {
                    output.WriteLine("同语言、来源和格式存在多条字幕，无法确定此前选择，请重新选择。");
                    selected = [];
                    return false;
                }
                if (matches.Count == 0)
                    output.WriteLine($"未找到此前选择的字幕：{preference.Source.Language}，来源或格式不匹配。");
                else if (!preference.AllMatches && matches.Count < preference.Count)
                    output.WriteLine($"仅找到此前选择的部分字幕：{preference.Source.Language}，请检查字幕来源和格式。");
                foreach (var match in matches) if (!selected.Contains(match)) selected.Add(match);
            }
            return true;
        }

        private sealed record Preference(SourceKey Source, string Format, int Count, bool AllMatches);
        private sealed record SourceKey(string Language, string? Label, int? Type, int? AiType, bool IsAi)
        {
            internal static SourceKey From(Subtitle subtitle) => new(subtitle.lan.ToUpperInvariant(),
                string.IsNullOrWhiteSpace(subtitle.lanDoc) ? null : subtitle.lanDoc.Trim().ToUpperInvariant(),
                subtitle.type, subtitle.aiType, subtitle.IsAi);
            internal bool SameKind(SourceKey other) => Language == other.Language && Type == other.Type
                && AiType == other.AiType && IsAi == other.IsAi;
        }
    }
}
