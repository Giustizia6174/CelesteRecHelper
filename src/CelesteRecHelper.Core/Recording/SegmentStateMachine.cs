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
    public Guid ObsSessionId => session.Id;

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

    /// <summary>Builds an immutable worker request for the replay file attached to a saved OBS request.</summary>
    public SegmentCutRequest CreateCutRequest(Guid requestId)
    {
        var request = GetRequest(requestId);
        if (request.Status != SaveRequestStatus.Saved || string.IsNullOrWhiteSpace(request.OutputPath))
            throw new InvalidOperationException("Only a completed replay save can be cut.");
        var source = route.Media.SingleOrDefault(media => media.SaveRequestId == request.Id &&
            PathComparer.Equals(Path.GetFullPath(media.FilePath), Path.GetFullPath(request.OutputPath)))
            ?? throw new InvalidOperationException("The saved replay source is missing from the manifest.");
        var segments = route.Segments.Where(segment => segment.SaveRequestId == request.Id &&
                segment.Status == SegmentStatus.Saved && segment.SliceStatus != SegmentSliceStatus.Succeeded)
            .Select(segment => new SegmentCutItem
            {
                SegmentId = segment.Id,
                Sequence = route.Segments.IndexOf(segment) + 1,
                StartTimestamp = segment.StartTimestamp,
                EndTimestamp = segment.EndTimestamp
            }).ToList();
        if (segments.Count == 0) throw new InvalidOperationException("The saved replay has no pending segments to cut.");
        var frequency = session.Clock.Frequency;
        if (frequency <= 0) throw new InvalidOperationException("The OBS session clock frequency is invalid.");
        return new SegmentCutRequest
        {
            RouteId = route.RouteId, SaveRequestId = request.Id, SourceMediaId = source.Id,
            ObsSessionId = session.Id, SourcePath = source.FilePath, OutputDirectory = session.OutputDirectory,
            MapName = route.MapName, ClockFrequency = frequency,
            SaveRequestedAtTimestamp = request.RequestedAtTimestamp, Segments = segments
        };
    }

    public void OnCutFailed(Guid requestId, string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        var request = GetRequest(requestId);
        foreach (var segment in route.Segments.Where(segment => segment.SaveRequestId == request.Id &&
            segment.Status == SegmentStatus.Saved && segment.SliceStatus != SegmentSliceStatus.Succeeded))
        {
            segment.SliceStatus = SegmentSliceStatus.Failed;
            segment.SliceFailureReason = reason;
        }
    }

    /// <summary>Applies only verified worker outputs. The caller deletes SourcePathToDelete after persisting this state.</summary>
    public SegmentCutApplyResult ApplyCutResult(SegmentCutResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (result.RouteId != route.RouteId || result.ObsSessionId != session.Id)
            throw new ArgumentException("The cut result does not belong to this route or OBS session.", nameof(result));
        var request = GetRequest(result.SaveRequestId);
        if (request.Status != SaveRequestStatus.Saved || string.IsNullOrWhiteSpace(request.OutputPath) ||
            !PathComparer.Equals(Path.GetFullPath(request.OutputPath), Path.GetFullPath(result.SourcePath)))
            throw new ArgumentException("The cut result does not match the saved replay request.", nameof(result));
        var source = route.Media.SingleOrDefault(media => media.Id == result.SourceMediaId)
            ?? throw new ArgumentException("The cut source is not registered in the route.", nameof(result));
        if (source.Kind != MediaKind.ReplayBuffer || source.SaveRequestId != request.Id ||
            !PathComparer.Equals(Path.GetFullPath(source.FilePath), Path.GetFullPath(result.SourcePath)))
            throw new ArgumentException("The cut source path does not match the registered media.", nameof(result));

        var expected = route.Segments.Where(segment => segment.SaveRequestId == request.Id &&
            segment.SliceStatus != SegmentSliceStatus.Succeeded).Select(segment => segment.Id).ToHashSet();
        if (result.Segments.Select(item => item.SegmentId).Distinct().Count() != result.Segments.Count ||
            !expected.IsSubsetOf(result.Segments.Select(item => item.SegmentId)))
            throw new ArgumentException("The cut result is missing segments or contains duplicates.", nameof(result));
        // Validate the whole batch before changing any references.
        foreach (var item in result.Segments)
        {
            var segment = route.Segments.SingleOrDefault(candidate => candidate.Id == item.SegmentId);
            if (segment is null || segment.SaveRequestId != request.Id || segment.ObsSessionId != session.Id ||
                item.Sequence != route.Segments.IndexOf(segment) + 1)
                throw new ArgumentException("The cut result contains a segment from another request.", nameof(result));
            if (segment.SliceStatus == SegmentSliceStatus.Succeeded || !item.Succeeded) continue;
            if (string.IsNullOrWhiteSpace(item.FilePath) || !Path.IsPathFullyQualified(item.FilePath) ||
                PathComparer.Equals(Path.GetFullPath(item.FilePath), Path.GetFullPath(source.FilePath)) ||
                item.Probe is null || item.Probe.FileSizeBytes <= 0 || item.Probe.DurationSeconds <= 0 ||
                !PathComparer.Equals(Path.GetFullPath(item.Probe.FilePath), Path.GetFullPath(item.FilePath)) ||
                item.StartSeconds is not { } start || item.EndSeconds is not { } end ||
                !double.IsFinite(start) || !double.IsFinite(end) || start < 0 || end <= start ||
                end > item.Probe.DurationSeconds + 0.1)
                throw new ArgumentException("A successful cut result must identify a valid, probed output and exact bounds.", nameof(result));
        }
        foreach (var item in result.Segments)
        {
            var segment = route.Segments.Single(candidate => candidate.Id == item.SegmentId);
            if (segment.SliceStatus == SegmentSliceStatus.Succeeded) continue;
            if (!item.Succeeded)
            {
                segment.SliceStatus = SegmentSliceStatus.Failed;
                segment.SliceFailureReason = string.IsNullOrWhiteSpace(item.Error) ? "The media worker could not cut this segment." : item.Error;
                continue;
            }
            var output = Path.GetFullPath(item.FilePath!);
            var existing = route.Media.SingleOrDefault(media => PathComparer.Equals(Path.GetFullPath(media.FilePath), output));
            if (existing is null)
            {
                existing = new MediaAsset
                {
                    ObsSessionId = session.Id, FilePath = output, Kind = MediaKind.SegmentClip,
                    SaveRequestId = request.Id, Probe = item.Probe
                };
                route.Media.Add(existing);
            }
            else if (existing.Kind != MediaKind.SegmentClip || existing.ObsSessionId != session.Id)
                throw new ArgumentException("A cut output path is already used by incompatible media.", nameof(result));
            segment.Media.RemoveAll(reference => reference.MediaId == source.Id);
            segment.Media.RemoveAll(reference => reference.MediaId == existing.Id);
            segment.Media.Add(new MediaReference
            {
                MediaId = existing.Id, StartTimestamp = segment.StartTimestamp, EndTimestamp = segment.EndTimestamp,
                StartSeconds = item.StartSeconds, EndSeconds = item.EndSeconds
            });
            segment.SliceStatus = SegmentSliceStatus.Succeeded;
            segment.SliceFailureReason = null;
        }

        var sourceStillReferenced = route.Segments.Any(segment => segment.Media.Any(reference => reference.MediaId == source.Id)) ||
            route.CurrentLife?.PendingMedia.Any(reference => reference.MediaId == source.Id) == true;
        var sourceSegments = route.Segments.Where(segment => segment.SaveRequestId == request.Id).ToArray();
        var allSourceSegmentsCut = sourceSegments.Length > 0 && sourceSegments.All(segment =>
            segment.SliceStatus == SegmentSliceStatus.Succeeded);
        return !sourceStillReferenced && allSourceSegmentsCut
            ? new SegmentCutApplyResult(source.FilePath, source.Id)
            : new SegmentCutApplyResult(null, null);
    }

    public void RemoveMedia(Guid mediaId)
    {
        if (route.Segments.Any(segment => segment.Media.Any(reference => reference.MediaId == mediaId)) ||
            route.CurrentLife?.PendingMedia.Any(reference => reference.MediaId == mediaId) == true)
            throw new InvalidOperationException("Cannot remove media that is still referenced by the route.");
        route.Media.RemoveAll(media => media.Id == mediaId);
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
