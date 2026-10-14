using System;
using System.Threading.Tasks;
using BBDownT.Core.Entity;
using BBDownT.Core.Util;

namespace BBDownT;

partial class Program
{
    // CLI and the serialized API worker share the same export/download result path.
    internal static async Task ExecuteWorkAsync(
        MyOption option, DownloadTask? relatedTask = null, bool batchEntry = false,
        Func<MyOption, Task<PreparedVideoDownload>>? prepare = null,
        Func<string, Task<string>>? resolveShortLink = null)
    {
        ThrowIfInvalid(option);
        if (BBDownTUtil.IsShortLinkUri(option.Url))
        {
            HTTPUtil.UserAgent = option.UserAgent;
            option.Url = await (resolveShortLink ?? BBDownTUtil.ResolveShortLinkAsync)(option.Url);
            ThrowIfInvalid(option);
        }
        Action<string>? reportAid = relatedTask is not null && !batchEntry ? relatedTask.SetAid : null;
        var prepared = await (prepare ?? (input => PrepareVideoAsync(input, reportAid)))(option);
        var info = prepared.Info;
        if (relatedTask is not null && !batchEntry)
        {
            relatedTask.SetAid(prepared.Aid);
            relatedTask.SetMetadata(info.Title, info.Pic, info.PubTime);
        }

        if (info is SpaceVideoInfo space)
        {
            await SpaceBatchDownload.HandleExportAsync(
                space.UrlListFilePath, option,
                child => ExecuteWorkAsync(child, relatedTask, batchEntry: true, prepare: prepare), relatedTask);
            return;
        }

        if (option.DownloadAll)
            throw new InvalidOperationException("该输入未解析为UP主投稿清单，无法执行 --download-all");
        await prepared.Download(relatedTask);
    }

    private static void ThrowIfInvalid(MyOption option)
    {
        if (ValidateWorkOptions(option) is { } error) throw new ArgumentException(error);
    }

    private static async Task<PreparedVideoDownload> PrepareVideoAsync(MyOption option, Action<string>? onAidResolved)
    {
        var (encodingPriority, dfnPriority, firstEncoding, downloadDanmaku, downloadDanmakuFormats, delay) = SetUpWork(option);
        var (fetchedAid, info, apiType) = await GetVideoInfoAsync(option, onAidResolved);
        return new PreparedVideoDownload(fetchedAid, info, task => DownloadPagesAsync(option,
            info, encodingPriority, dfnPriority, firstEncoding,
            downloadDanmaku, downloadDanmakuFormats, fetchedAid, delay, apiType, task));
    }
}

internal sealed record PreparedVideoDownload(string Aid, VInfo Info, Func<DownloadTask?, Task> Download);
