using CelesteRecHelper.Core.Obs;

namespace CelesteRecHelper.Core.Tests;

public sealed class ReplayCapacityTests
{
    [Fact]
    public void DurationLimitedOutputDoesNotApplyAnUnusedMemoryCap()
    {
        var result = ReplayCapacityEstimator.Estimate(new("Simple", true, 300, 0, "x264", true, true, true));
        Assert.Equal(300, result.Seconds);
        Assert.Equal(210, result.HandoffSeconds);
        Assert.True(result.Verified);
    }

    [Fact]
    public void QualityEncodingUsesConservativeHandoffUntilBitrateIsMeasured()
    {
        var config = new ReplayConfiguration("Advanced", true, 300, 32, "obs_x264", true, true, true);
        var initial = ReplayCapacityEstimator.Estimate(config);
        var measured = ReplayCapacityEstimator.Estimate(config, 10_000_000);
        Assert.False(initial.Verified);
        Assert.True(measured.Verified);
        Assert.True(measured.Seconds > initial.Seconds);
        Assert.Equal(32 * 1024 * 1024 * 8 / 15_000_000.0, measured.Seconds, 6);
        Assert.True(measured.HandoffSeconds < measured.Seconds);
    }

    [Theory]
    [InlineData(20, 5)]
    [InlineData(10, 0)]
    public void ShortBuffersReserveAtLeastTheMeasuredSafetyFloor(double capacity, double threshold)
    {
        var result = ReplayCapacityEstimator.Estimate(new("Advanced", true, capacity, 0, "obs_x264", true, true, true));
        Assert.Equal(threshold, result.HandoffSeconds);
    }

    [Fact]
    public void UnknownOutputStaysUnverifiedAndHandsOffEarly()
    {
        var result = ReplayCapacityEstimator.Estimate(new("Unknown", true, 300, 0, "custom", false, true, false));
        Assert.False(result.Verified);
        Assert.Equal(30, result.HandoffSeconds);
    }
}
