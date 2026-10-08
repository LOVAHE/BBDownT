using System;
using System.Collections.Generic;

namespace BBDownT;

public sealed record BBDownTServerOptions
{
    public bool AllowAria2cArgs { get; init; }
    public bool AllowCustomOutput { get; init; }
    public bool AllowCustomNetworkHosts { get; init; }
    public bool AllowPrivateCallbacks { get; init; }
    public string DownloadRoot { get; init; } = Environment.CurrentDirectory;
    public int MaxQueueLength { get; init; } = 100;
    public int MaxFinishedTasks { get; init; } = 1000;
    public long FinishedTaskRetentionSeconds { get; init; } = 24 * 60 * 60;
    /// <summary>
    /// 未启用API Token时，除本机地址外还允许的 Host(域名)，供本机反向代理保留原始 Host 的部署使用
    /// </summary>
    public IReadOnlyList<string> AllowedHosts { get; init; } = [];
    /// <summary>
    /// 下载历史文件；为null时使用数据目录(BBDOWNT_DATA_DIR，默认程序所在目录)下的 history.json
    /// </summary>
    public string? HistoryPath { get; init; }
    /// <summary>
    /// 下载历史最多保留的条数，超出时删除最旧的记录
    /// </summary>
    public int MaxHistoryEntries { get; init; } = DownloadHistoryStore.DefaultCapacity;
}
