using CelesteRecHelper.Core.Models;
using CelesteRecHelper.Core.Obs;

namespace CelesteRecHelper.Core.Recording;

public enum EndSegmentStatus { Recorded, NoActiveLife, GameplayUnavailable, RouteClosed, EmptyInterval }
public sealed record ReplaySaveCommand(Guid SegmentId, Guid ObsSessionId);
public sealed record EndSegmentResult(EndSegmentStatus Status, ReplaySaveCommand? SaveCommand = null);

/// <summary>
/// Mutates one live route on the owning thread; it performs no OBS, file or game calls.
/// The caller persists each change before dispatching commands. Save callbacks use their own request clock.
/// </summary>
public sealed class SegmentStateMachine
{
    private readonly RouteManifest route;
    private readonly ObsSession session;
    private long lastGameplayTimestamp;
    public RouteManifest Route => route;

    public SegmentStateMachine(RouteManifest route, Guid obsSessionId)
    {
        ArgumentNullException.ThrowIfNull(route);
        this.route = route;
        session = route.ObsSessions.SingleOrDefault(s => s.Id == obsSessionId)
            ?? throw new ArgumentException("The OBS session must belong to this route.", nameof(obsSessionId));
        if (route.CurrentLife is { } life && life.ObsSessionId != obsSessionId)
            throw new ArgumentException("The current life belongs to another OBS session.", nameof(obsSessionId));
        lastGameplayTimestamp = route.CurrentLife?.PendingStartTimestamp ?? 0;
    }

    public bool OnGameplayStable(long timestamp)
    {
        AdvanceGameplayTime(timestamp);
        if (route.Status != RouteStatus.Recording || route.CurrentLife is not null) return false;
        route.CurrentLife = new CurrentLife
        {
            ObsSessionId = session.Id, StartedAtTimestamp = timestamp, PendingStartTimestamp = timestamp
        };
        return true;
    }

    public EndSegmentResult OnEndKey(long timestamp, bool gameplayAvailable)
    {
        AdvanceGameplayTime(timestamp);
        if (route.Status != RouteStatus.Recording) return new(EndSegmentStatus.RouteClosed);
        if (!gameplayAvailable) return new(EndSegmentStatus.GameplayUnavailable);
        return RecordEnd(timestamp);
    }

    public bool OnDeath(long timestamp)
    {
        AdvanceGameplayTime(timestamp);
        if (route.Status != RouteStatus.Recording || route.CurrentLife is null) return false;
        route.CurrentLife = null;
        return true;
    }

    /// <summary>The game adapter calls this after the configured results-screen tail has elapsed.</summary>
    public EndSegmentResult OnCompleted(long timestamp)
    {
        AdvanceGameplayTime(timestamp);
        if (route.Status != RouteStatus.Recording) return new(EndSegmentStatus.RouteClosed);
        var result = RecordEnd(timestamp);
        route.Status = RouteStatus.Completed;
        route.CurrentLife = null;
        return result;
    }

    /// <summary>All first-version non-completion exits end the route and discard its unconfirmed tail.</summary>
    public bool OnExit(long timestamp)
    {
        AdvanceGameplayTime(timestamp);
        if (route.Status != RouteStatus.Recording) return false;
        route.Status = RouteStatus.Ended;
        route.CurrentLife = null;
        // The scheduler must drain pending saves before stopping OBS, including after this method returns.
        return true;
    }

    /// <summary>Bind confirmed boundaries when the scheduler is ready to dispatch one actual OBS save.</summary>
    public void BeginSave(ReplaySaveRequest request, IReadOnlyList<Guid> segmentIds)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(segmentIds);
        if (request.Id == Guid.Empty || request.ObsSessionId != session.Id || request.Status != SaveRequestStatus.Pending ||
            request.RequestedAtTimestamp < 0 || request.DeadlineUtc < request.RequestedAtUtc || request.ExistingFiles is null ||
            !Path.IsPathFullyQualified(request.ExpectedDirectory) ||
            !PathComparer.Equals(Path.GetFullPath(request.ExpectedDirectory), Path.GetFullPath(session.OutputDirectory)))
            throw new ArgumentException("The replay request does not describe this session's output.", nameof(request));
        if (route.SaveRequests.Any(r => r.Id == request.Id || r.Status == SaveRequestStatus.Pending))
            throw new InvalidOperationException("A replay save is already pending, or the request ID was reused.");
        if (segmentIds.Count == 0 || segmentIds.Distinct().Count() != segmentIds.Count)
            throw new ArgumentException("Select at least one distinct segment.", nameof(segmentIds));
        var segments = segmentIds.Select(id => route.Segments.SingleOrDefault(s => s.Id == id)
            ?? throw new ArgumentException("A selected segment does not belong to this route.", nameof(segmentIds))).ToArray();
        if (segments.Any(s => s.ObsSessionId != session.Id || s.Status != SegmentStatus.Recorded ||
            s.SaveRequestId is not null || s.EndTimestamp > request.RequestedAtTimestamp))
            throw new InvalidOperationException("Only unassigned recorded segments ending before the save request can be bound.");
        route.SaveRequests.Add(request);
        foreach (var segment in segments) segment.SaveRequestId = request.Id;
    }

    /// <summary>Accept only the file-backed result validated by ObsCaptureSession, associated by the scheduler.</summary>
    public bool OnSaveSucceeded(Guid requestId, SavedReplay replay)
    {
        ArgumentNullException.ThrowIfNull(replay);
        var request = GetRequest(requestId);
        if (request.Status != SaveRequestStatus.Pending) return false;
        if (!Path.IsPathFullyQualified(replay.FilePath) || replay.FileSizeBytes <= 0 ||
            replay.RequestedAtTimestamp < request.RequestedAtTimestamp || replay.EventAtTimestamp < replay.RequestedAtTimestamp ||
            replay.RequestedAtUtc < request.RequestedAtUtc || replay.EventAtUtc < replay.RequestedAtUtc ||
            replay.EventAtUtc > request.DeadlineUtc)
            throw new ArgumentException("The replay result has invalid file or request timing data.", nameof(replay));
        var path = Path.GetFullPath(replay.FilePath);
        var directory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(request.ExpectedDirectory)) + Path.DirectorySeparatorChar;
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!path.StartsWith(directory, comparison) || request.ExistingFiles.Any(p => PathComparer.Equals(Path.GetFullPath(p), path)) ||
            route.Media.Any(m => PathComparer.Equals(Path.GetFullPath(m.FilePath), path)))
            throw new ArgumentException("The replay result must be a new file inside the expected directory.", nameof(replay));
        var media = new MediaAsset
        {
            ObsSessionId = session.Id, FilePath = path, Kind = MediaKind.ReplayBuffer, SaveRequestId = request.Id,
            SaveRequestedAtTimestamp = replay.RequestedAtTimestamp, SaveEventAtTimestamp = replay.EventAtTimestamp,
            SaveEventAtUtc = replay.EventAtUtc
        };
        route.Media.Add(media);
        request.Status = SaveRequestStatus.Saved;
        request.OutputPath = path;
        request.FailureReason = null;
        foreach (var segment in route.Segments.Where(s => s.SaveRequestId == request.Id))
        {
            segment.Status = SegmentStatus.Saved;
            segment.FailureReason = null;
            segment.Media.Add(new MediaReference
            {
                MediaId = media.Id, StartTimestamp = segment.StartTimestamp, EndTimestamp = segment.EndTimestamp
            });
        }
        return true;
    }

    public bool OnSaveFailed(Guid requestId, string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        var request = GetRequest(requestId);
        if (request.Status != SaveRequestStatus.Pending) return false;
        request.Status = SaveRequestStatus.Failed;
        request.FailureReason = reason;
        foreach (var segment in route.Segments.Where(s => s.SaveRequestId == request.Id))
        {
            segment.Status = SegmentStatus.SaveFailed;
            segment.FailureReason = reason;
        }
        return true;
    }

    private EndSegmentResult RecordEnd(long timestamp)
    {
        if (route.CurrentLife is not { } life) return new(EndSegmentStatus.NoActiveLife);
        if (timestamp == life.PendingStartTimestamp) return new(EndSegmentStatus.EmptyInterval);
        var segment = new LogicalSegment
        {
            LifeId = life.Id, ObsSessionId = session.Id, StartTimestamp = life.PendingStartTimestamp, EndTimestamp = timestamp
        };
        route.Segments.Add(segment);
        life.PendingStartTimestamp = timestamp;
        return new(EndSegmentStatus.Recorded, new ReplaySaveCommand(segment.Id, session.Id));
    }

    private ReplaySaveRequest GetRequest(Guid requestId) =>
        route.SaveRequests.SingleOrDefault(r => r.Id == requestId && r.ObsSessionId == session.Id)
        ?? throw new ArgumentException("The replay request does not belong to this session.", nameof(requestId));

    private void AdvanceGameplayTime(long timestamp)
    {
        if (timestamp < lastGameplayTimestamp)
            throw new ArgumentOutOfRangeException(nameof(timestamp), "Gameplay timestamps must be monotonic within this session.");
        lastGameplayTimestamp = timestamp;
    }

    private static StringComparer PathComparer => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
}
