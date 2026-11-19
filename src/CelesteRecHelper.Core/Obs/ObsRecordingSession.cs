using System.Diagnostics;
using CelesteRecHelper.Core.Models;

namespace CelesteRecHelper.Core.Obs;

/// <summary>Ordinary recording used during handoff. A successful RPC response is not an output-state event.</summary>
public sealed class ObsRecordingSession(ObsApi api, string expectedDirectory)
{
    private bool owned;
    private Guid connectionId;
    private long requestedAt;
    private RecordingStarted? start;
    public bool OwnsRecording => owned;

    public async Task<RecordingStarted> StartAsync(TimeSpan? timeout = null, CancellationToken ct = default)
    {
        if (owned) throw new InvalidOperationException("This session already owns a recording.");
        if ((await api.GetRecordStatusAsync(ct).ConfigureAwait(false)).GetProperty("outputActive").GetBoolean())
            throw new InvalidOperationException("OBS is already recording. The existing recording will not be stopped.");
        connectionId = api.Client.ConnectionId;
        requestedAt = Stopwatch.GetTimestamp();
        var message = await ChangeStateAsync("OBS_WEBSOCKET_OUTPUT_STARTED", async token =>
        {
            await api.StartRecordAsync(token).ConfigureAwait(false);
            owned = true;
        }, timeout, ct).ConfigureAwait(false);
        start = new RecordingStarted(requestedAt, message.Timestamp, message.ReceivedAtUtc);
        return start;
    }

    public async Task<SavedRecording?> StopAsync(TimeSpan? timeout = null, CancellationToken ct = default)
    {
        if (!owned) return null;
        if (api.Client.ConnectionId != connectionId || api.Client.State != ObsConnectionState.Connected)
            throw new IOException("OBS reconnected; recording ownership needs manual verification before stopping outputs.");
        var stopRequested = Stopwatch.GetTimestamp();
        string? responsePath = null;
        var message = await ChangeStateAsync("OBS_WEBSOCKET_OUTPUT_STOPPED", async token =>
        {
            var response = await api.StopRecordAsync(token).ConfigureAwait(false);
            responsePath = response.GetProperty("outputPath").GetString();
        }, timeout, ct).ConfigureAwait(false);
        owned = false;
        var eventPath = message.Data.GetProperty("outputPath").GetString();
        if (string.IsNullOrWhiteSpace(eventPath) || !Path.IsPathFullyQualified(eventPath))
            throw new InvalidDataException("OBS recording stopped without an absolute file path.");
        var path = Path.GetFullPath(eventPath);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(expectedDirectory)) + Path.DirectorySeparatorChar;
        if (!path.StartsWith(root, comparison) || string.IsNullOrEmpty(responsePath) ||
            !string.Equals(path, Path.GetFullPath(responsePath), comparison))
            throw new InvalidDataException("OBS recording paths disagree or are outside the expected directory.");
        if (!File.Exists(path) || new FileInfo(path).Length == 0)
            throw new InvalidDataException("The stopped recording file is missing or empty.");
        if (start is null)
            throw new InvalidDataException($"Recording was stopped and retained at {path}, but its start event was not observed.");
        return new SavedRecording(path, start, stopRequested, message.Timestamp, message.ReceivedAtUtc);
    }

    private async Task<ObsEvent> ChangeStateAsync(string state, Func<CancellationToken, Task> request,
        TimeSpan? timeout, CancellationToken ct)
    {
        var completion = new TaskCompletionSource<ObsEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        var earliest = Stopwatch.GetTimestamp();
        void OnEvent(ObsEvent message)
        {
            if (message.Type == "RecordStateChanged" && message.Timestamp >= earliest &&
                message.Data.GetProperty("outputState").GetString() == state)
                completion.TrySetResult(message);
        }
        void OnState(ObsConnectionState current)
        {
            if (current != ObsConnectionState.Connected)
                completion.TrySetException(new IOException("OBS disconnected during a recording state change."));
        }
        api.Client.EventReceived += OnEvent;
        api.Client.StateChanged += OnState;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(timeout ?? TimeSpan.FromSeconds(15));
        try
        {
            await request(deadline.Token).ConfigureAwait(false);
            return await completion.Task.WaitAsync(deadline.Token).ConfigureAwait(false);
        }
        finally
        {
            api.Client.EventReceived -= OnEvent;
            api.Client.StateChanged -= OnState;
        }
    }
}
