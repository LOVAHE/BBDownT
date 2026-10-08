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
        // 下载历史：重新下载用的选项在解析(会改写部分选项)之前记下；
        // 批量下载的每一条在解析前就开始记录，解析失败也会留下一条失败记录
        var request = relatedTask is null ? null : DownloadHistoryRequest.From(option);
        var video = batchEntry && relatedTask is not null ? relatedTask.BeginVideo(option.Url, request!) : null;
        try
        {
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
            if (relatedTask is not null)
            {
                video ??= relatedTask.BeginVideo(option.Url, request!);
                relatedTask.DescribeVideo(video, prepared.Aid, info, prepared.ApiType);
            }
            await prepared.Download(relatedTask);
            if (video is not null) relatedTask!.FinishVideo(video, true, null);
        }
        catch (Exception e) when (video is not null)
        {
            relatedTask!.FinishVideo(video, false, e.Message);
            throw;
        }
    }

    private static async Task<PreparedVideoDownload> PrepareVideoAsync(MyOption option)
    {
        var (encodingPriority, dfnPriority, firstEncoding, downloadDanmaku, downloadDanmakuFormats,
            input, savePathFormat, lang, aidOri, delay) = SetUpWork(option);
        var (fetchedAid, info, apiType) = await GetVideoInfoAsync(option, aidOri, input);
        return new PreparedVideoDownload(fetchedAid, info, task => DownloadPagesAsync(option,
            info, encodingPriority, dfnPriority, firstEncoding,
            downloadDanmaku, downloadDanmakuFormats, input, savePathFormat, lang,
            fetchedAid, delay, apiType, task), apiType);
    }
}

/// <param name="ApiType">实际使用的解析接口(WEB/TV/APP/INTL)，写入下载历史用</param>
internal sealed record PreparedVideoDownload(string Aid, VInfo Info, Func<DownloadTask?, Task> Download, string? ApiType = null);
