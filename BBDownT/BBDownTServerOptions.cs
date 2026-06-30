namespace BBDownT;

public sealed record BBDownTServerOptions
{
    public bool AllowAria2cArgs { get; init; }
    public bool AllowCustomOutput { get; init; }
    public string DownloadRoot { get; init; } = "";
    public int MaxQueueLength { get; init; } = 100;
}
