using System.Diagnostics;
using System.Threading.Channels;
using CelesteRecHelper.Core.Models;
using CelesteRecHelper.Core.Obs;

namespace CelesteRecHelper.Core.Recording;

/// <summary>
/// Owns a live state machine and an already-started replay buffer. Public calls enqueue work off the game thread.
/// OBS saves run separately; all manifest changes and persistence run on one queue. Do not mutate the route externally.
/// </summary>
public sealed class ReplaySaveScheduler : IAsyncDisposable
{
    private readonly SegmentStateMachine machine;
    private readonly ObsCaptureSession capture;
    private readonly Action<RouteManifest> persist;
    private readonly string endpoint;
    private readonly TimeSpan saveTimeout;
    private readonly Func<SegmentCutRequest, CancellationToken, Task<SegmentCutResult>>? cut;
    private readonly ObsSession session;
    private readonly object cutsGate = new();
    private readonly List<Task> cuts = [];
    private readonly Channel<Action> work = Channel.CreateUnbounded<Action>(new UnboundedChannelOptions
        { SingleReader = true, AllowSynchronousContinuations = false });
    private readonly List<TaskCompletionSource> idleWaiters = [];
    private readonly object submission = new();
    private readonly Task runner;
    private Task? disposal;
    private bool busy;
    private bool stopAttempted;
    private Exception? storageFailure;
    private Exception? outputFailure;
    private RecordingFailureStage outputFailureStage = RecordingFailureStage.Save;
    private ReplaySaveSnapshot snapshot = null!;
    public ReplaySaveSnapshot Snapshot => Volatile.Read(ref snapshot);
    public Exception? Failure => Volatile.Read(ref storageFailure) ?? Volatile.Read(ref outputFailure);

    public ReplaySaveScheduler(SegmentStateMachine machine, ObsCaptureSession capture,
        Action<RouteManifest> persist, string endpoint, TimeSpan? saveTimeout = null,
        Func<SegmentCutRequest, CancellationToken, Task<SegmentCutResult>>? cut = null)
    {
        ArgumentNullException.ThrowIfNull(machine);
        ArgumentNullException.ThrowIfNull(capture);
        ArgumentNullException.ThrowIfNull(persist);
        this.machine = machine;
        this.capture = capture;
        this.persist = persist;
        this.endpoint = endpoint;
        this.saveTimeout = saveTimeout ?? TimeSpan.FromSeconds(10);
        this.cut = cut;
        if (this.saveTimeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(saveTimeout));
        if (!capture.OwnsReplayBuffer || machine.Route.Status != RouteStatus.Recording ||
            machine.Route.SaveRequests.Any(r => r.Status == SaveRequestStatus.Pending))
            throw new InvalidOperationException("Start a fresh owned replay buffer and a live route before creating its scheduler.");
        session = machine.Route.ObsSessions.Single(s => s.Id == machine.ObsSessionId);
        PublishSnapshot();
        runner = Task.Run(RunAsync);
    }

    public Task<bool> OnGameplayStableAsync(long timestamp) =>
        ChangeAsync(() => machine.OnGameplayStable(timestamp), changed => changed);
    public Task<EndSegmentResult> OnEndKeyAsync(long timestamp, bool gameplayAvailable) =>
        ChangeAsync(() => machine.OnEndKey(timestamp, gameplayAvailable), r => r.Status == EndSegmentStatus.Recorded);
    public Task<bool> OnDeathAsync(long timestamp) => ChangeAsync(() => machine.OnDeath(timestamp), changed => changed);
    public Task<EndSegmentResult> OnCompletedAsync(long timestamp) =>
        ChangeAsync(() => machine.OnCompleted(timestamp), r => r.Status != EndSegmentStatus.RouteClosed);
    public Task<bool> OnExitAsync(long timestamp) => ChangeAsync(() => machine.OnExit(timestamp), changed => changed);

    /// <summary>Latch a detected connection/output failure without cancelling or misassigning an in-flight result.</summary>
    public Task SuspendAsync(Exception error)
    {
        ArgumentNullException.ThrowIfNull(error);
        lock (submission)
        {
            ObjectDisposedException.ThrowIf(disposal is not null, this);
            return Submit(() =>
            {
                ThrowIfStorageFailed();
                if (outputFailure is null)
                {
                    outputFailure = error;
                    outputFailureStage = RecordingFailureStage.Buffer;
                    machine.Route.LastFailure = RecordingFailureClassifier.Classify(error, outputFailureStage);
                    FailUnassignedSegments(error.Message);
                    Persist();
                }
                return true;
            });
        }
    }

    /// <summary>Wait for queued saves, and for output shutdown when the route has ended. Throws any latched failure.</summary>
    public Task WhenIdleAsync()
    {
        lock (submission)
        {
            ObjectDisposedException.ThrowIf(disposal is not null, this);
            return WaitForIdleCoreAsync();
        }
    }

    private Task<T> ChangeAsync<T>(Func<T> change, Func<T, bool> changed)
    {
        lock (submission)
        {
            ObjectDisposedException.ThrowIf(disposal is not null, this);
            return Submit(() =>
            {
                ThrowIfStorageFailed();
                var result = change();
                if (changed(result))
                {
                    if (outputFailure is not null) FailUnassignedSegments(outputFailure.Message);
                    Persist();
                }
                return result;
            });
        }
    }

    private async Task RunAsync()
    {
        await foreach (var action in work.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            action();
            Pump();
            PublishSnapshot();
            if (!busy)
            {
                foreach (var waiter in idleWaiters)
                {
                    if (Failure is { } failure) waiter.TrySetException(failure);
                    else waiter.TrySetResult();
                }
                idleWaiters.Clear();
            }
        }
    }

    private Task<T> Submit<T>(Func<T> action)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!work.Writer.TryWrite(() =>
        {
            try { completion.TrySetResult(action()); }
            catch (Exception e) { completion.TrySetException(e); }
        })) completion.TrySetException(new ObjectDisposedException(nameof(ReplaySaveScheduler)));
        return completion.Task;
    }

    private void Pump()
    {
        if (busy) return;
        if (storageFailure is null)
        {
            var queued = machine.Route.Segments.Where(s => s.ObsSessionId == session.Id &&
                s.Status == SegmentStatus.Recorded && s.SaveRequestId is null).Select(s => s.Id).ToArray();
            if (outputFailure is null && queued.Length > 0)
            {
                busy = true;
                _ = Task.Run(() => SaveBatchAsync(queued));
                return;
            }
            if (machine.Route.Status != RouteStatus.Recording && !stopAttempted)
            {
                busy = true;
                stopAttempted = true;
                _ = Task.Run(StopOutputAsync);
                return;
            }
        }
    }

    private async Task SaveBatchAsync(Guid[] segmentIds)
    {
        Guid? requestId = null;
        SavedReplay? saved = null;
        Exception? failure = null;
        try
        {
            saved = await capture.SaveAsync(session.Id, (request, ct) => Submit(() =>
            {
                ThrowIfStorageFailed();
                ct.ThrowIfCancellationRequested();
                machine.BeginSave(request, segmentIds);
                requestId = request.Id;
                Persist();
                return true;
            }), saveTimeout).ConfigureAwait(false);
        }
        catch (Exception e) { failure = e; }
        try
        {
            await Submit(() =>
            {
                busy = false;
                if (storageFailure is not null) return false;
                if (failure is null)
                {
                    try
                    {
                        machine.OnSaveSucceeded(requestId!.Value, saved!);
                    }
                    catch (Exception e) { failure = e; }
                }
                if (failure is not null)
                {
                    outputFailure = failure;
                    machine.Route.LastFailure = RecordingFailureClassifier.Classify(failure, RecordingFailureStage.Save);
                    if (requestId is { } id) machine.OnSaveFailed(id, failure.Message);
                    FailUnassignedSegments(failure.Message);
                }
                Persist(); // Publish the OBS result before starting cuts, sending another save, or stopping the buffer.
                if (failure is null && cut is not null)
                {
                    try { QueueCut(machine.CreateCutRequest(requestId!.Value)); }
                    catch (Exception e)
                    {
                        // Cutting is independent: a saved replay remains Saved and later OBS requests may proceed.
                        machine.OnCutFailed(requestId!.Value, e.Message);
                        Persist();
                    }
                }
                return true;
            }).ConfigureAwait(false);
        }
        catch (Exception) when (storageFailure is not null) { /* Reported by Failure/WhenIdleAsync; no further output commands. */ }
    }

    private void QueueCut(SegmentCutRequest request)
    {
        if (cut is null) return;
        var task = Task.Run(async () =>
        {
            SegmentCutResult result;
            try
            {
                result = await cut(request, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception e)
            {
                result = new SegmentCutResult
                {
                    RouteId = request.RouteId, SaveRequestId = request.SaveRequestId,
                    SourceMediaId = request.SourceMediaId, ObsSessionId = request.ObsSessionId,
                    SourcePath = request.SourcePath,
                    Segments = request.Segments.Select(item => new SegmentCutItemResult
                    {
                        SegmentId = item.SegmentId, Sequence = item.Sequence, Succeeded = false, Error = e.Message
                    }).ToList()
                };
            }
            try
            {
                await Submit(() =>
                {
                    if (storageFailure is not null) return false;
                    try { machine.ApplyCutResult(result); }
                    catch (Exception e) { machine.OnCutFailed(request.SaveRequestId, e.Message); }
                    Persist();
                    SegmentCutFiles.RemoveUnusedReplays(machine, PersistRoute);
                    return true;
                }).ConfigureAwait(false);
            }
            catch (Exception) when (storageFailure is not null) { }
        });
        lock (cutsGate) cuts.Add(task);
        _ = task.ContinueWith(_ =>
        {
            lock (cutsGate) cuts.Remove(task);
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    private async Task StopOutputAsync()
    {
        Exception? failure = null;
        try
        {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await capture.StopAsync(endpoint, cleanup.Token).ConfigureAwait(false);
        }
        catch (Exception e) { failure = e; }
        try
        {
            await Submit(() =>
            {
                busy = false;
                if (failure is not null)
                {
                    if (outputFailure is null)
                    {
                        outputFailure = failure;
                        outputFailureStage = RecordingFailureStage.Stop;
                    }
                    machine.Route.LastFailure = RecordingFailureClassifier.Classify(failure, RecordingFailureStage.Stop);
                    Persist();
                }
                else
                {
                    session.ReplayBufferWasActive = false;
                    session.EndedAtUtc = DateTimeOffset.UtcNow;
                    Persist();
                }
                return true;
            }).ConfigureAwait(false);
        }
        catch (Exception) when (storageFailure is not null) { }
    }

    private void PublishSnapshot()
    {
        var route = machine.Route;
        var error = Failure;
        var failure = error is null ? null : RecordingFailureClassifier.Classify(error,
            storageFailure is not null ? RecordingFailureStage.Manifest : outputFailureStage);
        // A failed result write must not present the uncommitted in-memory state as saved.
        var recorded = storageFailure is null ? route.Segments.Count : snapshot.RecordedSegments;
        var saved = storageFailure is null ? route.Segments.Count(s => s.Status == SegmentStatus.Saved) : snapshot.SavedSegments;
        var failed = storageFailure is null ? route.Segments.Count(s => s.Status == SegmentStatus.SaveFailed) : snapshot.FailedSegments;
        var cutSegments = storageFailure is null ? route.Segments.Count(s => s.SliceStatus == SegmentSliceStatus.Succeeded) : snapshot.CutSegments;
        var pendingCuts = storageFailure is null ? route.Segments.Count(s => s.Status == SegmentStatus.Saved &&
            s.SliceStatus == SegmentSliceStatus.Pending) : snapshot.PendingCuts;
        var failedCuts = storageFailure is null ? route.Segments.Count(s => s.Status == SegmentStatus.Saved &&
            s.SliceStatus == SegmentSliceStatus.Failed) : snapshot.FailedCuts;
        Volatile.Write(ref snapshot, new ReplaySaveSnapshot(route.RouteId, route.Status, recorded, saved, failed,
            busy, capture.OwnsReplayBuffer, failure, cutSegments, pendingCuts, failedCuts));
    }

    private void FailUnassignedSegments(string reason)
    {
        foreach (var segment in machine.Route.Segments.Where(s => s.ObsSessionId == session.Id &&
            s.Status == SegmentStatus.Recorded && s.SaveRequestId is null))
        {
            segment.Status = SegmentStatus.SaveFailed;
            segment.FailureReason = reason;
        }
    }

    private void Persist() => PersistRoute(machine.Route);

    private void PersistRoute(RouteManifest route)
    {
        try { persist(route); }
        catch (Exception e)
        {
            storageFailure = e;
            throw;
        }
    }

    private void ThrowIfStorageFailed()
    {
        if (storageFailure is { } failure)
            throw new IOException("Manifest persistence failed; further recording commands are blocked.", failure);
    }

    private async Task WaitForIdleCoreAsync()
    {
        var idle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await Submit(() => { idleWaiters.Add(idle); return true; }).ConfigureAwait(false);
        await idle.Task.ConfigureAwait(false);
    }

    private async Task WaitForCutsAsync()
    {
        while (true)
        {
            Task[] pending;
            lock (cutsGate) pending = cuts.Where(task => !task.IsCompleted).ToArray();
            if (pending.Length == 0) return;
            await Task.WhenAll(pending).ConfigureAwait(false);
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (submission)
        {
            disposal ??= CloseAsync();
            return new ValueTask(disposal);
        }
    }

    private async Task CloseAsync()
    {
        Exception? failure = null;
        try
        {
            await Submit(() =>
            {
                ThrowIfStorageFailed();
                if (machine.OnExit(Stopwatch.GetTimestamp())) Persist();
                return true;
            }).ConfigureAwait(false);
        }
        catch (Exception e) { failure = e; }
        try { await WaitForIdleCoreAsync().ConfigureAwait(false); }
        catch (Exception e) { failure ??= e; }
        try { await WaitForCutsAsync().ConfigureAwait(false); }
        catch (Exception e) { failure ??= e; }
        finally
        {
            work.Writer.TryComplete();
            await runner.ConfigureAwait(false);
        }
        if (failure is not null) throw new IOException("Replay save scheduler stopped with an error; check retained files and OBS output state.", failure);
    }
}
