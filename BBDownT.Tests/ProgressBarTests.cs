namespace BBDownT.Tests;

public class ProgressBarTests
{
    [Theory]
    [InlineData(double.NaN, 0)]
    [InlineData(double.PositiveInfinity, 0)]
    [InlineData(double.NegativeInfinity, 0)]
    [InlineData(-0.1, 0)]
    [InlineData(0.5, 0.5)]
    [InlineData(1.1, 1)]
    public void NormalizeProgress_AlwaysReturnsFiniteUnitInterval(double input, double expected)
    {
        var progress = ProgressBar.NormalizeProgress(input);

        Assert.Equal(expected, progress);
        Assert.True(double.IsFinite(progress));
    }
}
