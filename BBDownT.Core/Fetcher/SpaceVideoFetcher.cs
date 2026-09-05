using BBDownT.Core.Entity;
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
        string userName = GetValidFileName(userInfo.RootElement.GetProperty("data").GetProperty("info").GetProperty("uname").ToString(), ".", true);
        List<string> urls = new();
        int pageSize = 50;
        int pageNumber = 1;
        var api = Parser.WbiSign($"mid={id}&order=pubdate&pn={pageNumber}&ps={pageSize}&tid=0&wts={DateTimeOffset.Now.ToUnixTimeSeconds().ToString()}");
        api = $"https://api.bilibili.com/x/space/wbi/arc/search?{api}";
        string json = await fetch(api);
        using var infoJson = JsonDocument.Parse(json);
        var pages = infoJson.RootElement.GetProperty("data").GetProperty("list").GetProperty("vlist").EnumerateArray();
        foreach (var page in pages)
        {
            urls.Add($"https://www.bilibili.com/video/av{page.GetProperty("aid")}");
        }
        int totalCount = infoJson.RootElement.GetProperty("data").GetProperty("page").GetProperty("count").GetInt32();
        int totalPage = (int)Math.Ceiling((double)totalCount / pageSize);
        while (pageNumber < totalPage)
        {
            pageNumber++;
            urls.AddRange(await GetVideosByPageAsync(pageNumber, pageSize, id, fetch));
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

    private static async Task<List<string>> GetVideosByPageAsync(
        int pageNumber, int pageSize, string mid, Func<string, Task<string>> fetch)
    {
        List<string> urls = new();
        var api = Parser.WbiSign($"mid={mid}&order=pubdate&pn={pageNumber}&ps={pageSize}&tid=0&wts={DateTimeOffset.Now.ToUnixTimeSeconds().ToString()}");
        api = $"https://api.bilibili.com/x/space/wbi/arc/search?{api}";
        string json = await fetch(api);
        using var infoJson = JsonDocument.Parse(json);
        var pages = infoJson.RootElement.GetProperty("data").GetProperty("list").GetProperty("vlist").EnumerateArray();
        foreach (var page in pages)
        {
            urls.Add($"https://www.bilibili.com/video/av{page.GetProperty("aid")}");
        }
        return urls;
    }

    private static string GetValidFileName(string input, string re = ".", bool filterSlash = false)
    {
        string title = input;
        foreach (char invalidChar in Path.GetInvalidFileNameChars())
        {
            title = title.Replace(invalidChar.ToString(), re);
        }
        if (filterSlash)
        {
            title = title.Replace("/", re);
            title = title.Replace("\\", re);
        }
        return title;
    }
}
