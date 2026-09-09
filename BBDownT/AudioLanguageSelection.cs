using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using BBDownT.Core.Entity;

namespace BBDownT;

internal static class AudioLanguageSelection
{
    internal static string? Normalize(string? code) =>
        string.IsNullOrWhiteSpace(code) ? null : code.Trim();

    internal static string? ValidateCode(string? code)
    {
        code = Normalize(code);
        if (code is null) return null;
        return code.Length <= 64 && char.IsAsciiLetterOrDigit(code[0])
            && code.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_')
            ? null : "--audio-language 必须是单个配音语言代码（字母、数字、- 或 _，最多64字符）；请从 -info 列表选择";
    }

    internal static string? ValidateOptions(MyOption option)
    {
        if (ValidateCode(option.AudioLanguage) is { } error) return error;
        if (Normalize(option.AudioLanguage) is null) return null;
        if (option.UseTvApi || option.UseAppApi || option.UseIntlApi)
            return "--audio-language 目前仅支持默认 WEB 解析模式，不能与 -tv、-app、-intl 同时使用";
        if (option.SubOnly || option.CoverOnly || option.DanmakuOnly)
            return "--audio-language 不能与仅字幕、仅封面或仅弹幕模式同时使用";
        return null;
    }

    internal static async Task<ParsedResult> FetchAsync(
        string? requestedLanguage, Func<string?, Task<ParsedResult>> fetch)
    {
        var initial = await fetch(null);
        var code = Normalize(requestedLanguage);
        if (code is null) return initial;

        var selected = initial.AudioLanguages.FirstOrDefault(language =>
            string.Equals(language.Code, code, StringComparison.OrdinalIgnoreCase));
        if (selected is null)
        {
            var available = initial.AudioLanguages.Count == 0
                ? "接口未提供可选配音语言"
                : string.Join(", ", initial.AudioLanguages.Select(language => language.Code));
            throw new AudioLanguageUnavailableException($"没有找到配音语言 {code}；{available}");
        }

        // Replace the complete playback result: translated video and audio may
        // both differ. Never merge the default-language tracks into this result.
        var result = await fetch(selected.Code);
        if (!string.Equals(result.CurrentAudioLanguage, selected.Code, StringComparison.OrdinalIgnoreCase))
            throw new AudioLanguageUnavailableException($"接口未确认所选配音 {selected.Code}，已停止下载，避免保存错误版本");
        if (result.Clips.Count > 0)
            throw new AudioLanguageUnavailableException("配音语言选择目前仅支持 DASH 音视频流，接口返回了分段合并流");
        result.DefaultAudioLanguage = initial.DefaultAudioLanguage ?? initial.CurrentAudioLanguage;
        return result;
    }

    internal static void PrintAvailable(ParsedResult result, TextWriter output)
    {
        if (result.AudioLanguages.Count == 0)
        {
            output.WriteLine("接口未提供可选配音语言，使用默认音轨。");
            return;
        }
        output.WriteLine("可选配音语言：");
        foreach (var language in result.AudioLanguages)
        {
            var isDefault = string.Equals(language.Code, result.DefaultAudioLanguage, StringComparison.OrdinalIgnoreCase);
            var marker = isDefault ? " [默认]" : "";
            if (string.Equals(language.Code, result.CurrentAudioLanguage, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(result.CurrentAudioLanguage, result.DefaultAudioLanguage, StringComparison.OrdinalIgnoreCase))
                marker += " [当前]";
            var line = $"  {language.Code}: {language.Title}{(language.IsAi ? " [AI]" : "")}{marker}";
            if (isDefault && ReferenceEquals(output, Console.Out) && !Console.IsOutputRedirected)
            {
                var previousColor = Console.ForegroundColor;
                try
                {
                    Console.ForegroundColor = ConsoleColor.Green;
                    output.WriteLine(line);
                }
                finally
                {
                    Console.ForegroundColor = previousColor;
                }
            }
            else output.WriteLine(line);
        }
    }

    internal static string WithLanguageSuffix(string path, string? language)
    {
        var code = Normalize(language);
        if (code is null || string.IsNullOrEmpty(path)) return path;
        if (ValidateCode(code) is { } error) throw new ArgumentException(error);
        var extension = Path.GetExtension(path);
        return path[..(path.Length - extension.Length)] + ".audio-" + code.ToLowerInvariant() + extension;
    }

    internal static string OutputPath(string path, string? language, bool audioOnly)
    {
        var output = WithLanguageSuffix(path, language);
        // Resolve an explicit audio-only variant's final extension before the
        // existing-output check; a same-language MP4 is not the requested M4A.
        return Normalize(language) is not null && audioOnly ? Path.ChangeExtension(output, ".m4a") : output;
    }

    // Existing archives contain only AIDs and cannot distinguish dubbed versions.
    // Explicit language downloads use their distinct output-file cache instead.
    internal static bool UseAidArchive(MyOption option) =>
        option.SaveArchivesToFile && Normalize(option.AudioLanguage) is null;
}
