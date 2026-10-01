namespace BBDownT.Tests;

public class DownloadPageOutcomeTests
{
    [Fact]
    public void CompletedMediaOutcomes_AreArchived()
    {
        foreach (var outcome in new[]
        {
            DownloadPageOutcome.Completed,
            DownloadPageOutcome.AlreadyExists,
            DownloadPageOutcome.Partial
        })
        {
            Assert.True(outcome.IsSuccessful());
            Assert.True(outcome.ShouldArchive());
        }
    }

    [Fact]
    public void NonMediaModes_SucceedWithoutArchive()
    {
        foreach (var outcome in new[]
        {
            DownloadPageOutcome.InfoOnly,
            DownloadPageOutcome.ExclusiveArtifact
        })
        {
            Assert.True(outcome.IsSuccessful());
            Assert.False(outcome.ShouldArchive());
        }
    }

    [Fact]
    public void FailedOutcome_IsNeitherSuccessfulNorArchived()
    {
        Assert.False(DownloadPageOutcome.Failed.IsSuccessful());
        Assert.False(DownloadPageOutcome.Failed.ShouldArchive());
    }

}
