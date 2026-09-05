using System;
using System.Threading.Tasks;
using BBDownT.Core.Entity;

namespace BBDownT;

partial class Program
{
    // CLI and the serialized API worker share the same export/download result path.
    internal static async Task ExecuteWorkAsync(
        MyOption option, DownloadTask? relatedTask = null, bool batchEntry = false,
        Func<MyOption, Task<PreparedVideoDownload>>? prepare = null)
    {
        var validationError = SpaceBatchDownload.ValidateOptions(option);
        if (validationError is not null) throw new ArgumentException(validationError);
        var prepared = await (prepare ?? PrepareVideoAsync)(option);
        var info = prepared.Info;
        if (relatedTask is not null && !batchEntry)
        {
            if (string.IsNullOrEmpty(relatedTask.Aid)) relatedTask.SetAid(prepared.Aid);
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

    private static async Task<PreparedVideoDownload> PrepareVideoAsync(MyOption option)
    {
        var (encodingPriority, dfnPriority, firstEncoding, downloadDanmaku, downloadDanmakuFormats,
            input, savePathFormat, lang, aidOri, delay) = SetUpWork(option);
        var (fetchedAid, info, apiType) = await GetVideoInfoAsync(option, aidOri, input);
        return new PreparedVideoDownload(fetchedAid, info, task => DownloadPagesAsync(option,
            info, encodingPriority, dfnPriority, firstEncoding,
            downloadDanmaku, downloadDanmakuFormats, input, savePathFormat, lang,
            fetchedAid, delay, apiType, task));
    }
}

internal sealed record PreparedVideoDownload(string Aid, VInfo Info, Func<DownloadTask?, Task> Download);
