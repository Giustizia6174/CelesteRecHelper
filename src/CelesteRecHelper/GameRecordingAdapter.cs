using System.Diagnostics;
using System.Runtime.CompilerServices;
using CelesteRecHelper.Core.Models;
using CelesteRecHelper.Core.Obs;
using CelesteRecHelper.Core.Persistence;
using CelesteRecHelper.Core.Recording;
using Microsoft.Xna.Framework;
using Monocle;

namespace Celeste.Mod.CelesteRecHelper;

/// <summary>All game objects and input are read on the game thread. Network and disk work belongs to RouteRun.</summary>
internal sealed class GameRecordingAdapter : IAsyncDisposable
{
    private readonly ManifestStore store;
    private RouteRun? current;
    private RouteRun? displayed;
    private Task closing = Task.CompletedTask;
    private Scene? hudScene;
    private RecordingHud? hud;
    private bool disposed;

    public GameRecordingAdapter(ManifestStore store)
    {
        this.store = store;
        Everest.Events.Level.OnEnter += OnEnter;
        Everest.Events.Level.OnExit += OnExit;
        Everest.Events.Level.OnComplete += OnComplete;
        Everest.Events.Player.OnDie += OnDeath;
        Everest.Events.Celeste.OnExiting += OnExiting;
        On.Monocle.Engine.Update += Update;
    }

    private void OnEnter(Session session, bool fromSaveData)
    {
        var identity = IdentityOf(session);
        Logger.Log(LogLevel.Info, "CelesteRecHelper",
            $"Level enter: {identity.MapSid} mode {identity.AreaMode}, slot {identity.SaveSlot}, " +
            $"from save {fromSaveData}, session {SessionTag(session)}, active session {SessionTag(current?.GameSession)}.");
        if (CelesteRecHelperModule.Settings.Enabled) Start(session, identity);
    }

    private void Start(Session session, RouteIdentity identity)
    {
        if (disposed) return;
        if (current is { } active && active.Identity == identity)
        {
            active.UseSession(session);
            return;
        }
        CloseCurrent(false, Stopwatch.GetTimestamp());
        var settings = CelesteRecHelperModule.Settings;
        var run = new RouteRun(store, session, identity,
            Dialog.Clean(AreaData.Get(session.Area).Name), settings.ObsHost, settings.ObsPort,
            settings.ObsPassword, settings.OutputDirectory, closing);
        current = displayed = run;
    }

    private void Update(On.Monocle.Engine.orig_Update orig, Engine self, GameTime gameTime)
    {
        orig(self, gameTime);
        if (disposed) return;
        var now = Stopwatch.GetTimestamp();
        if (!CelesteRecHelperModule.Settings.Enabled)
            CloseCurrent(false, now);
        else if (Engine.Scene is Level level && !level.Completed)
        {
            var identity = IdentityOf(level.Session);
            if (current is { } active && active.Identity == identity)
                active.UseSession(level.Session);
            else
                Start(level.Session, identity);
            if (current is { } run && run.Identity == identity && run.Ready && !run.Gate.Completing)
            {
                var player = level.Tracker.GetEntity<Player>();
                var stable = player is { Dead: false, Visible: true, InControl: true } && !player.JustRespawned &&
                    !level.FrozenOrPaused && !level.Transitioning && level.Wipe is null && level.Overlay is null;
                var actions = run.Gate.Observe(now, stable, CelesteRecHelperModule.Settings.EndSegment.Pressed);
                if (actions.StartLife) run.Stable(now);
                if (actions.ConfirmEnd) run.Confirm(now);
                else if (actions.EndIgnored) run.Notice("rechelper_recording_end_ignored");
                if (CelesteRecHelperModule.Settings.Undo.Pressed) run.Notice("rechelper_recording_undo_pending");
            }
        }
        if (current is { } completing && completing.Gate.TryFinishCompletion(now))
            CloseCurrent(true, now);
        displayed?.ReportStatus();
        AttachHud();
    }

    private void OnDeath(Player player)
    {
        if (current is { } run && player.Scene is Level level && run.Identity == IdentityOf(level.Session) && run.Gate.OnDeath())
            run.Died(Stopwatch.GetTimestamp());
    }

    private void OnComplete(Level level)
    {
        if (current is not { } run || run.Identity != IdentityOf(level.Session)) return;
        var player = level.Tracker.GetEntity<Player>();
        if (run.Gate.TryBeginCompletion(Stopwatch.GetTimestamp(), player is { Dead: false }))
            run.Notice("rechelper_recording_tail");
    }

    private void OnExit(Level level, LevelExit exit, LevelExit.Mode mode, Session session, HiresSnow snow)
    {
        var identity = IdentityOf(session);
        var run = current;
        var sameSession = run is not null && ReferenceEquals(run.GameSession, session);
        var sameIdentity = run?.Identity == identity;
        var disposition = RouteExitPolicy.Decide(ToGameplayExitMode(mode));
        Logger.Log(LogLevel.Info, "CelesteRecHelper",
            $"Level exit: {mode}, identity {identity.MapSid} mode {identity.AreaMode}, slot {identity.SaveSlot}, " +
            $"level session {SessionTag(level.Session)}, event session {SessionTag(session)}, " +
            $"active session {SessionTag(run?.GameSession)}, session match {sameSession}, " +
            $"route match {sameIdentity}, disposition {disposition}.");
        if (run is null || (!sameSession && !sameIdentity)) return;
        run.UseSession(session);
        if (disposition == RouteExitDisposition.Continue) return;
        run.Gate.OnExit(disposition == RouteExitDisposition.Complete);
        if (!run.Gate.Completing) CloseCurrent(false, Stopwatch.GetTimestamp());
    }

    private void CloseCurrent(bool completed, long timestamp)
    {
        if (current is not { } run) return;
        current = null;
        if (!completed) run.Gate.OnExit(false);
        closing = run.CloseAsync(completed, timestamp);
    }

    private static RouteIdentity IdentityOf(Session session) => new(
        SaveData.Instance?.FileSlot ?? -1, session.Area.SID, (int)session.Area.Mode);

    private static string SessionTag(Session? session) => session is null
        ? "none"
        : RuntimeHelpers.GetHashCode(session).ToString("x8");

    private static GameplayExitMode ToGameplayExitMode(LevelExit.Mode mode) => mode switch
    {
        LevelExit.Mode.SaveAndQuit => GameplayExitMode.SaveAndQuit,
        LevelExit.Mode.GiveUp => GameplayExitMode.GiveUp,
        LevelExit.Mode.Restart => GameplayExitMode.Restart,
        LevelExit.Mode.GoldenBerryRestart => GameplayExitMode.GoldenBerryRestart,
        LevelExit.Mode.Completed => GameplayExitMode.Completed,
        LevelExit.Mode.CompletedInterlude => GameplayExitMode.CompletedInterlude,
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unknown Celeste level exit mode.")
    };

    private void AttachHud()
    {
        if (displayed is null || Engine.Scene is null) return;
        if (hudScene == Engine.Scene && hud is { WasRemoved: false }) return;
        hud?.RemoveSelf();
        hudScene = Engine.Scene;
        hud = new RecordingHud(() => displayed?.View());
        hudScene.Add(hud);
    }

    private void OnExiting() => DisposeAsync().AsTask().GetAwaiter().GetResult();

    public async ValueTask DisposeAsync()
    {
        if (disposed) return;
        disposed = true;
        hud?.RemoveSelf();
        Everest.Events.Level.OnEnter -= OnEnter;
        Everest.Events.Level.OnExit -= OnExit;
        Everest.Events.Level.OnComplete -= OnComplete;
        Everest.Events.Player.OnDie -= OnDeath;
        Everest.Events.Celeste.OnExiting -= OnExiting;
        On.Monocle.Engine.Update -= Update;
        CloseCurrent(false, Stopwatch.GetTimestamp());
        await closing.ConfigureAwait(false);
    }

    private sealed class RouteRun
    {
        private readonly ManifestStore store;
        private readonly Guid routeId = Guid.NewGuid();
        private readonly CancellationTokenSource startupCancellation = new();
        private readonly Task startup;
        private ObsWebSocketClient? client;
        private ObsCaptureSession? capture;
        private ReplaySaveScheduler? scheduler;
        private RecordingFailure? localFailure;
        private string notice = "rechelper_recording_connecting";
        private long noticeUntil;
        private Task? closeTask;
        private volatile bool closed;
        private string endpoint = "";
        private RecordingFailure? reportedFailure;
        private int reportedSavedSegments;
        public Session GameSession { get; private set; }
        public RouteIdentity Identity { get; }
        public GameplayEventGate Gate { get; } = new(Stopwatch.Frequency);
        public bool Ready => Volatile.Read(ref scheduler) is not null;

        public RouteRun(ManifestStore store, Session gameSession, RouteIdentity identity, string name,
            string host, int port, string password, string directory, Task previousClose)
        {
            this.store = store;
            GameSession = gameSession;
            Identity = identity;
            startup = Task.Run(async () =>
            {
                RouteManifest? createdRoute = null;
                ObsSession? createdObsSession = null;
                try
                {
                    await previousClose.ConfigureAwait(false);
                    startupCancellation.Token.ThrowIfCancellationRequested();
                    var uri = new UriBuilder("ws", host.Trim(), port).Uri;
                    endpoint = uri.AbsoluteUri;
                    client = new ObsWebSocketClient(uri, password);
                    client.StateChanged += OnConnectionState;
                    client.EventReceived += OnObsEvent;
                    await client.ConnectAsync(startupCancellation.Token).ConfigureAwait(false);
                    var api = new ObsApi(client);
                    capture = new ObsCaptureSession(api, store);
                    var check = await new ObsInspector(api).InspectAsync(ct: startupCancellation.Token).ConfigureAwait(false);
                    if (check.RecordingActive || check.ReplayActive)
                    {
                        SetFailure(new RecordingFailure(RecordingFailureKind.Unknown, RecordingFailureStage.Start,
                            "rechelper_error_inuse", "Existing OBS outputs were not taken over."));
                        return;
                    }
                    if (!check.Configuration.Enabled || !check.ReplayAvailable || !check.Configuration.OutputSupported)
                    {
                        var key = !check.Configuration.Enabled ? "rechelper_error_obs_disabled" :
                            !check.Configuration.OutputSupported ? "rechelper_error_output_unsupported" : "rechelper_error_replay_unavailable";
                        SetFailure(new RecordingFailure(RecordingFailureKind.Unknown, RecordingFailureStage.Start,
                            key, "The current OBS replay configuration is unavailable or unsupported."));
                        return;
                    }
                    var diagnostics = await capture.StartAsync(this.endpoint, directory, startupCancellation.Token).ConfigureAwait(false);
                    startupCancellation.Token.ThrowIfCancellationRequested();
                    var session = new ObsSession
                    {
                        Endpoint = this.endpoint, ProfileName = diagnostics.ProfileName,
                        OutputDirectory = string.IsNullOrWhiteSpace(directory) ? diagnostics.RecordDirectory : Path.GetFullPath(directory),
                        ReplayBufferWasActive = true
                    };
                    var route = new RouteManifest
                    {
                        RouteId = routeId, SaveSlot = identity.SaveSlot, MapSid = identity.MapSid, MapName = name,
                        AreaMode = identity.AreaMode, ObsSessions = [session]
                    };
                    createdRoute = route;
                    createdObsSession = session;
                    try { store.Save(route); }
                    catch (Exception e)
                    {
                        SetFailure(RecordingFailureClassifier.Classify(e, RecordingFailureStage.Manifest));
                        return;
                    }
                    var started = new ReplaySaveScheduler(new SegmentStateMachine(route, session.Id), capture, store.Save, this.endpoint);
                    Volatile.Write(ref scheduler, started);
                    Notice("rechelper_recording_started");
                    Logger.Log(LogLevel.Info, "CelesteRecHelper",
                        $"Route {routeId:N} started: {identity.MapSid}, mode {identity.AreaMode}, slot {identity.SaveSlot}.");
                }
                catch (OperationCanceledException) when (startupCancellation.IsCancellationRequested) { }
                catch (Exception e) { SetFailure(RecordingFailureClassifier.Classify(e, RecordingFailureStage.Start)); }
                finally
                {
                    if (scheduler is null && capture is not null)
                    {
                        var captureStopped = false;
                        try
                        {
                            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                            await capture.StopAsync(this.endpoint, cleanup.Token).ConfigureAwait(false);
                            captureStopped = true;
                        }
                        catch (Exception e) { SetFailure(RecordingFailureClassifier.Classify(e, RecordingFailureStage.Stop)); }

                        if (captureStopped && createdRoute is not null && createdObsSession is not null)
                        {
                            createdObsSession.ReplayBufferWasActive = false;
                            createdObsSession.RecordingWasActive = false;
                            createdObsSession.EndedAtUtc = DateTimeOffset.UtcNow;
                            createdRoute.Status = RouteStatus.Ended;
                            try
                            {
                                store.Save(createdRoute);
                                if (store.DeleteIfEmptyClosedRoute(routeId))
                                    Logger.Log(LogLevel.Info, "CelesteRecHelper", $"Removed empty route {routeId:N} after startup cleanup.");
                            }
                            catch (Exception e)
                            {
                                SetFailure(RecordingFailureClassifier.Classify(e, RecordingFailureStage.Manifest));
                            }
                        }
                    }
                }
            });
        }

        public void UseSession(Session session)
        {
            if (ReferenceEquals(GameSession, session)) return;
            Logger.Log(LogLevel.Info, "CelesteRecHelper",
                $"Route {routeId:N}: replacing Session object while keeping logical identity {Identity.MapSid}, " +
                $"mode {Identity.AreaMode}, slot {Identity.SaveSlot}.");
            GameSession = session;
        }

        private void OnConnectionState(ObsConnectionState state)
        {
            if (!closed && Volatile.Read(ref scheduler) is { } active && state != ObsConnectionState.Connected)
                Observe(active.SuspendAsync(new ObsTransportException("OBS connection changed during gameplay; automatic saving is paused.")));
        }

        private void OnObsEvent(ObsEvent message)
        {
            if (!closed && Volatile.Read(ref scheduler) is { } active && message.Type == "ReplayBufferStateChanged" &&
                message.Data.TryGetProperty("outputState", out var state) && state.GetString() == "OBS_WEBSOCKET_OUTPUT_STOPPED")
                Observe(active.SuspendAsync(new ObsReplayStoppedException("The owned replay buffer stopped during gameplay.")));
        }

        public void Stable(long timestamp)
        {
            Logger.Log(LogLevel.Info, "CelesteRecHelper", $"Route {routeId:N}: stable start at {timestamp}.");
            Observe(scheduler!.OnGameplayStableAsync(timestamp));
        }
        public void Died(long timestamp)
        {
            Logger.Log(LogLevel.Info, "CelesteRecHelper", $"Route {routeId:N}: death at {timestamp}; unconfirmed tail discarded.");
            Observe(scheduler!.OnDeathAsync(timestamp));
        }
        public void Confirm(long timestamp)
        {
            Notice("rechelper_recording_recorded");
            ObserveEnd(scheduler!.OnEndKeyAsync(timestamp, true), timestamp);
        }
        private async void ObserveEnd(Task<EndSegmentResult> task, long timestamp)
        {
            try
            {
                var result = await task.ConfigureAwait(false);
                Logger.Log(LogLevel.Info, "CelesteRecHelper", $"Route {routeId:N}: end at {timestamp}, {result.Status}, segment {result.SaveCommand?.SegmentId:N}.");
            }
            catch (Exception e) when (!closed) { SetFailure(scheduler?.Snapshot.Failure ?? RecordingFailureClassifier.Classify(e, RecordingFailureStage.Save)); }
            catch (Exception) when (closed) { }
        }

        private async void Observe(Task task)
        {
            try { await task.ConfigureAwait(false); }
            catch (Exception e) when (!closed) { SetFailure(scheduler?.Snapshot.Failure ?? RecordingFailureClassifier.Classify(e, RecordingFailureStage.Save)); }
            catch (Exception) when (closed) { }
        }

        public void Notice(string key)
        {
            Volatile.Write(ref notice, key);
            Volatile.Write(ref noticeUntil, Stopwatch.GetTimestamp() + 3 * Stopwatch.Frequency);
        }

        public void ReportStatus()
        {
            var status = Volatile.Read(ref scheduler)?.Snapshot;
            if (status is not null && status.SavedSegments != reportedSavedSegments)
            {
                reportedSavedSegments = status.SavedSegments;
                Logger.Log(LogLevel.Info, "CelesteRecHelper", $"Route {routeId:N}: saved {status.SavedSegments}/{status.RecordedSegments} segments. Manifest: {store.GetManifestPath(routeId)}");
            }
            var failure = status?.Failure;
            if (failure is null || failure == reportedFailure) return;
            reportedFailure = failure;
            _ = Task.Run(() => SetFailure(failure));
        }

        private void SetFailure(RecordingFailure failure)
        {
            var first = Interlocked.CompareExchange(ref localFailure, failure, null) is null;
            Logger.Log(LogLevel.Warn, "CelesteRecHelper", $"Route {routeId:N}: {failure.Kind}/{failure.Stage}: {failure.Detail}");
            try { AtomicJsonFile.Write(Path.Combine(store.RootDirectory, "diagnostics", "recording", routeId.ToString("N"), first ? "failure.json" : $"failure-{failure.Stage}.json"), failure); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Logger.Log(LogLevel.Warn, "CelesteRecHelper", e.Message); }
        }

        public RecordingHudView View()
        {
            var status = Volatile.Read(ref scheduler)?.Snapshot;
            var failure = status?.Failure ?? Volatile.Read(ref localFailure);
            if (failure is not null)
            {
                var code = failure.ObsCode is { } value ? $" ({value}: {failure.ObsRequest})" : "";
                return new RecordingHudView(Dialog.Clean("rechelper_recording_suspended"),
                    Dialog.Clean(failure.MessageKey) + code, Counts(status) + " / " +
                    Dialog.Clean(status?.ReplayBufferOwned == true ? "rechelper_recording_buffer_owned" : "rechelper_recording_buffer_check"), true);
            }
            var title = Dialog.Clean(closed ? "rechelper_recording_ended" : Gate.Completing ? "rechelper_recording_tail" :
                status is null ? "rechelper_recording_connecting" : "rechelper_recording_active");
            var message = Stopwatch.GetTimestamp() < Volatile.Read(ref noticeUntil) ? Dialog.Clean(Volatile.Read(ref notice)) : "";
            return new RecordingHudView(title, Counts(status), message, false);
        }

        private static string Counts(ReplaySaveSnapshot? status) => status is null ? "" :
            Dialog.Clean("rechelper_recording_counts") + $" {status.SavedSegments}/{status.RecordedSegments}";

        public Task CloseAsync(bool completed, long timestamp) => closeTask ??= CloseCoreAsync(completed, timestamp);
        private async Task CloseCoreAsync(bool completed, long timestamp)
        {
            closed = true;
            if (completed && !Ready)
                SetFailure(new RecordingFailure(RecordingFailureKind.Unknown, RecordingFailureStage.Start,
                    "rechelper_error_start_notready", "The chapter completed before the replay capture became ready."));
            startupCancellation.Cancel();
            await startup.ConfigureAwait(false);
            var schedulerStoppedCleanly = false;
            if (scheduler is { } active)
            {
                try
                {
                    if (completed) await active.OnCompletedAsync(timestamp).ConfigureAwait(false);
                    else await active.OnExitAsync(timestamp).ConfigureAwait(false);
                }
                catch (Exception e) { SetFailure(active.Snapshot.Failure ?? RecordingFailureClassifier.Classify(e, RecordingFailureStage.Save)); }
                try
                {
                    await active.DisposeAsync().ConfigureAwait(false);
                    schedulerStoppedCleanly = true;
                }
                catch (Exception e) { SetFailure(active.Snapshot.Failure ?? RecordingFailureClassifier.Classify(e, RecordingFailureStage.Stop)); }
            }
            if (client is not null)
            {
                client.StateChanged -= OnConnectionState;
                client.EventReceived -= OnObsEvent;
                await client.DisposeAsync().ConfigureAwait(false);
            }
            if (schedulerStoppedCleanly)
            {
                try
                {
                    if (store.DeleteIfEmptyClosedRoute(routeId))
                        Logger.Log(LogLevel.Info, "CelesteRecHelper", $"Removed empty route {routeId:N} after OBS stopped.");
                }
                catch (Exception e)
                {
                    Logger.Log(LogLevel.Warn, "CelesteRecHelper", $"Could not clean empty route {routeId:N}: {e.Message}");
                }
            }
            startupCancellation.Dispose();
        }
    }
}
