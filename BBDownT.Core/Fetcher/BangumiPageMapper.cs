using System.Text.Json;
using static BBDownT.Core.Entity.Entity;

namespace BBDownT.Core.Fetcher;

internal static class BangumiPageMapper
{
    // Envelope/section selection belongs to each fetcher. Only international
    // episodes may omit pub_time; all other required fields remain strict.
    internal static (List<Page> Pages, string SelectedIndex) Map(
        IEnumerable<JsonElement> episodes,
        string selectedEpisodeId,
        bool allowMissingPublicationTime)
    {
        List<Page> pages = [];
        string selectedIndex = "";
        foreach (var episode in episodes)
        {
            if (episode.TryGetProperty("badge", out var badge) && badge.ToString() == "预告")
                continue;

            var resolution = ReadResolution(episode);
            var title = (episode.GetProperty("title").ToString() + " "
                + episode.GetProperty("long_title").ToString()).Trim();
            Page page = new(
                pages.Count + 1,
                episode.GetProperty("aid").ToString(),
                episode.GetProperty("cid").ToString(),
                episode.GetProperty("id").ToString(),
                title,
                0,
                resolution,
                allowMissingPublicationTime && !episode.TryGetProperty("pub_time", out _)
                    ? 0
                    : episode.GetProperty("pub_time").GetInt64());
            if (page.epid == selectedEpisodeId) selectedIndex = page.index.ToString();
            pages.Add(page);
        }

        return (pages, selectedIndex);
    }

    private static string ReadResolution(JsonElement episode)
    {
        if (episode.TryGetProperty("dimension", out var dimension)
            && dimension.ValueKind == JsonValueKind.Object
            && dimension.TryGetProperty("width", out var width)
            && dimension.TryGetProperty("height", out var height))
        {
            return width.ToString() + "x" + height.ToString();
        }

        return "";
    }
}
