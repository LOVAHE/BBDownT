using System;

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
}
