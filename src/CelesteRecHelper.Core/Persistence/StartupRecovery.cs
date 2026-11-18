using System.Text.Json;
using CelesteRecHelper.Core.Models;

namespace CelesteRecHelper.Core.Persistence;

public sealed class RecoveryReport
{
    public List<Guid> InterruptedRoutes { get; } = [];
    public int RecoveredSaves { get; internal set; }
    public int FailedSaves { get; internal set; }
    public bool OutputsMayStillBeActive { get; internal set; }
    public ObsDirectoryRecoveryRecord? ObsDirectoryRecovery { get; internal set; }
    public List<BackgroundJobStatus> UnfinishedJobs { get; } = [];
    public List<string> Warnings { get; } = [];
}

/// <summary>Run once at game startup, before accepting new recording work.</summary>
public sealed class StartupRecovery(ManifestStore store)
{
    private static readonly HashSet<string> MediaExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".mkv", ".mp4", ".mov", ".flv", ".ts", ".m4v" };
    private static readonly StringComparer PathComparer = OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    public RecoveryReport Recover()
    {
        var report = new RecoveryReport();
        var routes = new List<RouteManifest>();
        foreach (var id in store.EnumerateRouteIds())
        {
            try { routes.Add(store.Load(id)); }
            catch (Exception e) when (IsDataError(e))
            {
                report.Warnings.Add($"Could not read route {id:N}: {e.Message}");
            }
        }

        var usedPaths = routes.SelectMany(r => r.Media).Select(m => Path.GetFullPath(m.FilePath))
            .ToHashSet(PathComparer);
        var pending = routes.SelectMany(r => r.SaveRequests)
            .Where(r => r.Status == SaveRequestStatus.Pending).ToList();
        // Resolve all candidates first. A file matching two requests is ambiguous, never assigned twice.
        var candidates = new Dictionary<ReplaySaveRequest, List<string>>(ReferenceEqualityComparer.Instance);
        foreach (var request in pending)
            candidates.Add(request, FindCandidates(request, usedPaths, report));
        var claims = candidates.Values.SelectMany(paths => paths).GroupBy(p => p, PathComparer)
            .ToDictionary(g => g.Key, g => g.Count(), PathComparer);

        foreach (var route in routes)
        {
            var changed = false;
            if (route.Status == RouteStatus.Recording)
            {
                route.Status = RouteStatus.Interrupted;
                report.InterruptedRoutes.Add(route.RouteId);
                report.OutputsMayStillBeActive |= route.ObsSessions.Any(s =>
                    s.EndedAtUtc is null && (s.ReplayBufferWasActive || s.RecordingWasActive));
                changed = true;
            }
            // A crashed life cannot be continued in v1. Confirmed segments remain intact.
            if (route.CurrentLife is not null)
            {
                route.CurrentLife = null;
                changed = true;
            }

            foreach (var request in route.SaveRequests.Where(r => r.Status == SaveRequestStatus.Pending))
            {
                var paths = candidates[request];
                if (paths.Count == 1 && claims[paths[0]] == 1)
                {
                    var media = new MediaAsset
                    {
                        ObsSessionId = request.ObsSessionId,
                        FilePath = paths[0],
                        Kind = MediaKind.ReplayBuffer,
                        SaveRequestId = request.Id,
                        SaveRequestedAtTimestamp = request.RequestedAtTimestamp,
                        RecoveredAfterCrash = true
                    };
                    route.Media.Add(media);
                    request.OutputPath = media.FilePath;
                    request.Status = SaveRequestStatus.Saved;
                    request.FailureReason = null;
                    foreach (var segment in route.Segments.Where(s => s.SaveRequestId == request.Id))
                    {
                        segment.Status = SegmentStatus.Saved;
                        segment.FailureReason = null;
                        segment.Media.Add(new MediaReference
                        {
                            MediaId = media.Id,
                            StartTimestamp = segment.StartTimestamp,
                            EndTimestamp = segment.EndTimestamp
                        });
                    }
                    report.RecoveredSaves++;
                }
                else
                {
                    request.Status = SaveRequestStatus.Failed;
                    request.FailureReason = paths.Count == 0
                        ? "No completed replay file was found after restart."
                        : "Replay file matching is ambiguous after restart.";
                    report.FailedSaves++;
                    report.Warnings.Add($"Route {route.RouteId:N}, save {request.Id:N}: {request.FailureReason}");
                }
                changed = true;
            }

            foreach (var segment in route.Segments.Where(s => s.Status == SegmentStatus.Recorded))
            {
                segment.Status = SegmentStatus.SaveFailed;
                segment.FailureReason = "The game stopped before this segment was saved.";
                changed = true;
            }
            if (changed)
            {
                try { store.Save(route); }
                catch (Exception e) when (IsDataError(e))
                {
                    report.Warnings.Add($"Could not persist recovery for route {route.RouteId:N}: {e.Message}");
                }
            }
        }

        try { report.ObsDirectoryRecovery = store.LoadObsRecovery(); }
        catch (Exception e) when (IsDataError(e))
        {
            report.Warnings.Add($"Could not read OBS directory recovery record: {e.Message}");
        }
        foreach (var path in store.EnumerateJobStatusPaths())
        {
            try
            {
                var status = AtomicJsonFile.Read<BackgroundJobStatus>(path);
                if (status.State != BackgroundJobState.Succeeded)
                    report.UnfinishedJobs.Add(status);
            }
            catch (Exception e) when (IsDataError(e))
            {
                report.Warnings.Add($"Could not read job status {path}: {e.Message}");
            }
        }
        return report;
    }

    private static List<string> FindCandidates(ReplaySaveRequest request, HashSet<string> usedPaths,
        RecoveryReport report)
    {
        try
        {
            if (!Directory.Exists(request.ExpectedDirectory))
                return [];
            var existing = request.ExistingFiles.Select(Path.GetFullPath).ToHashSet(PathComparer);
            return Directory.EnumerateFiles(request.ExpectedDirectory)
                .Where(path => MediaExtensions.Contains(Path.GetExtension(path)))
                .Select(path => new FileInfo(path))
                .Where(file => file.Length > 0 &&
                    file.LastWriteTimeUtc >= request.RequestedAtUtc.UtcDateTime.AddSeconds(-2) &&
                    file.LastWriteTimeUtc <= request.DeadlineUtc.UtcDateTime &&
                    !existing.Contains(file.FullName) && !usedPaths.Contains(file.FullName))
                .Select(file => file.FullName).ToList();
        }
        catch (Exception e) when (IsDataError(e))
        {
            report.Warnings.Add($"Could not search replay directory {request.ExpectedDirectory}: {e.Message}");
            return [];
        }
    }

    private static bool IsDataError(Exception e) =>
        e is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or ArgumentException or NotSupportedException;
}
