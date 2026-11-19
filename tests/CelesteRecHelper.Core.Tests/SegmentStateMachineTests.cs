using CelesteRecHelper.Core.Models;
using CelesteRecHelper.Core.Obs;
using CelesteRecHelper.Core.Persistence;
using CelesteRecHelper.Core.Recording;

namespace CelesteRecHelper.Core.Tests;

public sealed class SegmentStateMachineTests : IDisposable
{
    private readonly TestWorkspace workspace = new();
    private readonly DateTimeOffset requestedAt = DateTimeOffset.UtcNow;
    public void Dispose() => workspace.Dispose();
    private RouteManifest NewRoute() => workspace.NewRoute() with { CurrentLife = null };
    private static SegmentStateMachine Machine(RouteManifest route) => new(route, route.ObsSessions[0].Id);
    private ReplaySaveRequest Request(RouteManifest route, long timestamp) => new()
    {
        ObsSessionId = route.ObsSessions[0].Id, ExpectedDirectory = workspace.VideoDirectory,
        RequestedAtTimestamp = timestamp, RequestedAtUtc = requestedAt, DeadlineUtc = requestedAt.AddSeconds(10)
    };
    private SavedReplay Replay(ReplaySaveRequest request, string name = "replay.mkv") => new(
        workspace.CreateVideo(name), request.RequestedAtTimestamp + 1, request.RequestedAtTimestamp + 10,
        request.RequestedAtUtc.AddMilliseconds(1), request.RequestedAtUtc.AddMilliseconds(100), 3);

    [Fact]
    public void StableGameplayStartsOneLifeAndRepeatedFramesDoNotMoveItsStart()
    {
        var route = NewRoute();
        var machine = Machine(route);
        Assert.True(machine.OnGameplayStable(100));
        var life = route.CurrentLife!;
        Assert.False(machine.OnGameplayStable(150));
        Assert.Same(life, route.CurrentLife);
        Assert.Equal(100, life.StartedAtTimestamp);
        Assert.Equal(100, life.PendingStartTimestamp);
        Assert.Empty(route.Segments);
        Assert.Empty(route.SaveRequests);
    }

    [Fact]
    public void ConsecutiveEndKeysFixAdjacentBoundariesAndEmitSaveCommandsImmediately()
    {
        var route = NewRoute();
        var machine = Machine(route);
        machine.OnGameplayStable(100);
        var first = machine.OnEndKey(200, true);
        var second = machine.OnEndKey(350, true);
        Assert.Equal(EndSegmentStatus.Recorded, first.Status);
        Assert.Equal(EndSegmentStatus.Recorded, second.Status);
        Assert.Equal(new ReplaySaveCommand(route.Segments[0].Id, route.ObsSessions[0].Id), first.SaveCommand);
        Assert.Equal(new ReplaySaveCommand(route.Segments[1].Id, route.ObsSessions[0].Id), second.SaveCommand);
        Assert.Equal((100L, 200L), (route.Segments[0].StartTimestamp, route.Segments[0].EndTimestamp));
        Assert.Equal((200L, 350L), (route.Segments[1].StartTimestamp, route.Segments[1].EndTimestamp));
        Assert.Equal(route.Segments[0].LifeId, route.Segments[1].LifeId);
        Assert.All(route.Segments, s => Assert.Equal(SegmentStatus.Recorded, s.Status));
        Assert.Equal(350, route.CurrentLife!.PendingStartTimestamp);
        Assert.Empty(route.SaveRequests);
        Assert.Empty(route.Media);
    }

    [Fact]
    public void EndKeysBeforeStartDuringUnavailableGameplayOrAtAnEmptyBoundaryDoNotCreateSegments()
    {
        var route = NewRoute();
        var machine = Machine(route);
        Assert.Equal(new EndSegmentResult(EndSegmentStatus.NoActiveLife), machine.OnEndKey(50, true));
        machine.OnGameplayStable(100);
        Assert.Equal(new EndSegmentResult(EndSegmentStatus.EmptyInterval), machine.OnEndKey(100, true));
        Assert.Equal(new EndSegmentResult(EndSegmentStatus.GameplayUnavailable), machine.OnEndKey(150, false));
        Assert.Empty(route.Segments);
        Assert.Equal(100, route.CurrentLife!.PendingStartTimestamp);
        Assert.Equal(EndSegmentStatus.Recorded, machine.OnEndKey(200, true).Status);
        Assert.Equal(new EndSegmentResult(EndSegmentStatus.EmptyInterval), machine.OnEndKey(200, true));
        Assert.Single(route.Segments);
    }

    [Fact]
    public void DeathDiscardsOnlyTheUnconfirmedTailAndAnOlderSaveCanFinishAfterRespawn()
    {
        var route = NewRoute();
        var machine = Machine(route);
        machine.OnGameplayStable(100);
        machine.OnEndKey(200, true);
        var originalLife = route.CurrentLife!.Id;
        var request = Request(route, 220);
        machine.BeginSave(request, [route.Segments[0].Id]);
        Assert.True(machine.OnDeath(300));
        Assert.Null(route.CurrentLife);
        Assert.False(machine.OnDeath(301));
        machine.OnGameplayStable(400);
        machine.OnEndKey(500, true);
        Assert.NotEqual(originalLife, route.CurrentLife!.Id);
        Assert.Equal((400L, 500L), (route.Segments[1].StartTimestamp, route.Segments[1].EndTimestamp));
        Assert.True(machine.OnSaveSucceeded(request.Id, Replay(request)));
        Assert.Equal(SegmentStatus.Saved, route.Segments[0].Status);
        Assert.Equal(SegmentStatus.Recorded, route.Segments[1].Status);
        Assert.Null(route.Segments[1].SaveRequestId);
        Assert.Equal(500, route.CurrentLife.PendingStartTimestamp);
        Assert.Single(route.SaveRequests);
    }

    [Fact]
    public void RepeatedFailedAttemptsDoNotAccumulateLifeHistoryOrIssueSaves()
    {
        var route = NewRoute();
        var machine = Machine(route);
        for (var i = 0; i < 100; i++)
        {
            machine.OnGameplayStable(i * 100);
            machine.OnDeath(i * 100 + 50);
        }
        Assert.Null(route.CurrentLife);
        Assert.Empty(route.Segments);
        Assert.Empty(route.SaveRequests);
        Assert.Empty(route.Media);
    }

    [Fact]
    public void CompletionRecordsTheFinalTailOnceAndLaterExitPreservesCompletedStatus()
    {
        var route = NewRoute();
        var machine = Machine(route);
        machine.OnGameplayStable(100);
        machine.OnEndKey(200, true);
        var result = machine.OnCompleted(400); // The adapter supplies the time after its results-screen wait.
        Assert.Equal(EndSegmentStatus.Recorded, result.Status);
        Assert.Equal(route.Segments[1].Id, result.SaveCommand!.SegmentId);
        Assert.Equal((200L, 400L), (route.Segments[1].StartTimestamp, route.Segments[1].EndTimestamp));
        Assert.Equal(RouteStatus.Completed, route.Status);
        Assert.Null(route.CurrentLife);
        Assert.Equal(EndSegmentStatus.RouteClosed, machine.OnCompleted(410).Status);
        Assert.False(machine.OnExit(420));
        Assert.False(machine.OnGameplayStable(430));
        Assert.Equal(EndSegmentStatus.RouteClosed, machine.OnEndKey(440, true).Status);
        Assert.Equal(RouteStatus.Completed, route.Status);
        Assert.Equal(2, route.Segments.Count);
    }

    [Fact]
    public void CompletionWithoutAnActiveLifeDoesNotInventFootage()
    {
        var route = NewRoute();
        var result = Machine(route).OnCompleted(100);
        Assert.Equal(EndSegmentStatus.NoActiveLife, result.Status);
        Assert.Null(result.SaveCommand);
        Assert.Equal(RouteStatus.Completed, route.Status);
        Assert.Empty(route.Segments);
    }

    [Fact]
    public void ExitKeepsConfirmedBoundariesAndPendingSaveCanFinishAfterTheRouteEnds()
    {
        var route = NewRoute();
        var machine = Machine(route);
        machine.OnGameplayStable(100);
        machine.OnEndKey(200, true);
        var request = Request(route, 220);
        machine.BeginSave(request, [route.Segments[0].Id]);
        Assert.True(machine.OnExit(300));
        Assert.Equal(RouteStatus.Ended, route.Status);
        Assert.Null(route.CurrentLife);
        Assert.Equal(SaveRequestStatus.Pending, request.Status);
        Assert.Single(route.Segments);
        Assert.True(machine.OnSaveSucceeded(request.Id, Replay(request)));
        Assert.Equal(SegmentStatus.Saved, route.Segments[0].Status);
        Assert.False(machine.OnExit(310));
        Assert.False(machine.OnGameplayStable(320));
        Assert.Equal(EndSegmentStatus.RouteClosed, machine.OnEndKey(330, true).Status);
        Assert.Equal(RouteStatus.Ended, route.Status);
    }

    [Fact]
    public void OneLaterReplayCanCoverSeveralQueuedBoundariesWithoutChangingTheirTimes()
    {
        var route = NewRoute();
        var machine = Machine(route);
        machine.OnGameplayStable(100);
        machine.OnEndKey(200, true);
        var first = Request(route, 210);
        machine.BeginSave(first, [route.Segments[0].Id]);
        machine.OnEndKey(300, true);
        machine.OnEndKey(400, true);
        var second = Request(route, 410);
        var queued = route.Segments.Skip(1).Select(s => s.Id).ToArray();
        Assert.Throws<InvalidOperationException>(() => machine.BeginSave(second, queued));
        Assert.Single(route.SaveRequests);
        Assert.All(route.Segments.Skip(1), s => Assert.Null(s.SaveRequestId));
        machine.OnSaveSucceeded(first.Id, Replay(first, "first.mkv"));
        machine.BeginSave(second, queued);
        machine.OnSaveSucceeded(second.Id, Replay(second, "batch.mkv"));
        Assert.Equal(2, route.Media.Count);
        Assert.Equal(2, route.SaveRequests.Count);
        Assert.All(route.Segments, s => Assert.Equal(SegmentStatus.Saved, s.Status));
        Assert.Equal(route.Segments[1].Media[0].MediaId, route.Segments[2].Media[0].MediaId);
        Assert.Equal((200L, 300L), (route.Segments[1].Media[0].StartTimestamp, route.Segments[1].Media[0].EndTimestamp));
        Assert.Equal((300L, 400L), (route.Segments[2].Media[0].StartTimestamp, route.Segments[2].Media[0].EndTimestamp));
    }

    [Fact]
    public void SaveFailureRetainsTheLogicalSegmentAndDoesNotBlockGameplayOrAcceptALateSuccess()
    {
        var route = NewRoute();
        var machine = Machine(route);
        machine.OnGameplayStable(100);
        machine.OnEndKey(200, true);
        var request = Request(route, 210);
        machine.BeginSave(request, [route.Segments[0].Id]);
        Assert.True(machine.OnSaveFailed(request.Id, "OBS disconnected"));
        Assert.Equal(SaveRequestStatus.Failed, request.Status);
        Assert.Equal(SegmentStatus.SaveFailed, route.Segments[0].Status);
        Assert.Equal("OBS disconnected", route.Segments[0].FailureReason);
        Assert.False(machine.OnSaveSucceeded(request.Id, Replay(request)));
        Assert.False(machine.OnSaveFailed(request.Id, "late error"));
        Assert.Equal("OBS disconnected", request.FailureReason);
        Assert.Equal(EndSegmentStatus.Recorded, machine.OnEndKey(300, true).Status);
        Assert.Equal(200, route.Segments[1].StartTimestamp);
        Assert.Empty(route.Media);
    }

    [Fact]
    public void SuccessfulResultIsAppliedOnlyOnceAndPreservesRawTimes()
    {
        var route = NewRoute();
        var machine = Machine(route);
        machine.OnGameplayStable(100);
        machine.OnEndKey(200, true);
        var request = Request(route, 210);
        machine.BeginSave(request, [route.Segments[0].Id]);
        var replay = Replay(request);
        Assert.True(machine.OnSaveSucceeded(request.Id, replay));
        Assert.False(machine.OnSaveSucceeded(request.Id, replay));
        Assert.False(machine.OnSaveFailed(request.Id, "late error"));
        var media = Assert.Single(route.Media);
        Assert.Equal(replay.FilePath, request.OutputPath);
        Assert.Equal(replay.RequestedAtTimestamp, media.SaveRequestedAtTimestamp);
        Assert.Equal(replay.EventAtTimestamp, media.SaveEventAtTimestamp);
        Assert.Equal(replay.EventAtUtc, media.SaveEventAtUtc);
        Assert.Null(route.Segments[0].FailureReason);
        Assert.Equal(media.Id, Assert.Single(route.Segments[0].Media).MediaId);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("duplicate")]
    [InlineData("future")]
    public void InvalidSaveBatchIsRejectedBeforeAnySegmentIsAssigned(string problem)
    {
        var route = NewRoute();
        var machine = Machine(route);
        machine.OnGameplayStable(100);
        machine.OnEndKey(200, true);
        machine.OnEndKey(300, true);
        var ids = problem switch
        {
            "missing" => new[] { route.Segments[0].Id, Guid.NewGuid() },
            "duplicate" => new[] { route.Segments[0].Id, route.Segments[0].Id },
            _ => route.Segments.Select(s => s.Id).ToArray()
        };
        if (problem == "future")
            Assert.Throws<InvalidOperationException>(() => machine.BeginSave(Request(route, 250), ids));
        else
            Assert.Throws<ArgumentException>(() => machine.BeginSave(Request(route, 310), ids));
        Assert.Empty(route.SaveRequests);
        Assert.All(route.Segments, s => Assert.Null(s.SaveRequestId));
    }

    [Theory]
    [InlineData("outside")]
    [InlineData("existing")]
    [InlineData("already-used")]
    public void AReplayOutsideTheDirectoryOrAlreadyKnownCannotBeAdopted(string problem)
    {
        var route = NewRoute();
        var machine = Machine(route);
        machine.OnGameplayStable(100);
        machine.OnEndKey(200, true);
        var request = Request(route, 210);
        machine.BeginSave(request, [route.Segments[0].Id]);
        var replay = Replay(request);
        if (problem == "outside") replay = replay with { FilePath = Path.Combine(workspace.Root, "foreign.mkv") };
        if (problem == "existing") request.ExistingFiles.Add(replay.FilePath);
        if (problem == "already-used") route.Media.Add(new MediaAsset
        {
            ObsSessionId = route.ObsSessions[0].Id, FilePath = replay.FilePath, Kind = MediaKind.ReplayBuffer
        });
        Assert.Throws<ArgumentException>(() => machine.OnSaveSucceeded(request.Id, replay));
        Assert.Equal(SaveRequestStatus.Pending, request.Status);
        Assert.Equal(SegmentStatus.Recorded, route.Segments[0].Status);
        Assert.Empty(route.Segments[0].Media);
    }

    [Theory]
    [InlineData("before-request")]
    [InlineData("event-before-request")]
    [InlineData("late-event")]
    [InlineData("empty-file")]
    public void InvalidReplayTimingOrEmptyFilesCannotMarkASegmentSaved(string problem)
    {
        var route = NewRoute();
        var machine = Machine(route);
        machine.OnGameplayStable(100);
        machine.OnEndKey(200, true);
        var request = Request(route, 210);
        machine.BeginSave(request, [route.Segments[0].Id]);
        var replay = Replay(request);
        replay = problem switch
        {
            "before-request" => replay with { RequestedAtTimestamp = 209 },
            "event-before-request" => replay with { EventAtTimestamp = 210 },
            "late-event" => replay with { EventAtUtc = request.DeadlineUtc.AddTicks(1) },
            _ => replay with { FileSizeBytes = 0 }
        };
        Assert.Throws<ArgumentException>(() => machine.OnSaveSucceeded(request.Id, replay));
        Assert.Equal(SaveRequestStatus.Pending, request.Status);
        Assert.Equal(SegmentStatus.Recorded, route.Segments[0].Status);
        Assert.Empty(route.Media);
    }

    [Fact]
    public void UnknownSaveCallbacksCannotChangeAnotherRequest()
    {
        var route = NewRoute();
        var machine = Machine(route);
        machine.OnGameplayStable(100);
        machine.OnEndKey(200, true);
        var request = Request(route, 210);
        machine.BeginSave(request, [route.Segments[0].Id]);
        Assert.Throws<ArgumentException>(() => machine.OnSaveSucceeded(Guid.NewGuid(), Replay(request)));
        Assert.Throws<ArgumentException>(() => machine.OnSaveFailed(Guid.NewGuid(), "unknown request"));
        Assert.Equal(SaveRequestStatus.Pending, request.Status);
        Assert.Empty(route.Media);
    }

    [Fact]
    public void BackwardGameplayEventsCannotCreateInvalidIntervals()
    {
        var route = NewRoute();
        var machine = Machine(route);
        Assert.Throws<ArgumentOutOfRangeException>(() => machine.OnGameplayStable(-1));
        machine.OnGameplayStable(100);
        machine.OnEndKey(200, true);
        Assert.Throws<ArgumentOutOfRangeException>(() => machine.OnEndKey(199, true));
        Assert.Throws<ArgumentOutOfRangeException>(() => machine.OnDeath(199));
        Assert.Throws<ArgumentOutOfRangeException>(() => machine.OnCompleted(199));
        Assert.Throws<ArgumentOutOfRangeException>(() => machine.OnExit(199));
        Assert.Single(route.Segments);
        Assert.NotNull(route.CurrentLife);
        Assert.Equal(RouteStatus.Recording, route.Status);
    }

    [Fact]
    public void ForeignSessionsAndRequestsCannotBeBoundToTheRoute()
    {
        var route = NewRoute();
        Assert.Throws<ArgumentException>(() => new SegmentStateMachine(route, Guid.NewGuid()));
        var otherSession = route.ObsSessions[0] with { Id = Guid.NewGuid() };
        route.ObsSessions.Add(otherSession);
        var machine = Machine(route);
        machine.OnGameplayStable(100);
        Assert.Throws<ArgumentException>(() => new SegmentStateMachine(route, otherSession.Id));
        machine.OnEndKey(200, true);
        Assert.Throws<ArgumentException>(() => machine.BeginSave(Request(route, 210) with { ObsSessionId = otherSession.Id }, [route.Segments[0].Id]));
        Assert.Empty(route.SaveRequests);
    }

    [Fact]
    public void RecordedPendingAndSavedStatesRoundTripThroughTheExistingManifestSchema()
    {
        var route = NewRoute();
        var machine = Machine(route);
        machine.OnGameplayStable(100);
        machine.OnEndKey(200, true);
        workspace.Store.Save(route);
        var recorded = new ManifestStore(workspace.Store.RootDirectory).Load(route.RouteId);
        Assert.Equal(SegmentStatus.Recorded, recorded.Segments[0].Status);
        Assert.Null(recorded.Segments[0].SaveRequestId);
        var request = Request(route, 210);
        machine.BeginSave(request, [route.Segments[0].Id]);
        workspace.Store.Save(route); // Persist this before dispatching the OBS request.
        var pending = workspace.Store.Load(route.RouteId);
        Assert.Equal(request.Id, Assert.Single(pending.SaveRequests).Id);
        Assert.Equal(request.Id, pending.Segments[0].SaveRequestId);
        machine.OnSaveSucceeded(request.Id, Replay(request));
        workspace.Store.Save(route);
        var saved = workspace.Store.Load(route.RouteId);
        Assert.Equal(RouteManifest.CurrentSchemaVersion, saved.SchemaVersion);
        Assert.Equal(SegmentStatus.Saved, saved.Segments[0].Status);
        Assert.Equal(saved.Media[0].Id, saved.Segments[0].Media[0].MediaId);
        Assert.Equal(route.ObsSessions[0].Clock, saved.ObsSessions[0].Clock);
    }

    [Fact]
    public void StartupRecoveryKeepsSavedSegmentsAndMarksUnsentBoundariesAsMissingMedia()
    {
        var route = NewRoute();
        var machine = Machine(route);
        machine.OnGameplayStable(100);
        machine.OnEndKey(200, true);
        var request = Request(route, 210);
        machine.BeginSave(request, [route.Segments[0].Id]);
        machine.OnSaveSucceeded(request.Id, Replay(request));
        machine.OnEndKey(300, true);
        workspace.Store.Save(route);
        new StartupRecovery(workspace.Store).Recover();
        var recovered = workspace.Store.Load(route.RouteId);
        Assert.Equal(RouteStatus.Interrupted, recovered.Status);
        Assert.Null(recovered.CurrentLife);
        Assert.Equal(SegmentStatus.Saved, recovered.Segments[0].Status);
        Assert.Single(recovered.Segments[0].Media);
        Assert.Equal(SegmentStatus.SaveFailed, recovered.Segments[1].Status);
        Assert.Empty(recovered.Segments[1].Media);
    }
}
