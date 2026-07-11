using BBDownT.Core.Entity;
using static BBDownT.Core.Util.HTTPUtil;

namespace BBDownT.Core.Fetcher;

/// <summary>
/// 系列解析
/// https://space.bilibili.com/23630128/channel/seriesdetail?sid=340933
/// </summary>
public class SeriesListFetcher : IFetcher
{
    public async Task<VInfo> FetchAsync(string id)
    {
        var bizId = id[12..];
        return await MediaListFetcherCore.FetchAsync(
            bizId,
            type: 5,
            descending: true,
            includeBvid: true,
            displayName: "系列",
            url => GetWebSourceAsync(url));
    }
}
