using System.Diagnostics;
using CelesteRecHelper.Core.Models;
using CelesteRecHelper.Core.Persistence;

namespace CelesteRecHelper.Core.Obs;

public static class ObsHandoffTest
{
    public const double DefaultOverlapSeconds = 2;

    public static async Task<HandoffCapture> RunAsync(ObsApi api, ManifestStore store, string endpoint,
        string? customDirectory = null, double overlapSeconds = DefaultOverlapSeconds, CancellationToken ct = default)
    {
        if (!double.IsFinite(overlapSeconds) || overlapSeconds is < 0.1 or > 10)
            throw new ArgumentOutOfRangeException(nameof(overlapSeconds));
        var id = Guid.NewGuid();
        var folder = Path.Combine(store.RootDirectory, "diagnostics", "handoff", id.ToString("N"));
        var capture = new ObsCaptureSession(api, store);
        ObsRecordingSession? recording = null;
        HandoffCapture result;
        Exception? failure = null;
        try
        {
            var configuration = await capture.StartAsync(endpoint, customDirectory, ct).ConfigureAwait(false);
            AtomicJsonFile.Write(Path.Combine(folder, "capture-state.json"), new { State = "BufferStarted", Configuration = configuration });
            var directory = string.IsNullOrWhiteSpace(customDirectory) ? configuration.RecordDirectory : Path.GetFullPath(customDirectory);
            recording = new ObsRecordingSession(api, directory);
            await Task.Delay(TimeSpan.FromSeconds(3), ct).ConfigureAwait(false);
            var started = await recording.StartAsync(ct: ct).ConfigureAwait(false);
            AtomicJsonFile.Write(Path.Combine(folder, "capture-state.json"), new { State = "RecordingStarted", Start = started, Configuration = configuration });
            await Task.Delay(TimeSpan.FromSeconds(overlapSeconds), ct).ConfigureAwait(false);
            var replay = await capture.SaveAsync(ct: ct).ConfigureAwait(false);
            AtomicJsonFile.Write(Path.Combine(folder, "capture-state.json"), new { State = "ReplaySaved", Start = started, Replay = replay, Configuration = configuration });
            await Task.Delay(TimeSpan.FromSeconds(1), ct).ConfigureAwait(false);
            var saved = await recording.StopAsync(ct: ct).ConfigureAwait(false)
                ?? throw new InvalidOperationException("The test did not own an ordinary recording.");
            result = new HandoffCapture(id, Stopwatch.Frequency, configuration, overlapSeconds, replay, saved);
            AtomicJsonFile.Write(Path.Combine(folder, "capture.json"), result);
        }
        catch (Exception e)
        {
            failure = e;
            AtomicJsonFile.Write(Path.Combine(folder, "error.json"), new { State = "Failed", Error = e.Message });
            throw;
        }
        finally
        {
            var errors = new List<string>();
            // Independent tokens: a cancelled test must still close the outputs it owns.
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(35));
            try { if (recording is { OwnsRecording: true }) await recording.StopAsync(ct: cleanup.Token).ConfigureAwait(false); }
            catch (Exception e) { errors.Add("Recording cleanup: " + e.Message); }
            // A failed buffer start may still have changed the recording directories.
            try { await capture.StopAsync(endpoint, cleanup.Token).ConfigureAwait(false); }
            catch (Exception e) { errors.Add("Replay cleanup: " + e.Message); }
            if (errors.Count > 0)
            {
                AtomicJsonFile.Write(Path.Combine(folder, "cleanup-error.json"), new { Errors = errors });
                if (failure is null) throw new IOException(string.Join(" | ", errors));
            }
        }
        return result;
    }
}
