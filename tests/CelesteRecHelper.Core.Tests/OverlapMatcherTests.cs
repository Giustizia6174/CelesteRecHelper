using CelesteRecHelper.Core.Media;
using CelesteRecHelper.Core.Models;

namespace CelesteRecHelper.Core.Tests;

public sealed class OverlapMatcherTests
{
    private static VideoFrameHash[] Frames(int first, int count, double timeOffset = 0) =>
        Enumerable.Range(first, count).Select((value, i) => new VideoFrameHash(timeOffset + i / 20.0, value.ToString())).ToArray();

    [Fact]
    public void FindsAUniqueContinuousOverlapAndTheSameSeamInBothFiles()
    {
        var result = OverlapMatcher.Match(Frames(0, 100), Frames(60, 100, 0.1));
        Assert.Equal(OverlapStatus.Verified, result.Status);
        Assert.Equal(40, result.MatchedFrames);
        Assert.Equal(40, result.DistinctFrames);
        Assert.Equal(1.95, result.OverlapSeconds, 6);
        Assert.Equal(2.9, result.ReplaySeamSeconds!.Value - result.RecordingSeamSeconds!.Value, 6);
    }

    [Fact]
    public void DisjointFootageDoesNotClaimContinuity()
    {
        Assert.Equal(OverlapStatus.Inconclusive, OverlapMatcher.Match(Frames(0, 40), Frames(41, 40)).Status);
    }

    [Fact]
    public void StaticVideoCannotProveAnOverlap()
    {
        var frames = Enumerable.Range(0, 100).Select(i => new VideoFrameHash(i / 20.0, "black")).ToArray();
        Assert.Equal(OverlapStatus.Inconclusive, OverlapMatcher.Match(frames, frames).Status);
    }

    [Fact]
    public void RepeatingVideoHasAnAmbiguousAlignment()
    {
        var frames = Enumerable.Range(0, 100).Select(i => new VideoFrameHash(i / 20.0, (i % 20).ToString())).ToArray();
        Assert.Equal(OverlapStatus.Inconclusive, OverlapMatcher.Match(frames, frames).Status);
    }

    [Fact]
    public void ASingleMatchingFrameIsInsufficient()
    {
        Assert.Equal(OverlapStatus.Inconclusive, OverlapMatcher.Match(Frames(0, 20), Frames(19, 20)).Status);
    }

    [Fact]
    public void MatchingImagesWithInconsistentFrameTimingAreInsufficient()
    {
        var source = Frames(0, 30);
        var stretched = source.Select(f => f with { Seconds = f.Seconds * 2 }).ToArray();
        Assert.Equal(OverlapStatus.Inconclusive, OverlapMatcher.Match(source, stretched).Status);
    }

    [Fact]
    public void EmptyFootageIsInconclusive()
    {
        Assert.Equal(OverlapStatus.Inconclusive, OverlapMatcher.Match([], []).Status);
    }
}
