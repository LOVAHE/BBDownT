namespace BBDownT.Core.Fetcher;

internal static class MediaListPagination
{
    public static void EnsureAdvanced(string previousCursor, string currentCursor, bool hasMore)
    {
        if (hasMore && string.Equals(previousCursor, currentCursor, StringComparison.Ordinal))
        {
            throw new InvalidDataException("媒体列表分页未返回新的游标，已停止以避免无限请求");
        }
    }
}
