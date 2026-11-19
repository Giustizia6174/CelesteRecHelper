using System.Text.Json;
using CelesteRecHelper.Core.Models;
using CelesteRecHelper.Core.Recording;

namespace CelesteRecHelper.Core.Persistence;

public sealed class SegmentCutRecoveryReport
{
    public int SucceededSegments { get; internal set; }
    public int FailedSegments { get; internal set; }
    public int RemovedReplays { get; internal set; }
    public List<string> Warnings { get; } = [];
}

/// <summary>Run after StartupRecovery and before new routes. Manifests are the source of truth,
/// including a crash between saving an OBS result, creating a job, and applying its result.</summary>
public sealed class SegmentCutRecovery(ManifestStore store)
{
    public async Task<SegmentCutRecoveryReport> RecoverAsync(
        Func<SegmentCutRequest, CancellationToken, Task<SegmentCutResult>> cut, CancellationToken ct = default)
    {
        var report = new SegmentCutRecoveryReport();
        foreach (var id in store.EnumerateRouteIds())
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var route = store.Load(id);
                foreach (var session in route.ObsSessions)
                {
                    var machine = new SegmentStateMachine(route, session.Id);
                    foreach (var request in route.SaveRequests.Where(request => request.ObsSessionId == session.Id &&
                        request.Status == SaveRequestStatus.Saved && route.Segments.Any(segment =>
                            segment.SaveRequestId == request.Id && segment.Status == SegmentStatus.Saved &&
                            segment.SliceStatus != SegmentSliceStatus.Succeeded)))
                    {
                        ct.ThrowIfCancellationRequested();
                        try
                        {
                            var input = machine.CreateCutRequest(request.Id);
                            if (!File.Exists(input.SourcePath))
                                throw new FileNotFoundException("The replay source required for cut recovery is missing.", input.SourcePath);
                            machine.ApplyCutResult(await cut(input, ct).ConfigureAwait(false));
                        }
                        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                        catch (Exception e)
                        {
                            machine.OnCutFailed(request.Id, e.Message);
                            report.Warnings.Add($"Route {id:N}, cut {request.Id:N}: {e.Message}");
                        }
                        store.Save(route);
                        var segments = route.Segments.Where(segment => segment.SaveRequestId == request.Id).ToArray();
                        report.SucceededSegments += segments.Count(segment => segment.SliceStatus == SegmentSliceStatus.Succeeded);
                        report.FailedSegments += segments.Count(segment => segment.SliceStatus == SegmentSliceStatus.Failed);
                    }
                    // Also completes a crash after reference publication but before source deletion/removal.
                    report.RemovedReplays += SegmentCutFiles.RemoveUnusedReplays(machine, store.Save);
                }
            }
            catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or
                ArgumentException or InvalidOperationException)
            {
                report.Warnings.Add($"Could not recover cuts for route {id:N}: {e.Message}");
            }
        }
        return report;
    }
}
