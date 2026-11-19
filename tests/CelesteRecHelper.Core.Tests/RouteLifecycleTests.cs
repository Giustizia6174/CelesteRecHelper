using CelesteRecHelper.Core.Models;
using CelesteRecHelper.Core.Recording;

namespace CelesteRecHelper.Core.Tests;

public sealed class RouteLifecycleTests : IDisposable
{
    private readonly TestWorkspace workspace = new();

    public void Dispose() => workspace.Dispose();

    [Fact]
    public void SameSaveMapAndModeIdentifyOneLogicalRoute()
    {
        var first = new RouteIdentity(2, "Celeste/LostLevels", 0);
        var replacementSession = new RouteIdentity(2, "Celeste/LostLevels", 0);

        Assert.Equal(first, replacementSession);
    }

    [Theory]
    [InlineData(GameplayExitMode.Restart)]
    [InlineData(GameplayExitMode.GoldenBerryRestart)]
    public void RestartModesEndTheLogicalRoute(GameplayExitMode mode)
    {
        Assert.Equal(RouteExitDisposition.End, RouteExitPolicy.Decide(mode));
    }

    [Theory]
    [InlineData(GameplayExitMode.SaveAndQuit)]
    [InlineData(GameplayExitMode.GiveUp)]
    public void LeavingModesEndTheLogicalRoute(GameplayExitMode mode)
    {
        Assert.Equal(RouteExitDisposition.End, RouteExitPolicy.Decide(mode));
    }

    [Theory]
    [InlineData(GameplayExitMode.Completed)]
    [InlineData(GameplayExitMode.CompletedInterlude)]
    public void CompletionModesEnterTheCompletionLifecycle(GameplayExitMode mode)
    {
        Assert.Equal(RouteExitDisposition.Complete, RouteExitPolicy.Decide(mode));
    }

    [Fact]
    public void DifferentSaveMapOrModeStartsAnotherLogicalRoute()
    {
        var identity = new RouteIdentity(2, "Celeste/LostLevels", 0);

        Assert.NotEqual(identity, new RouteIdentity(1, identity.MapSid, identity.AreaMode));
        Assert.NotEqual(identity, new RouteIdentity(identity.SaveSlot, "Celeste/7-TheSummit", identity.AreaMode));
        Assert.NotEqual(identity, new RouteIdentity(identity.SaveSlot, identity.MapSid, 1));
    }

    [Fact]
    public void EmptyClosedRouteIsDeletedAfterItsObsSessionStopped()
    {
        var route = NewClosedEmptyRoute();
        workspace.Store.Save(route);

        Assert.True(workspace.Store.DeleteIfEmptyClosedRoute(route.RouteId));
        Assert.False(File.Exists(workspace.Store.GetManifestPath(route.RouteId)));
        Assert.DoesNotContain(route.RouteId, workspace.Store.EnumerateRouteIds());
    }

    [Fact]
    public void ClosedRouteWithSegmentsIsRetained()
    {
        var route = NewClosedEmptyRoute();
        route.Segments.Add(new LogicalSegment
        {
            LifeId = Guid.NewGuid(), ObsSessionId = route.ObsSessions[0].Id, StartTimestamp = 100, EndTimestamp = 200
        });
        workspace.Store.Save(route);

        Assert.False(workspace.Store.DeleteIfEmptyClosedRoute(route.RouteId));
        Assert.True(File.Exists(workspace.Store.GetManifestPath(route.RouteId)));
    }

    [Fact]
    public void ClosedRouteWithMediaIsRetained()
    {
        var route = NewClosedEmptyRoute();
        route.Media.Add(new MediaAsset
        {
            ObsSessionId = route.ObsSessions[0].Id, FilePath = Path.Combine(workspace.VideoDirectory, "saved.mkv"),
            Kind = MediaKind.ReplayBuffer
        });
        workspace.Store.Save(route);

        Assert.False(workspace.Store.DeleteIfEmptyClosedRoute(route.RouteId));
        Assert.True(File.Exists(workspace.Store.GetManifestPath(route.RouteId)));
    }

    [Fact]
    public void ClosedRouteWithPendingRecoveryRequestIsRetained()
    {
        var route = NewClosedEmptyRoute();
        route.SaveRequests.Add(new ReplaySaveRequest
        {
            ObsSessionId = route.ObsSessions[0].Id, ExpectedDirectory = workspace.VideoDirectory,
            RequestedAtTimestamp = 200, RequestedAtUtc = DateTimeOffset.UtcNow,
            DeadlineUtc = DateTimeOffset.UtcNow.AddSeconds(10), Status = SaveRequestStatus.Pending
        });
        workspace.Store.Save(route);

        Assert.False(workspace.Store.DeleteIfEmptyClosedRoute(route.RouteId));
        Assert.True(File.Exists(workspace.Store.GetManifestPath(route.RouteId)));
    }

    [Fact]
    public void RouteWithActiveObsSessionIsRetained()
    {
        var route = NewClosedEmptyRoute();
        route.ObsSessions[0].ReplayBufferWasActive = true;
        route.ObsSessions[0].EndedAtUtc = null;
        workspace.Store.Save(route);

        Assert.False(workspace.Store.DeleteIfEmptyClosedRoute(route.RouteId));
        Assert.True(File.Exists(workspace.Store.GetManifestPath(route.RouteId)));
    }

    [Fact]
    public void RouteWithActiveRecordingSessionIsRetained()
    {
        var route = NewClosedEmptyRoute();
        route.ObsSessions[0].RecordingWasActive = true;
        workspace.Store.Save(route);

        Assert.False(workspace.Store.DeleteIfEmptyClosedRoute(route.RouteId));
        Assert.True(File.Exists(workspace.Store.GetManifestPath(route.RouteId)));
    }

    private RouteManifest NewClosedEmptyRoute()
    {
        var route = workspace.NewRoute();
        route.Status = RouteStatus.Ended;
        route.CurrentLife = null;
        route.ObsSessions[0].ReplayBufferWasActive = false;
        route.ObsSessions[0].EndedAtUtc = DateTimeOffset.UtcNow;
        return route;
    }

}
