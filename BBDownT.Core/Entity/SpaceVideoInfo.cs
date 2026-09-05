namespace BBDownT.Core.Entity;

// A completed URL-list export. Media is downloaded separately from this file.
public sealed class SpaceVideoInfo : VInfo
{
    public required string UrlListFilePath { get; init; }
}
