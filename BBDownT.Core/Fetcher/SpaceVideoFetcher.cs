using BBDownT.Core.Entity;
using BBDownT.Core.Util;
using System.Text.Json;
using static BBDownT.Core.Util.HTTPUtil;

namespace BBDownT.Core.Fetcher;

public class SpaceVideoFetcher : IFetcher
{
    public Task<VInfo> FetchAsync(string id) => FetchAsync(
        id, url => GetWebSourceAsync(url),
        (path, content) => File.WriteAllTextAsync(path, content));

    internal static async Task<VInfo> FetchAsync(
        string id,
        Func<string, Task<string>> fetch,
        Func<string, string, Task> writeList)
    {
        id = id[4..];
        // using the live API can bypass w_rid
        string userInfoApi = $"https://api.live.bilibili.com/live_user/v1/Master/info?uid={id}";
        using var userInfo = JsonDocument.Parse(await fetch(userInfoApi));
        string userName = SanitizeUploaderName(BilibiliApi.ReadPayload(userInfo.RootElement, "获取UP主信息").GetProperty("info").GetProperty("uname").ToString());
        int pageSize = 50;
        int pageNumber = 1;
        await WbiKey.EnsureAsync(fetch);
        var (urls, totalCount) = await GetVideosByPageAsync(pageNumber, pageSize, id, fetch);
        int totalPage = (int)Math.Ceiling((double)totalCount / pageSize);
        while (pageNumber < totalPage)
        {
            pageNumber++;
            urls.AddRange((await GetVideosByPageAsync(pageNumber, pageSize, id, fetch)).Urls);
        }
        var path = Path.GetFullPath($"{userName}的投稿视频.txt");
        // Return only after every page and the complete file write succeed.
        await writeList(path, string.Join(Environment.NewLine, urls));
        return new SpaceVideoInfo
        {
            Title = userName,
            Desc = "投稿视频链接清单",
            Pic = "",
            PubTime = 0,
            PagesInfo = [],
            UrlListFilePath = path
        };
    }

    private static async Task<(List<string> Urls, int Count)> GetVideosByPageAsync(
        int pageNumber, int pageSize, string mid, Func<string, Task<string>> fetch)
    {
        List<string> urls = new();
        var api = Parser.WbiSign($"mid={mid}&order=pubdate&pn={pageNumber}&ps={pageSize}&tid=0&wts={AppKeys.UnixSeconds()}");
        api = $"https://api.bilibili.com/x/space/wbi/arc/search?{api}";
        string json = await fetch(api);
        using var infoJson = JsonDocument.Parse(json);
        var data = BilibiliApi.ReadPayload(infoJson.RootElement, "获取UP主投稿列表");
        foreach (var page in data.GetProperty("list").GetProperty("vlist").EnumerateArray())
        {
            urls.Add($"https://www.bilibili.com/video/av{page.GetProperty("aid")}");
        }
        return (urls, pageNumber == 1 ? data.GetProperty("page").GetProperty("count").GetInt32() : 0);
    }

    private static string SanitizeUploaderName(string name)
    {
        foreach (char invalidChar in Path.GetInvalidFileNameChars())
        {
            name = name.Replace(invalidChar.ToString(), ".");
        }
        return name.Replace("/", ".").Replace("\\", ".");
    }
}
