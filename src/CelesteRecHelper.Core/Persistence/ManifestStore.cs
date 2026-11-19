using CelesteRecHelper.Core.Models;

namespace CelesteRecHelper.Core.Persistence;

/// <summary>The game is the manifest writer; workers read immutable job snapshots later.</summary>
public sealed class ManifestStore
{
    public string RootDirectory { get; }
    public string RoutesDirectory => Path.Combine(RootDirectory, "routes");
    public string JobsDirectory => Path.Combine(RootDirectory, "jobs");
    public string ObsRecoveryPath => Path.Combine(RootDirectory, "obs-directory-recovery.json");

    public ManifestStore(string rootDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        RootDirectory = Path.GetFullPath(rootDirectory);
    }

    public string GetManifestPath(Guid routeId) =>
        Path.Combine(RoutesDirectory, routeId.ToString("N"), "manifest.json");

    public RouteManifest Load(Guid routeId)
    {
        var route = AtomicJsonFile.Read<RouteManifest>(GetManifestPath(routeId));
        Validate(route);
        if (route.RouteId != routeId)
            throw new InvalidDataException("The manifest route ID does not match its directory.");
        return route;
    }

    public void Save(RouteManifest route)
    {
        Validate(route);
        route.UpdatedAtUtc = DateTimeOffset.UtcNow;
        AtomicJsonFile.Write(GetManifestPath(route.RouteId), route);
    }

    /// <summary>Delete a closed route only when its OBS sessions stopped and it has no recoverable work or media.</summary>
    public bool DeleteIfEmptyClosedRoute(Guid routeId)
    {
        var manifestPath = GetManifestPath(routeId);
        if (!File.Exists(manifestPath)) return false;

        var route = Load(routeId);
        if (route.Status is not (RouteStatus.Ended or RouteStatus.Completed) || route.CurrentLife is not null ||
            route.Segments.Count != 0 || route.SaveRequests.Count != 0 || route.Media.Count != 0 ||
            route.ObsSessions.Count == 0 || route.ObsSessions.Any(session =>
                session.ReplayBufferWasActive || session.RecordingWasActive || session.EndedAtUtc is null))
            return false;

        Directory.Delete(Path.GetDirectoryName(manifestPath)!, recursive: true);
        return true;
    }

    public IEnumerable<Guid> EnumerateRouteIds()
    {
        if (!Directory.Exists(RoutesDirectory))
            yield break;
        foreach (var directory in Directory.EnumerateDirectories(RoutesDirectory).Order(StringComparer.Ordinal))
            if (Guid.TryParseExact(Path.GetFileName(directory), "N", out var routeId) &&
                File.Exists(Path.Combine(directory, "manifest.json")))
                yield return routeId;
    }

    public void SaveObsRecovery(ObsDirectoryRecoveryRecord record) => AtomicJsonFile.Write(ObsRecoveryPath, record);
    public ObsDirectoryRecoveryRecord? LoadObsRecovery() => File.Exists(ObsRecoveryPath)
        ? AtomicJsonFile.Read<ObsDirectoryRecoveryRecord>(ObsRecoveryPath) : null;
    public void ClearObsRecovery() => File.Delete(ObsRecoveryPath);

    public string GetJobDirectory(Guid jobId) => Path.Combine(JobsDirectory, jobId.ToString("N"));
    public string GetJobRequestPath(Guid jobId) => Path.Combine(GetJobDirectory(jobId), "request.json");
    public string GetJobResultPath(Guid jobId) => Path.Combine(GetJobDirectory(jobId), "result.json");

    public void SaveJobStatus(BackgroundJobStatus status) =>
        AtomicJsonFile.Write(Path.Combine(GetJobDirectory(status.JobId), "status.json"), status);

    public IEnumerable<string> EnumerateJobStatusPaths() => Directory.Exists(JobsDirectory)
        ? Directory.EnumerateFiles(JobsDirectory, "status.json", SearchOption.AllDirectories)
        : [];

    private static void Validate(RouteManifest route)
    {
        ArgumentNullException.ThrowIfNull(route);
        if (route.SchemaVersion != RouteManifest.CurrentSchemaVersion)
            throw new InvalidDataException($"Unsupported manifest schema: {route.SchemaVersion}.");
        if (route.RouteId == Guid.Empty || string.IsNullOrWhiteSpace(route.MapSid))
            throw new InvalidDataException("A route must have a non-empty ID and map SID.");
        if (route.ObsSessions is null || route.Segments is null || route.Media is null || route.SaveRequests is null)
            throw new InvalidDataException("Manifest collections cannot be null.");
        if (route.ObsSessions.Any(s => s is null || s.Clock is null || s.Clock.Frequency <= 0) ||
            route.Segments.Any(s => s is null || s.Media is null || s.EndTimestamp < s.StartTimestamp) ||
            route.Media.Any(m => m is null || !Path.IsPathFullyQualified(m.FilePath)) ||
            route.SaveRequests.Any(r => r is null || r.ExistingFiles is null ||
                !Path.IsPathFullyQualified(r.ExpectedDirectory) || r.DeadlineUtc < r.RequestedAtUtc) ||
            route.CurrentLife is { PendingMedia: null })
            throw new InvalidDataException("The manifest contains invalid clock, interval, path, or collection data.");

        var sessionIds = route.ObsSessions.Select(s => s.Id).ToHashSet();
        var mediaById = route.Media.GroupBy(m => m.Id).ToDictionary(g => g.Key, g => g.First());
        var requestIds = route.SaveRequests.Select(r => r.Id).ToHashSet();
        if (sessionIds.Count != route.ObsSessions.Count || mediaById.Count != route.Media.Count ||
            requestIds.Count != route.SaveRequests.Count ||
            route.Segments.Select(s => s.Id).Distinct().Count() != route.Segments.Count ||
            sessionIds.Contains(Guid.Empty) || mediaById.ContainsKey(Guid.Empty) || requestIds.Contains(Guid.Empty))
            throw new InvalidDataException("Manifest identifiers must be non-empty and unique.");

        if (route.Media.Any(m => !sessionIds.Contains(m.ObsSessionId) ||
                (m.SaveRequestId is { } requestId && !requestIds.Contains(requestId))) ||
            route.SaveRequests.Any(r => !sessionIds.Contains(r.ObsSessionId)) ||
            route.Segments.Any(s => s.Id == Guid.Empty || s.LifeId == Guid.Empty ||
                !sessionIds.Contains(s.ObsSessionId) ||
                (s.SaveRequestId is { } requestId && !requestIds.Contains(requestId)) ||
                !ValidReferences(s.Media, s.ObsSessionId)) ||
            (route.CurrentLife is { } life && (life.Id == Guid.Empty || !sessionIds.Contains(life.ObsSessionId) ||
                life.PendingStartTimestamp < life.StartedAtTimestamp || !ValidReferences(life.PendingMedia, life.ObsSessionId))))
            throw new InvalidDataException("A manifest interval or reference does not belong to the recorded session.");

        bool ValidReferences(List<MediaReference> references, Guid sessionId) => references.All(reference =>
            reference is not null && reference.EndTimestamp >= reference.StartTimestamp &&
            ((reference.StartSeconds is null && reference.EndSeconds is null) ||
                (reference.StartSeconds is { } start && reference.EndSeconds is { } end &&
                    double.IsFinite(start) && double.IsFinite(end) && start >= 0 && end > start)) &&
            mediaById.TryGetValue(reference.MediaId, out var media) && media.ObsSessionId == sessionId);
    }
}
