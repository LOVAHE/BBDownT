using System.Reflection;

namespace BBDownT.Tests;

public class UpdateCheckTests
{
    [Theory]
    [InlineData("2.1.1", "2.1.2", true)]
    [InlineData("2.1.2", "2.1.2", false)]
    [InlineData("2.1.2", "2.1.1", false)]
    [InlineData("v2.1.1", "V2.1.2", true)]
    [InlineData("2.1.2", "https://github.com/example", false)]
    [InlineData("2.2.0-beta3", "2.2.0", true)]
    [InlineData("2.2.0-beta3", "2.1.7", false)]
    [InlineData("2.2.0", "2.2.0-beta3", false)]
    public void IsNewerVersion_TreatsTheReleaseAsNewerThanItsBetas(
        string currentVersion, string candidateVersion, bool expected)
    {
        Assert.True(BBDownTSelfUpdater.ReleaseVersion.TryParse(currentVersion, out var current));
        Assert.Equal(expected, BBDownTUtil.IsNewerVersion(current, candidateVersion));
    }

    [Fact]
    public void CurrentVersion_KeepsThePrereleaseTag()
    {
        var informational = typeof(Program).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion;

        Assert.Equal(informational.Split('+')[0], BBDownTSelfUpdater.CurrentVersion()?.ToString());
    }
}
