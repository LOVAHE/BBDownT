using System.Text.Json;
using BBDownT.Core.Entity;

namespace BBDownT.Core;

internal static class AudioLanguageMapper
{
    internal static void Map(JsonElement root, ParsedResult result, string? requestedLanguage = null)
    {
        var current = ReadString(root, "cur_language");
        // Verify every selected-language response, including the highest-quality
        // refetch, before its video/audio tracks can be merged with earlier ones.
        if (!string.IsNullOrEmpty(requestedLanguage)
            && !string.Equals(current, requestedLanguage, StringComparison.OrdinalIgnoreCase))
            throw new AudioLanguageUnavailableException(
                $"接口未返回所选配音 {requestedLanguage}（返回：{current ?? "未标明"}），已停止下载，避免保存错误版本");

        if (!string.IsNullOrEmpty(current))
        {
            result.CurrentAudioLanguage = current;
            if (string.IsNullOrEmpty(requestedLanguage)) result.DefaultAudioLanguage = current;
        }
        if (!root.TryGetProperty("language", out var language)
            || language.ValueKind != JsonValueKind.Object
            || !language.TryGetProperty("items", out var items)
            || items.ValueKind != JsonValueKind.Array)
            return;

        var languages = new List<AudioLanguageInfo>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in items.EnumerateArray())
        {
            var code = ReadString(item, "lang");
            if (string.IsNullOrWhiteSpace(code) || !seen.Add(code)) continue;
            var title = ReadString(item, "title");
            var isAi = item.TryGetProperty("production_type", out var productionType)
                && productionType.ValueKind == JsonValueKind.Number
                && productionType.TryGetInt32(out var type) && type == 2;
            languages.Add(new AudioLanguageInfo(code, string.IsNullOrWhiteSpace(title) ? code : title, isAi));
        }
        result.AudioLanguages = languages;
    }

    private static string? ReadString(JsonElement value, string name) =>
        value.ValueKind == JsonValueKind.Object
        && value.TryGetProperty(name, out var property)
        && property.ValueKind == JsonValueKind.String
            ? property.GetString() : null;
}
