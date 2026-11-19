using System.Text.Json;
using CelesteRecHelper.Core.Models;

namespace CelesteRecHelper.Core.Persistence;

/// <summary>Serializes immutable cut jobs while allowing OBS saves to continue independently.</summary>
public sealed class SegmentCutJobRunner(ManifestStore store, Func<string, string, CancellationToken, Task> run)
{
    private readonly SemaphoreSlim serial = new(1, 1);

    public async Task<SegmentCutResult> CutAsync(SegmentCutRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await serial.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var jobId = FindJob(request) ?? Guid.NewGuid();
            var requestPath = store.GetJobRequestPath(jobId);
            var resultPath = store.GetJobResultPath(jobId);
            AtomicJsonFile.Write(requestPath, request);
            void Status(BackgroundJobState state, string? error = null) => store.SaveJobStatus(new BackgroundJobStatus
            {
                JobId = jobId, RouteId = request.RouteId, State = state, Kind = "segment-cut",
                RequestPath = requestPath, ResultPath = resultPath, Error = error
            });
            Status(BackgroundJobState.Pending);
            try
            {
                Status(BackgroundJobState.Running);
                await run(requestPath, resultPath, ct).ConfigureAwait(false);
                var result = AtomicJsonFile.Read<SegmentCutResult>(resultPath);
                if (result.RouteId != request.RouteId || result.SaveRequestId != request.SaveRequestId ||
                    result.SourceMediaId != request.SourceMediaId || result.ObsSessionId != request.ObsSessionId ||
                    !Path.GetFullPath(result.SourcePath).Equals(Path.GetFullPath(request.SourcePath),
                        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal) ||
                    !result.Segments.Select(item => item.SegmentId).Order().SequenceEqual(request.Segments.Select(item => item.SegmentId).Order()))
                    throw new InvalidDataException("The worker returned a result for a different cut job.");
                var failures = result.Segments.Where(item => !item.Succeeded).ToArray();
                Status(failures.Length == 0 ? BackgroundJobState.Succeeded : BackgroundJobState.Failed,
                    failures.Length == 0 ? null : string.Join("; ", failures.Select(item => item.Error)));
                return result;
            }
            catch (Exception e)
            {
                try { Status(BackgroundJobState.Failed, e.Message); }
                catch (Exception statusError) when (statusError is IOException or UnauthorizedAccessException) { }
                throw;
            }
        }
        finally { serial.Release(); }
    }

    private Guid? FindJob(SegmentCutRequest request)
    {
        foreach (var path in store.EnumerateJobStatusPaths())
        {
            try
            {
                var status = AtomicJsonFile.Read<BackgroundJobStatus>(path);
                var snapshot = store.GetJobRequestPath(status.JobId);
                if (status.Kind != "segment-cut" || status.RouteId != request.RouteId || !File.Exists(snapshot)) continue;
                var existing = AtomicJsonFile.Read<SegmentCutRequest>(snapshot);
                if (existing.SaveRequestId == request.SaveRequestId && existing.SourceMediaId == request.SourceMediaId)
                    return status.JobId;
            }
            catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or ArgumentException or NotSupportedException) { }
        }
        return null;
    }
}
