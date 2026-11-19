namespace CelesteRecHelper.Core.Recording;

public sealed record GameplayFrameActions(bool StartLife, bool ConfirmEnd, bool EndIgnored);

/// <summary>Filters game input and completion timing for one route; independent of Everest and disk I/O.</summary>
public sealed class GameplayEventGate
{
    public const double EndDebounceSeconds = 0.15;
    public const double CompletionTailSeconds = 3;
    private readonly long debounceTicks;
    private readonly long tailTicks;
    private long? lastEnd;
    private long? completionDue;
    private bool wasPressed;
    public bool HasLife { get; private set; }
    public bool Completing => completionDue is not null;
    public bool Closed { get; private set; }

    public GameplayEventGate(long clockFrequency)
    {
        if (clockFrequency <= 0) throw new ArgumentOutOfRangeException(nameof(clockFrequency));
        debounceTicks = (long)Math.Ceiling(clockFrequency * EndDebounceSeconds);
        tailTicks = (long)Math.Ceiling(clockFrequency * CompletionTailSeconds);
    }

    public GameplayFrameActions Observe(long timestamp, bool gameplayStable, bool endPressed)
    {
        var edge = endPressed && !wasPressed;
        wasPressed = endPressed;
        if (Closed || Completing) return new(false, false, edge);
        var start = gameplayStable && !HasLife;
        if (start) HasLife = true;
        var confirm = edge && gameplayStable && HasLife &&
            (lastEnd is null || timestamp - lastEnd.Value >= debounceTicks);
        if (confirm) lastEnd = timestamp;
        return new(start, confirm, edge && !confirm);
    }

    public bool OnDeath()
    {
        if (Closed || Completing || !HasLife) return false;
        HasLife = false;
        return true;
    }

    public bool TryBeginCompletion(long timestamp, bool playerAlive)
    {
        if (Closed || Completing || !playerAlive) return false;
        completionDue = checked(timestamp + tailTicks);
        return true;
    }

    /// <summary>Once accepted while alive, completion survives player removal and the completion exit scene.</summary>
    public bool TryFinishCompletion(long timestamp)
    {
        if (Closed || completionDue is not { } due || timestamp < due) return false;
        completionDue = null;
        Closed = true;
        HasLife = false;
        return true;
    }

    public void OnExit(bool completionExit)
    {
        if (completionExit && Completing) return;
        completionDue = null;
        HasLife = false;
        Closed = true;
    }
}
