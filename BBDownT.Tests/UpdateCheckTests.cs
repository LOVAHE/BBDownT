namespace BBDownT.Tests;

public class UpdateCheckTests
{
    [Theory]
    [InlineData("2.1.1", "2.1.2", true)]
    [InlineData("2.1.2", "2.1.2", false)]
    [InlineData("2.1.2", "2.1.1", false)]
    [InlineData("v2.1.1", "V2.1.2", true)]
    [InlineData("2.1.2", "https://github.com/example", false)]
    public void IsNewerVersion_OnlyAcceptsGreaterSemanticVersions(
        string currentVersion, string candidateVersion, bool expected)
    {
        Assert.Equal(expected, BBDownTUtil.IsNewerVersion(currentVersion, candidateVersion));
    }
}
