using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.WebSockets;
using System.Threading.Channels;
using CelesteRecHelper.Core.Models;
using CelesteRecHelper.Core.Obs;
using CelesteRecHelper.Core.Persistence;
using CelesteRecHelper.Core.Recording;

namespace CelesteRecHelper.Core.Tests;

public sealed class ReplaySaveSchedulerTests : IDisposable
{
    private readonly TestWorkspace workspace = new();
    public void Dispose() => workspace.Dispose();
    private static long Now => Stopwatch.GetTimestamp();

    [Fact]
    public async Task SavesAreSerializedQueuedBoundariesAreMergedAndEveryRpcHasADurableRequest()
    {
        workspace.CreateVideo("older.mkv");
        await using var rig = await StartAsync();
        await rig.Scheduler.OnGameplayStableAsync(Now);
        await rig.Scheduler.OnEndKeyAsync(Now, true);
        var first = await rig.Output.NextSaveAsync();
        var firstRequest = Assert.Single(first.SaveRequests);
        Assert.Equal(SaveRequestStatus.Pending, firstRequest.Status);
        Assert.Contains(Path.Combine(workspace.VideoDirectory, "older.mkv"), firstRequest.ExistingFiles);
        Assert.Equal(firstRequest.Id, Assert.Single(first.Segments).SaveRequestId);
        await rig.Scheduler.OnEndKeyAsync(Now, true).WaitAsync(TimeSpan.FromSeconds(3));
        await rig.Scheduler.OnEndKeyAsync(Now, true).WaitAsync(TimeSpan.FromSeconds(3));
        var queued = workspace.Store.Load(rig.Route.RouteId);
        Assert.Equal(3, queued.Segments.Count);
        Assert.Single(queued.SaveRequests);
        Assert.All(queued.Segments.Skip(1), s => Assert.Null(s.SaveRequestId));
        var idle = rig.Scheduler.WhenIdleAsync();
        Assert.False(idle.IsCompleted);
        rig.Output.ReleaseSave();
        var second = await rig.Output.NextSaveAsync();
        Assert.Equal(SegmentStatus.Saved, second.Segments[0].Status);
        Assert.Single(second.Media);
        Assert.Equal(2, second.SaveRequests.Count);
        var secondRequest = second.SaveRequests[1];
        Assert.All(second.Segments.Skip(1), s => Assert.Equal(secondRequest.Id, s.SaveRequestId));
        Assert.Contains(firstRequest.ExistingFiles[0], secondRequest.ExistingFiles);
        Assert.Contains(second.Media[0].FilePath, secondRequest.ExistingFiles);
        rig.Output.ReleaseSave();
        await idle.WaitAsync(TimeSpan.FromSeconds(5));
        var saved = workspace.Store.Load(rig.Route.RouteId);
        Assert.All(saved.Segments, s => Assert.Equal(SegmentStatus.Saved, s.Status));
        Assert.Equal(2, saved.Media.Count);
        Assert.Equal(saved.Segments[1].Media[0].MediaId, saved.Segments[2].Media[0].MediaId);
        Assert.Equal(saved.SaveRequests[0].RequestedAtTimestamp, saved.Media[0].SaveRequestedAtTimestamp);
        Assert.Equal(2, rig.Output.Commands.Count(c => c == "SaveReplayBuffer"));
        Assert.DoesNotContain("StopReplayBuffer", rig.Output.Commands);
    }

    [Fact]
    public async Task SnapshotListsOnlyTopLevelVideoFilesSoUnrelatedFilesDoNotGrowTheManifest()
    {
        var older = workspace.CreateVideo("older.mkv");
        for (var i = 0; i < 200; i++) File.WriteAllText(Path.Combine(workspace.VideoDirectory, $"note-{i}.txt"), "x");
        var nested = Directory.CreateDirectory(Path.Combine(workspace.VideoDirectory, "project", "deep"));
        for (var i = 0; i < 200; i++) File.WriteAllBytes(Path.Combine(nested.FullName, $"clip-{i}.mkv"), [1]);
        await using var rig = await StartAsync();
        await rig.Scheduler.OnGameplayStableAsync(Now);
        await rig.Scheduler.OnEndKeyAsync(Now, true);
        var request = Assert.Single((await rig.Output.NextSaveAsync()).SaveRequests);
        Assert.Equal([Path.GetFullPath(older)], request.ExistingFiles);
        rig.Output.ReleaseSave();
        await rig.Scheduler.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(new FileInfo(workspace.Store.GetManifestPath(rig.Route.RouteId)).Length < 64 * 1024);
    }

    [Fact]
    public async Task ExitReturnsDuringAnInflightSaveAndStopsOnlyAfterBothBatchesAreDurable()
    {
        await using var rig = await StartAsync();
        await rig.Scheduler.OnGameplayStableAsync(Now);
        await rig.Scheduler.OnEndKeyAsync(Now, true);
        await rig.Output.NextSaveAsync();
        await rig.Scheduler.OnEndKeyAsync(Now, true);
        Assert.True(await rig.Scheduler.OnExitAsync(Now).WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.Equal(RouteStatus.Ended, workspace.Store.Load(rig.Route.RouteId).Status);
        Assert.DoesNotContain("StopReplayBuffer", rig.Output.Commands);
        rig.Output.ReleaseSave();
        await rig.Output.NextSaveAsync();
        Assert.DoesNotContain("StopReplayBuffer", rig.Output.Commands);
        rig.Output.ReleaseSave();
        await rig.Scheduler.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(5));
        var beforeStop = Assert.Single(rig.Output.StopSnapshots);
        Assert.All(beforeStop.Segments, s => Assert.Equal(SegmentStatus.Saved, s.Status));
        Assert.All(beforeStop.SaveRequests, r => Assert.Equal(SaveRequestStatus.Saved, r.Status));
        var ended = workspace.Store.Load(rig.Route.RouteId);
        Assert.Null(ended.CurrentLife);
        Assert.False(ended.ObsSessions[0].ReplayBufferWasActive);
        Assert.NotNull(ended.ObsSessions[0].EndedAtUtc);
        Assert.Equal(1, rig.Output.Commands.Count(c => c == "StopReplayBuffer"));
    }

    [Fact]
    public async Task DeathAndRespawnContinueWhileThePreviousLifeIsBeingSaved()
    {
        await using var rig = await StartAsync();
        await rig.Scheduler.OnGameplayStableAsync(Now);
        await rig.Scheduler.OnEndKeyAsync(Now, true);
        var first = await rig.Output.NextSaveAsync();
        var firstLife = first.CurrentLife!.Id;
        await rig.Scheduler.OnDeathAsync(Now);
        Assert.Null(workspace.Store.Load(rig.Route.RouteId).CurrentLife);
        await rig.Scheduler.OnGameplayStableAsync(Now);
        await rig.Scheduler.OnEndKeyAsync(Now, true);
        rig.Output.ReleaseSave();
        var second = await rig.Output.NextSaveAsync();
        Assert.NotEqual(firstLife, second.Segments[1].LifeId);
        Assert.Equal(second.CurrentLife!.StartedAtTimestamp, second.Segments[1].StartTimestamp);
        Assert.Equal(SegmentStatus.Saved, second.Segments[0].Status);
        rig.Output.ReleaseSave();
        await rig.Scheduler.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(2, workspace.Store.Load(rig.Route.RouteId).Segments.Count);
    }

    [Fact]
    public async Task CompletionDrainsItsFinalSaveAndKeepsTheCompletedRouteStatus()
    {
        await using var rig = await StartAsync();
        await rig.Scheduler.OnGameplayStableAsync(Now);
        var result = await rig.Scheduler.OnCompletedAsync(Now);
        Assert.Equal(EndSegmentStatus.Recorded, result.Status);
        var observed = await rig.Output.NextSaveAsync();
        Assert.Equal(RouteStatus.Completed, observed.Status);
        Assert.Null(observed.CurrentLife);
        Assert.False(await rig.Scheduler.OnExitAsync(Now));
        rig.Output.ReleaseSave();
        await rig.Scheduler.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(RouteStatus.Completed, workspace.Store.Load(rig.Route.RouteId).Status);
        Assert.Single(rig.Output.StopSnapshots);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TimeoutOrObsRejectionFailsBothInflightAndQueuedBoundariesWithoutResending(bool reject)
    {
        await using var rig = await StartAsync(timeout: TimeSpan.FromMilliseconds(750));
        rig.Output.EmitSaveEvents = false;
        rig.Output.RejectSave = reject;
        await rig.Scheduler.OnGameplayStableAsync(Now);
        await rig.Scheduler.OnEndKeyAsync(Now, true);
        await rig.Output.NextSaveAsync();
        await rig.Scheduler.OnEndKeyAsync(Now, true);
        if (reject)
            await Assert.ThrowsAsync<ObsRequestException>(() => rig.Scheduler.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(5)));
        else
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => rig.Scheduler.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.NotNull(rig.Scheduler.Failure);
        if (reject) Assert.IsType<ObsRequestException>(rig.Scheduler.Failure);
        else Assert.IsAssignableFrom<OperationCanceledException>(rig.Scheduler.Failure);
        var failed = workspace.Store.Load(rig.Route.RouteId);
        Assert.All(failed.Segments, s => Assert.Equal(SegmentStatus.SaveFailed, s.Status));
        Assert.All(failed.Segments, s => Assert.False(string.IsNullOrWhiteSpace(s.FailureReason)));
        Assert.Equal(SaveRequestStatus.Failed, Assert.Single(failed.SaveRequests).Status);
        Assert.Empty(failed.Media);
        await rig.Scheduler.OnEndKeyAsync(Now, true);
        Assert.Equal(SegmentStatus.SaveFailed, workspace.Store.Load(rig.Route.RouteId).Segments[2].Status);
        Assert.Equal(1, rig.Output.Commands.Count(c => c == "SaveReplayBuffer"));
        await rig.Scheduler.OnExitAsync(Now);
        if (reject)
            await Assert.ThrowsAsync<ObsRequestException>(() => rig.Scheduler.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(5)));
        else
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => rig.Scheduler.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Single(rig.Output.StopSnapshots);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BoundaryOrRequestPersistenceFailureNeverSendsAnObsSave(bool failRequest)
    {
        void Persist(RouteManifest route)
        {
            if (route.Segments.Count > 0 && (!failRequest || route.SaveRequests.Count > 0))
                throw new IOException("Injected disk failure");
            workspace.Store.Save(route);
        }
        await using var rig = await StartAsync(Persist);
        await rig.Scheduler.OnGameplayStableAsync(Now);
        if (failRequest) await rig.Scheduler.OnEndKeyAsync(Now, true);
        else await Assert.ThrowsAsync<IOException>(() => rig.Scheduler.OnEndKeyAsync(Now, true));
        await Assert.ThrowsAsync<IOException>(() => rig.Scheduler.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.DoesNotContain("SaveReplayBuffer", rig.Output.Commands);
        Assert.DoesNotContain("StopReplayBuffer", rig.Output.Commands);
        await Assert.ThrowsAsync<IOException>(() => rig.Scheduler.OnEndKeyAsync(Now, true));
        var durable = workspace.Store.Load(rig.Route.RouteId);
        Assert.Empty(durable.SaveRequests);
        Assert.Equal(failRequest ? 1 : 0, durable.Segments.Count);
    }

    [Fact]
    public async Task FailedResultPersistenceKeepsTheOldPendingRequestForRecoveryAndBlocksTheNextBatchAndStop()
    {
        void Persist(RouteManifest route)
        {
            if (route.SaveRequests.Any(r => r.Status == SaveRequestStatus.Saved))
                throw new IOException("Injected result write failure");
            workspace.Store.Save(route);
        }
        await using var rig = await StartAsync(Persist);
        await rig.Scheduler.OnGameplayStableAsync(Now);
        await rig.Scheduler.OnEndKeyAsync(Now, true);
        await rig.Output.NextSaveAsync();
        await rig.Scheduler.OnEndKeyAsync(Now, true);
        rig.Output.ReleaseSave();
        await Assert.ThrowsAsync<IOException>(() => rig.Scheduler.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(5)));
        var durable = workspace.Store.Load(rig.Route.RouteId);
        Assert.Equal(SaveRequestStatus.Pending, Assert.Single(durable.SaveRequests).Status);
        Assert.All(durable.Segments, s => Assert.Equal(SegmentStatus.Recorded, s.Status));
        Assert.Empty(durable.Media);
        Assert.Equal(0, rig.Scheduler.Snapshot.SavedSegments);
        Assert.Equal(RecordingFailureStage.Manifest, rig.Scheduler.Snapshot.Failure!.Stage);
        Assert.Equal(1, rig.Output.Commands.Count(c => c == "SaveReplayBuffer"));
        Assert.DoesNotContain("StopReplayBuffer", rig.Output.Commands);
        Assert.True(File.Exists(Path.Combine(workspace.VideoDirectory, "save-1.mkv")));
        var recovered = new StartupRecovery(workspace.Store).Recover();
        Assert.Equal(1, recovered.RecoveredSaves);
        var afterRecovery = workspace.Store.Load(rig.Route.RouteId);
        Assert.Equal(SegmentStatus.Saved, afterRecovery.Segments[0].Status);
        Assert.Equal(SegmentStatus.SaveFailed, afterRecovery.Segments[1].Status);
    }

    [Fact]
    public async Task PersistenceFailureDuringAnInflightSaveDrainsTheCallbackAndRetainsItsFileForRecovery()
    {
        void Persist(RouteManifest route)
        {
            if (route.Status == RouteStatus.Recording && route.CurrentLife is null && route.Segments.Count > 0)
                throw new IOException("Injected death-state write failure");
            workspace.Store.Save(route);
        }
        await using var rig = await StartAsync(Persist);
        await rig.Scheduler.OnGameplayStableAsync(Now);
        await rig.Scheduler.OnEndKeyAsync(Now, true);
        await rig.Output.NextSaveAsync();
        await Assert.ThrowsAsync<IOException>(() => rig.Scheduler.OnDeathAsync(Now));
        var waiting = rig.Scheduler.WhenIdleAsync();
        Assert.False(waiting.IsCompleted);
        rig.Output.ReleaseSave();
        await Assert.ThrowsAsync<IOException>(() => waiting.WaitAsync(TimeSpan.FromSeconds(5)));
        var durable = workspace.Store.Load(rig.Route.RouteId);
        Assert.Equal(SaveRequestStatus.Pending, Assert.Single(durable.SaveRequests).Status);
        Assert.True(File.Exists(Path.Combine(workspace.VideoDirectory, "save-1.mkv")));
        Assert.DoesNotContain("StopReplayBuffer", rig.Output.Commands);
        Assert.Equal(1, new StartupRecovery(workspace.Store).Recover().RecoveredSaves);
    }

    [Fact]
    public async Task DisposeFinishesAnAlreadyQueuedSaveBeforeClosingItsOwnedBuffer()
    {
        await using var rig = await StartAsync();
        await rig.Scheduler.OnGameplayStableAsync(Now);
        await rig.Scheduler.OnEndKeyAsync(Now, true);
        await rig.Output.NextSaveAsync();
        var closing = rig.Scheduler.DisposeAsync().AsTask();
        Assert.False(closing.IsCompleted);
        await Assert.ThrowsAsync<ObjectDisposedException>(async () => await rig.Scheduler.OnEndKeyAsync(Now, true));
        rig.Output.ReleaseSave();
        await closing.WaitAsync(TimeSpan.FromSeconds(5));
        var durable = workspace.Store.Load(rig.Route.RouteId);
        Assert.Equal(RouteStatus.Ended, durable.Status);
        Assert.Equal(SegmentStatus.Saved, Assert.Single(durable.Segments).Status);
        Assert.Single(rig.Output.StopSnapshots);
    }

    [Fact]
    public async Task SuspensionKeepsTheInflightSuccessButBlocksFutureSavesAndKeepsAStableFailureSnapshot()
    {
        await using var rig = await StartAsync();
        await rig.Scheduler.OnGameplayStableAsync(Now);
        await rig.Scheduler.OnEndKeyAsync(Now, true);
        await rig.Output.NextSaveAsync();
        await rig.Scheduler.SuspendAsync(new ObsTransportException("Connection state changed"));
        await rig.Scheduler.OnEndKeyAsync(Now, true);
        rig.Output.ReleaseSave();
        await Assert.ThrowsAsync<ObsTransportException>(() => rig.Scheduler.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(5)));
        var durable = workspace.Store.Load(rig.Route.RouteId);
        Assert.Equal(SegmentStatus.Saved, durable.Segments[0].Status);
        Assert.Equal(SegmentStatus.SaveFailed, durable.Segments[1].Status);
        Assert.Equal(RecordingFailureKind.Disconnected, durable.LastFailure!.Kind);
        await rig.Scheduler.OnEndKeyAsync(Now, true);
        await Assert.ThrowsAsync<ObsTransportException>(() => rig.Scheduler.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(1, rig.Output.Commands.Count(c => c == "SaveReplayBuffer"));
        Assert.Equal(1, rig.Scheduler.Snapshot.SavedSegments);
        Assert.Equal(3, rig.Scheduler.Snapshot.RecordedSegments);
        Assert.Equal(RecordingFailureKind.Disconnected, rig.Scheduler.Snapshot.Failure!.Kind);
        Assert.True(rig.Scheduler.Snapshot.ReplayBufferOwned);
    }

    [Fact]
    public async Task ObsErrorCodeAndCommentRemainAvailableInTheSuspendedStatusAndOnDisk()
    {
        await using var rig = await StartAsync();
        await rig.Scheduler.OnGameplayStableAsync(Now);
        await rig.Scheduler.OnEndKeyAsync(Now, true);
        await rig.Output.NextSaveAsync();
        await rig.Scheduler.SuspendAsync(new ObsRequestException("SaveReplayBuffer", 501, "Output is not running"));
        rig.Output.ReleaseSave();
        await Assert.ThrowsAsync<ObsRequestException>(() => rig.Scheduler.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(5)));
        await rig.Scheduler.OnEndKeyAsync(Now, true);
        await Assert.ThrowsAsync<ObsRequestException>(() => rig.Scheduler.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(5)));
        var failure = workspace.Store.Load(rig.Route.RouteId).LastFailure!;
        Assert.Equal(501, failure.ObsCode);
        Assert.Equal("Output is not running", failure.ObsComment);
        Assert.Equal("rechelper_error_obs_notrunning", rig.Scheduler.Snapshot.Failure!.MessageKey);
        Assert.Equal(1, rig.Output.Commands.Count(c => c == "SaveReplayBuffer"));
    }

    [Fact]
    public async Task CuttingDoesNotBlockLaterSavesAndDisposeDrainsCutsBeforeReturning()
    {
        var cuts = Channel.CreateUnbounded<SegmentCutRequest>();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var rig = await StartAsync(cut: async (request, _) =>
        {
            Assert.Equal(SaveRequestStatus.Saved, workspace.Store.Load(request.RouteId).SaveRequests
                .Single(save => save.Id == request.SaveRequestId).Status);
            cuts.Writer.TryWrite(request);
            await release.Task;
            return SegmentCutTests.Success(request, workspace.CreateVideo($"clip-{request.Segments[0].Sequence}.mkv"));
        });
        try
        {
            await rig.Scheduler.OnGameplayStableAsync(Now);
            await rig.Scheduler.OnEndKeyAsync(Now, true);
            await rig.Output.NextSaveAsync();
            rig.Output.ReleaseSave();
            var first = await cuts.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            await rig.Scheduler.OnEndKeyAsync(Now, true).WaitAsync(TimeSpan.FromSeconds(3));
            await rig.Output.NextSaveAsync();
            rig.Output.ReleaseSave();
            await cuts.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(File.Exists(first.SourcePath));
            var closing = rig.Scheduler.DisposeAsync().AsTask();
            Assert.False(closing.IsCompleted);
            release.TrySetResult();
            await closing.WaitAsync(TimeSpan.FromSeconds(5));
            var route = workspace.Store.Load(rig.Route.RouteId);
            Assert.All(route.Segments, segment => Assert.Equal(SegmentSliceStatus.Succeeded, segment.SliceStatus));
            Assert.All(route.Media, media => Assert.Equal(MediaKind.SegmentClip, media.Kind));
            Assert.False(File.Exists(first.SourcePath));
            Assert.Equal(2, rig.Output.Commands.Count(command => command == "SaveReplayBuffer"));
            Assert.Equal(2, rig.Scheduler.Snapshot.CutSegments);
            Assert.Equal(0, rig.Scheduler.Snapshot.PendingCuts);
        }
        finally { release.TrySetResult(); }
    }

    [Fact]
    public async Task AWorkerFailureKeepsSavedReplaysAndDoesNotSuspendObsSaves()
    {
        await using var rig = await StartAsync(cut: (_, _) => throw new IOException("ffmpeg failed"));
        await rig.Scheduler.OnGameplayStableAsync(Now);
        for (var i = 0; i < 2; i++)
        {
            await rig.Scheduler.OnEndKeyAsync(Now, true);
            await rig.Output.NextSaveAsync();
            rig.Output.ReleaseSave();
            await rig.Scheduler.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(5));
        }
        await rig.Scheduler.DisposeAsync();
        var route = workspace.Store.Load(rig.Route.RouteId);
        Assert.Null(rig.Scheduler.Failure);
        Assert.Equal(2, rig.Scheduler.Snapshot.FailedCuts);
        Assert.Equal(2, rig.Scheduler.Snapshot.SavedSegments);
        Assert.All(route.SaveRequests, request => Assert.Equal(SaveRequestStatus.Saved, request.Status));
        Assert.All(route.Segments, segment =>
        {
            Assert.Equal(SegmentStatus.Saved, segment.Status);
            Assert.Equal(SegmentSliceStatus.Failed, segment.SliceStatus);
            Assert.Equal("ffmpeg failed", segment.SliceFailureReason);
        });
        Assert.All(route.Media, media => Assert.True(File.Exists(media.FilePath)));
    }

    private async Task<Rig> StartAsync(Action<RouteManifest>? persist = null, TimeSpan? timeout = null,
        Func<SegmentCutRequest, CancellationToken, Task<SegmentCutResult>>? cut = null)
    {
        var output = new Outputs(workspace);
        var server = await ObsTestServer.StartAsync(output.RunAsync);
        var client = new ObsWebSocketClient(server.Endpoint);
        await client.ConnectAsync();
        var capture = new ObsCaptureSession(new ObsApi(client), workspace.Store);
        var diagnostics = await capture.StartAsync(server.Endpoint.AbsoluteUri);
        var session = new ObsSession
        {
            Endpoint = server.Endpoint.AbsoluteUri, ProfileName = diagnostics.ProfileName,
            OutputDirectory = diagnostics.RecordDirectory, ReplayBufferWasActive = true
        };
        var route = new RouteManifest { SaveSlot = 0, MapSid = "test/map", MapName = "测试地图", ObsSessions = [session] };
        output.RouteId = route.RouteId;
        var scheduler = new ReplaySaveScheduler(new SegmentStateMachine(route, session.Id), capture,
            persist ?? workspace.Store.Save, server.Endpoint.AbsoluteUri, timeout, cut);
        return new Rig(scheduler, route, output, capture, client, server);
    }

    private sealed record Rig(ReplaySaveScheduler Scheduler, RouteManifest Route, Outputs Output,
        ObsCaptureSession Capture, ObsWebSocketClient Client, ObsTestServer Server) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            // Release a test-controlled event if an assertion failed while a save was held.
            for (var i = 0; i < 10; i++) Output.ReleaseSave();
            try { await Scheduler.DisposeAsync(); }
            catch (IOException) when (Scheduler.Failure is not null) { }
            finally
            {
                if (Capture.OwnsReplayBuffer) await Capture.StopAsync(Server.Endpoint.AbsoluteUri);
                await Client.DisposeAsync();
                await Server.DisposeAsync();
            }
        }
    }

    private sealed class Outputs(TestWorkspace workspace)
    {
        private readonly Channel<RouteManifest> saves = Channel.CreateUnbounded<RouteManifest>();
        private readonly Channel<bool> releases = Channel.CreateUnbounded<bool>();
        private int number;
        public Guid RouteId { get; set; }
        public bool EmitSaveEvents { get; set; } = true;
        public bool RejectSave { get; set; }
        public bool ReplayActive { get; private set; }
        public ConcurrentQueue<string> Commands { get; } = new();
        public ConcurrentQueue<RouteManifest> StopSnapshots { get; } = new();
        public void ReleaseSave() => releases.Writer.TryWrite(true);
        public async Task<RouteManifest> NextSaveAsync() =>
            await saves.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));

        public async Task RunAsync(WebSocket socket, int _, CancellationToken ct)
        {
            while (true)
            {
                var request = await ObsTestServer.Receive(socket, ct);
                var type = request.GetProperty("requestType").GetString()!;
                Commands.Enqueue(type);
                object result = new { };
                switch (type)
                {
                    case "GetVersion": result = new { obsVersion = "32.2.2", obsWebSocketVersion = "5.7.4" }; break;
                    case "GetProfileList": result = new { currentProfileName = "Test" }; break;
                    case "GetRecordStatus": result = new { outputActive = false }; break;
                    case "GetReplayBufferStatus": result = new { outputActive = ReplayActive }; break;
                    case "GetRecordDirectory": result = new { recordDirectory = workspace.VideoDirectory }; break;
                    case "GetOutputList": result = new { outputs = Array.Empty<object>() }; break;
                    case "GetProfileParameter":
                        var name = request.GetProperty("requestData").GetProperty("parameterName").GetString();
                        var value = name switch
                        {
                            "Mode" => "Simple", "RecRB" => "true", "RecRBTime" => "300", "RecRBSize" => "512",
                            "RecEncoder" => "x264", "RecQuality" => "Stream", _ => ""
                        };
                        result = new { parameterValue = value, defaultParameterValue = (string?)null };
                        break;
                    case "StartReplayBuffer": ReplayActive = true; break;
                    case "StopReplayBuffer":
                        StopSnapshots.Enqueue(workspace.Store.Load(RouteId));
                        ReplayActive = false;
                        break;
                    case "SaveReplayBuffer":
                        var index = ++number;
                        saves.Writer.TryWrite(workspace.Store.Load(RouteId));
                        await ObsTestServer.Reply(socket, request, new { }, ct, success: !RejectSave);
                        if (EmitSaveEvents && !RejectSave)
                        {
                            await releases.Reader.ReadAsync(ct);
                            var path = Path.Combine(workspace.VideoDirectory, $"save-{index}.mkv");
                            await File.WriteAllBytesAsync(path, [1, 2, 3], ct);
                            await ObsTestServer.Event(socket, "ReplayBufferSaved", new { savedReplayPath = path }, ct);
                        }
                        continue;
                }
                await ObsTestServer.Reply(socket, request, result, ct);
            }
        }
    }
}
