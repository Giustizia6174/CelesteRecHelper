using System.Diagnostics;
using CelesteRecHelper.Core.Models;
using CelesteRecHelper.Core.Persistence;

namespace CelesteRecHelper.Core.Obs;

public sealed record SavedReplay(string FilePath, long RequestedAtTimestamp, long EventAtTimestamp,
    DateTimeOffset RequestedAtUtc, DateTimeOffset EventAtUtc, long FileSizeBytes);

/// <summary>Owns only outputs it started. Serializes saves and directory changes off the game thread.</summary>
public sealed class ObsCaptureSession(ObsApi api, ManifestStore store)
{
    private readonly SemaphoreSlim operation = new(1);
    private bool owned;
    private Guid connectionId;
    private bool saveUncertain;
    private string? outputDirectory;
    public bool OwnsReplayBuffer => owned;
    public double? MeasuredBitsPerSecond { get; private set; }

    public async Task<string?> RestoreDirectoryAsync(string endpoint, CancellationToken ct = default)
    {
        var recovery = store.LoadObsRecovery();
        if (recovery is null) return null;
        if (!Uri.TryCreate(recovery.Endpoint, UriKind.Absolute, out var savedEndpoint) ||
            !Uri.TryCreate(endpoint, UriKind.Absolute, out var requestedEndpoint) || savedEndpoint != requestedEndpoint)
            return "OBS endpoint differs from the directory recovery record; original directories were kept for manual recovery.";
        var profile = (await api.GetProfileListAsync(ct).ConfigureAwait(false)).GetProperty("currentProfileName").GetString();
        if (profile != recovery.ProfileName)
            return "OBS profile changed; directory restoration was skipped.";
        if ((await api.GetRecordStatusAsync(ct).ConfigureAwait(false)).GetProperty("outputActive").GetBoolean())
            return "OBS recording is active; directory restoration was deferred.";
        try
        {
            if ((await api.GetReplayBufferStatusAsync(ct).ConfigureAwait(false)).GetProperty("outputActive").GetBoolean())
                return "OBS replay buffer is active; directory restoration was deferred.";
        }
        catch (ObsRequestException e) when (e.Code is 504 or 604) { /* OBS reports disabled/unavailable replay outputs as 504 or 604. */ }
        await api.SetProfileParameterAsync("SimpleOutput", "FilePath", recovery.OriginalSimpleDirectory, ct).ConfigureAwait(false);
        await api.SetProfileParameterAsync("AdvOut", "RecFilePath", recovery.OriginalAdvancedDirectory, ct).ConfigureAwait(false);
        store.ClearObsRecovery();
        return null;
    }

    public async Task<ObsDiagnostics> StartAsync(string endpoint, string? customDirectory = null, CancellationToken ct = default)
    {
        await operation.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (owned) throw new InvalidOperationException("This session already owns a replay buffer.");
            var diagnostics = await new ObsInspector(api).InspectAsync(ct: ct).ConfigureAwait(false);
            if (diagnostics.RecordingActive || diagnostics.ReplayActive)
                throw new InvalidOperationException("OBS recording or replay buffer is already active. Stop it before starting the helper.");
            var restoration = await RestoreDirectoryAsync(endpoint, ct).ConfigureAwait(false);
            if (restoration is not null) throw new InvalidOperationException(restoration);
            diagnostics = await new ObsInspector(api).InspectAsync(ct: ct).ConfigureAwait(false);
            if (!diagnostics.ReplayAvailable || !diagnostics.Configuration.Enabled || !diagnostics.Configuration.OutputSupported)
                throw new InvalidOperationException("Enable a supported replay buffer configuration in OBS first.");
            if (!string.IsNullOrWhiteSpace(customDirectory))
            {
                var version = await api.GetVersionAsync(ct).ConfigureAwait(false);
                if (!version.GetProperty("availableRequests").EnumerateArray().Any(r => r.GetString() == "SetRecordDirectory"))
                    throw new NotSupportedException("Custom directories require obs-websocket 5.3.0 or later.");
                var path = Path.GetFullPath(customDirectory);
                Directory.CreateDirectory(path);
                var simple = await api.GetProfileParameterAsync("SimpleOutput", "FilePath", ct).ConfigureAwait(false);
                var advanced = await api.GetProfileParameterAsync("AdvOut", "RecFilePath", ct).ConfigureAwait(false);
                if (simple is null || advanced is null)
                    throw new InvalidDataException("Could not back up both OBS recording directories.");
                store.SaveObsRecovery(new ObsDirectoryRecoveryRecord
                {
                    Endpoint = new Uri(endpoint).AbsoluteUri, ProfileName = diagnostics.ProfileName, OriginalSimpleDirectory = simple,
                    OriginalAdvancedDirectory = advanced, TemporaryDirectory = path
                });
                await api.SetRecordDirectoryAsync(path, ct).ConfigureAwait(false);
                outputDirectory = path;
            }
            else outputDirectory = Path.GetFullPath(diagnostics.RecordDirectory);
            var startingConnection = api.Client.ConnectionId;
            await api.StartReplayBufferAsync(ct).ConfigureAwait(false);
            if (api.Client.ConnectionId != startingConnection)
                throw new ObsTransportException("OBS reconnected during replay startup; verify output ownership before continuing.");
            owned = true;
            connectionId = startingConnection;
            saveUncertain = false;
            return await new ObsInspector(api).InspectAsync(ct: ct).ConfigureAwait(false);
        }
        finally { operation.Release(); }
    }

    public Task<SavedReplay> SaveAsync(TimeSpan? timeout = null, CancellationToken ct = default) =>
        SaveCoreAsync(null, null, timeout, ct);

    /// <summary>The callback must persist the exact request and file snapshot before any save RPC is sent.</summary>
    public Task<SavedReplay> SaveAsync(Guid obsSessionId, Func<ReplaySaveRequest, CancellationToken, Task> beforeSend,
        TimeSpan? timeout = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(beforeSend);
        if (obsSessionId == Guid.Empty) throw new ArgumentException("An OBS session ID is required.", nameof(obsSessionId));
        return SaveCoreAsync(obsSessionId, beforeSend, timeout, ct);
    }

    private async Task<SavedReplay> SaveCoreAsync(Guid? obsSessionId,
        Func<ReplaySaveRequest, CancellationToken, Task>? beforeSend, TimeSpan? timeout, CancellationToken ct)
    {
        await operation.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!owned || saveUncertain || outputDirectory is null)
                throw new InvalidOperationException("Start a fresh owned replay session before saving.");
            if (api.Client.ConnectionId != connectionId)
                throw new ObsTransportException("OBS reconnected; start a fresh replay session after checking its output state.");
            var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
            var existing = ReplayOutputFiles.EnumerateTopLevel(outputDirectory).ToHashSet(comparer);
            var completion = new TaskCompletionSource<ObsEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
            var requested = Stopwatch.GetTimestamp();
            var utc = DateTimeOffset.UtcNow;
            void OnEvent(ObsEvent message)
            {
                if (message.Type != "ReplayBufferSaved" || message.Timestamp < requested) return;
                var value = message.Data.GetProperty("savedReplayPath").GetString();
                if (string.IsNullOrWhiteSpace(value) || !Path.IsPathFullyQualified(value)) return;
                var path = Path.GetFullPath(value);
                var root = Path.TrimEndingDirectorySeparator(outputDirectory) + Path.DirectorySeparatorChar;
                if (!path.StartsWith(root, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal) || existing.Contains(path)) return;
                completion.TrySetResult(message);
            }
            void OnState(ObsConnectionState state)
            {
                if (state != ObsConnectionState.Connected)
                    completion.TrySetException(new ObsTransportException("OBS disconnected while a replay save was in flight."));
            }
            api.Client.EventReceived += OnEvent;
            api.Client.StateChanged += OnState;
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var duration = timeout ?? TimeSpan.FromSeconds(10);
            deadline.CancelAfter(duration);
            try
            {
                if (beforeSend is not null)
                    await beforeSend(new ReplaySaveRequest
                    {
                        ObsSessionId = obsSessionId!.Value, ExpectedDirectory = outputDirectory,
                        RequestedAtTimestamp = requested, RequestedAtUtc = utc, DeadlineUtc = utc + duration,
                        ExistingFiles = existing.ToList()
                    }, deadline.Token).ConfigureAwait(false);
                // Persistence may take time; refuse a late or reconnected dispatch.
                deadline.Token.ThrowIfCancellationRequested();
                if (api.Client.ConnectionId != connectionId)
                    throw new ObsTransportException("OBS reconnected before the replay request was dispatched.");
                await api.SaveReplayBufferAsync(deadline.Token).ConfigureAwait(false);
                var message = await completion.Task.WaitAsync(deadline.Token).ConfigureAwait(false);
                var path = Path.GetFullPath(message.Data.GetProperty("savedReplayPath").GetString()!);
                while (!File.Exists(path) || new FileInfo(path).Length == 0)
                    await Task.Delay(50, deadline.Token).ConfigureAwait(false);
                return new SavedReplay(path, requested, message.Timestamp, utc, message.ReceivedAtUtc, new FileInfo(path).Length);
            }
            catch { saveUncertain = true; throw; }
            finally
            {
                api.Client.EventReceived -= OnEvent;
                api.Client.StateChanged -= OnState;
            }
        }
        finally { operation.Release(); }
    }

    public void ObserveProbe(SavedReplay replay, double durationSeconds)
    {
        if (durationSeconds <= 0 || !double.IsFinite(durationSeconds)) throw new ArgumentOutOfRangeException(nameof(durationSeconds));
        MeasuredBitsPerSecond = replay.FileSizeBytes * 8.0 / durationSeconds;
    }

    public async Task StopAsync(string endpoint, CancellationToken ct = default)
    {
        await operation.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (owned)
            {
                if (api.Client.ConnectionId != connectionId)
                    throw new ObsTransportException("OBS reconnected; replay ownership needs manual verification before stopping outputs.");
                await api.StopReplayBufferAsync(ct).ConfigureAwait(false);
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
                deadline.CancelAfter(TimeSpan.FromSeconds(10));
                while ((await api.GetReplayBufferStatusAsync(deadline.Token).ConfigureAwait(false)).GetProperty("outputActive").GetBoolean())
                    await Task.Delay(100, deadline.Token).ConfigureAwait(false);
                owned = false;
            }
            var warning = await RestoreDirectoryAsync(endpoint, ct).ConfigureAwait(false);
            if (warning is not null) throw new InvalidOperationException(warning);
        }
        finally { operation.Release(); }
    }
}
