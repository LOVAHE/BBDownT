using System;
using System.Collections.Generic;

namespace BBDownT;

internal static class PageSelectionParser
{
    private static readonly HashSet<string> LastPageAliases =
        new(StringComparer.OrdinalIgnoreCase) { "LAST", "NEW", "LATEST" };

    internal static List<string>? Parse(string selection, int pageCount)
    {
        if (pageCount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(pageCount));
        }

        var normalized = selection.Trim();
        if (normalized.Length == 0)
        {
            throw InvalidSelection(selection, pageCount);
        }
        if (string.Equals(normalized, "ALL", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var selectedPages = new List<string>();
        var seenPages = new HashSet<int>();
        foreach (var rawToken in normalized.Split(','))
        {
            var token = rawToken.Trim();
            if (token.Length == 0)
            {
                throw InvalidSelection(selection, pageCount);
            }

            if (LastPageAliases.Contains(token))
            {
                AddPage(pageCount, pageCount, selectedPages, seenPages, selection);
                continue;
            }

            if (token.Contains('-'))
            {
                var range = token.Split('-', StringSplitOptions.TrimEntries);
                if (range.Length != 2
                    || !TryParsePageNumber(range[0], out var start)
                    || !TryParsePageNumber(range[1], out var end)
                    || start > end)
                {
                    throw InvalidSelection(selection, pageCount);
                }
                for (var page = start; page <= end; page++)
                {
                    AddPage(page, pageCount, selectedPages, seenPages, selection);
                }
                continue;
            }

            if (!TryParsePageNumber(token, out var selectedPage))
            {
                throw InvalidSelection(selection, pageCount);
            }
            AddPage(selectedPage, pageCount, selectedPages, seenPages, selection);
        }

        return selectedPages;
    }

    private static bool TryParsePageNumber(string value, out int page)
    {
        page = 0;
        if (value.Length == 0)
        {
            return false;
        }
        foreach (var character in value)
        {
            if (character is < '0' or > '9')
            {
                return false;
            }
        }
        return int.TryParse(value, out page);
    }

    private static void AddPage(
        int page,
        int pageCount,
        List<string> selectedPages,
        HashSet<int> seenPages,
        string selection)
    {
        if (page < 1 || page > pageCount)
        {
            throw InvalidSelection(selection, pageCount);
        }
        if (seenPages.Add(page))
        {
            selectedPages.Add(page.ToString());
        }
    }

    private static ArgumentException InvalidSelection(string selection, int pageCount)
    {
        // 不带 paramName：NativeAOT 裁掉资源字符串后会在消息末尾多出 "Arg_ParamName_Name, selection"
        return new ArgumentException(
            $"「分P」写法无效：{selection}。请填写 1-{pageCount} 之间的序号、范围、逗号列表、LAST/LATEST/NEW 或 ALL。");
    }

    /// <summary>
    /// 只检查写法、不展开范围(不知道分P数时使用，如提交任务时的预检)：
    /// 逐个字符扫描，耗时只与输入长度有关，"1-2147483647" 这类超大范围不会被展开。
    /// 是否超出实际分P数要等拿到视频信息后再用 <see cref="Parse"/> 检查。
    /// </summary>
    internal static string? ValidateSyntax(string? selection)
    {
        if (selection is null) return InvalidSyntaxMessage("");
        var normalized = selection.Trim();
        if (string.Equals(normalized, "ALL", StringComparison.OrdinalIgnoreCase)) return null;
        if (normalized.Length == 0) return InvalidSyntaxMessage(selection);

        foreach (var rawToken in normalized.Split(','))
        {
            var token = rawToken.Trim();
            if (token.Length == 0) return InvalidSyntaxMessage(selection);
            if (LastPageAliases.Contains(token)) continue;

            if (token.Contains('-'))
            {
                var range = token.Split('-', StringSplitOptions.TrimEntries);
                if (range.Length != 2
                    || !TryParsePageNumber(range[0], out var start)
                    || !TryParsePageNumber(range[1], out var end)
                    || start < 1
                    || start > end)
                {
                    return InvalidSyntaxMessage(selection);
                }
                continue;
            }

            if (!TryParsePageNumber(token, out var page) || page < 1) return InvalidSyntaxMessage(selection);
        }
        return null;
    }

    /// <summary>
    /// 已知分P数时的提示：给出该视频的分P数和可用写法
    /// </summary>
    internal static string DescribeInvalidSelection(string selection, int pageCount)
    {
        var examples = pageCount switch
        {
            <= 1 => "1",
            2 => "1、1-2",
            3 => "1、1,3、2-3",
            _ => $"1、1,3-{pageCount}、2-{pageCount}"
        };
        return $"「分P」写法无效：{selection}。该视频共 {pageCount} 个分P，可填写如 {examples}、LAST（最新一P）或 ALL（全部）。";
    }

    private static string InvalidSyntaxMessage(string selection) =>
        $"「分P」写法无效：{selection}。可填写如 2、1,3-5、LAST（最新一P）或 ALL（全部）。";
}
