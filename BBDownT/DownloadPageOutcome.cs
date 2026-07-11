namespace BBDownT;

internal enum DownloadPageOutcome
{
    Completed,
    AlreadyExists,
    Partial,
    InfoOnly,
    ExclusiveArtifact,
    Failed
}

internal static class DownloadPageOutcomeExtensions
{
    public static bool ShouldArchive(this DownloadPageOutcome outcome)
    {
        return outcome is DownloadPageOutcome.Completed
            or DownloadPageOutcome.AlreadyExists
            or DownloadPageOutcome.Partial;
    }

    public static bool IsSuccessful(this DownloadPageOutcome outcome)
    {
        return outcome is not DownloadPageOutcome.Failed;
    }
}
