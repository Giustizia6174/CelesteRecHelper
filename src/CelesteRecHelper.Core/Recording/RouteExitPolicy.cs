namespace CelesteRecHelper.Core.Recording;

public enum GameplayExitMode
{
    SaveAndQuit,
    GiveUp,
    Restart,
    GoldenBerryRestart,
    Completed,
    CompletedInterlude
}

public enum RouteExitDisposition
{
    Continue,
    End,
    Complete
}

/// <summary>Maps the game's level exit modes to route lifecycle actions.</summary>
public static class RouteExitPolicy
{
    public static RouteExitDisposition Decide(GameplayExitMode mode) => mode switch
    {
        GameplayExitMode.Restart => RouteExitDisposition.End,
        // Treat as a chapter restart for now; confirm this policy with the user.
        GameplayExitMode.GoldenBerryRestart => RouteExitDisposition.End,
        GameplayExitMode.Completed or GameplayExitMode.CompletedInterlude => RouteExitDisposition.Complete,
        GameplayExitMode.SaveAndQuit or GameplayExitMode.GiveUp => RouteExitDisposition.End,
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unknown gameplay exit mode.")
    };
}