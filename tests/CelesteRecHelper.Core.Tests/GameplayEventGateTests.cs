using CelesteRecHelper.Core.Recording;

namespace CelesteRecHelper.Core.Tests;

public sealed class GameplayEventGateTests
{
    [Fact]
    public void EndKeyRequiresANewPressAndSuppressesShortRepeatedPresses()
    {
        var gate = new GameplayEventGate(1000);
        Assert.Equal(new GameplayFrameActions(true, false, false), gate.Observe(1000, true, false));
        Assert.True(gate.Observe(1001, true, true).ConfirmEnd);
        Assert.False(gate.Observe(1010, true, true).ConfirmEnd);
        gate.Observe(1020, true, false);
        Assert.Equal(new GameplayFrameActions(false, false, true), gate.Observe(1050, true, true));
        gate.Observe(1199, true, false);
        Assert.True(gate.Observe(1200, true, true).ConfirmEnd);
    }

    [Fact]
    public void APressWhilePausedDoesNotTurnIntoAnEndPointOnUnpause()
    {
        var gate = new GameplayEventGate(1000);
        Assert.Equal(new GameplayFrameActions(false, false, true), gate.Observe(1000, false, true));
        Assert.Equal(new GameplayFrameActions(true, false, false), gate.Observe(1100, true, true));
        gate.Observe(1200, true, false);
        Assert.True(gate.Observe(1300, true, true).ConfirmEnd);
    }

    [Fact]
    public void DeathRequiresANewStableFrameBeforeRecordingAnotherEndPoint()
    {
        var gate = new GameplayEventGate(1000);
        gate.Observe(100, true, false);
        Assert.True(gate.OnDeath());
        Assert.False(gate.OnDeath());
        Assert.Equal(new GameplayFrameActions(false, false, true), gate.Observe(200, false, true));
        gate.Observe(300, false, false);
        Assert.Equal(new GameplayFrameActions(true, true, false), gate.Observe(400, true, true));
    }

    [Fact]
    public void DeadCompletionIsIgnoredAndAcceptedCompletionIsDeduplicated()
    {
        var gate = new GameplayEventGate(1000);
        gate.Observe(100, true, false);
        gate.OnDeath();
        Assert.False(gate.TryBeginCompletion(200, false));
        Assert.False(gate.Completing);
        gate.Observe(400, true, false);
        Assert.True(gate.TryBeginCompletion(500, true));
        Assert.False(gate.TryBeginCompletion(600, true));
        Assert.False(gate.Observe(700, true, true).ConfirmEnd);
        Assert.False(gate.TryFinishCompletion(3499));
        Assert.True(gate.TryFinishCompletion(3500));
        Assert.False(gate.TryFinishCompletion(3501));
        Assert.True(gate.Closed);
    }

    [Fact]
    public void AcceptedCompletionSurvivesPlayerRemovalAndTheResultsScene()
    {
        var gate = new GameplayEventGate(1000);
        Assert.True(gate.TryBeginCompletion(100, true));
        Assert.False(gate.OnDeath());
        gate.OnExit(completionExit: true);
        Assert.True(gate.Completing);
        Assert.False(gate.Closed);
        Assert.True(gate.TryFinishCompletion(3100));
    }

    [Fact]
    public void NormalExitCancelsTheOldCompletionAndCannotCompleteAnotherRoute()
    {
        var oldRoute = new GameplayEventGate(1000);
        oldRoute.TryBeginCompletion(100, true);
        oldRoute.OnExit(completionExit: false);
        var nextRoute = new GameplayEventGate(1000);
        nextRoute.Observe(4000, true, false);
        Assert.False(oldRoute.TryFinishCompletion(4000));
        Assert.False(nextRoute.TryFinishCompletion(4000));
        Assert.True(nextRoute.HasLife);
        Assert.False(nextRoute.Closed);
    }

    [Fact]
    public void CompletionCanEndAnEmptyRouteWithoutInventingAnActiveLife()
    {
        var gate = new GameplayEventGate(1000);
        Assert.True(gate.TryBeginCompletion(100, true));
        Assert.False(gate.HasLife);
        Assert.True(gate.TryFinishCompletion(3100));
        Assert.False(gate.HasLife);
    }
}
