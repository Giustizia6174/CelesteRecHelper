using CelesteRecHelper.Core.Models;
using CelesteRecHelper.Core.Persistence;

namespace CelesteRecHelper.Core.Tests;

public sealed class StartupRecoveryTests : IDisposable
{
    private readonly TestWorkspace workspace = new();
    public void Dispose() => workspace.Dispose();
    private RecoveryReport Recover() => new StartupRecovery(new ManifestStore(workspace.Store.RootDirectory)).Recover();

    [Fact]
    public void CrashedRouteIsInterruptedWhileSavedSegmentsAndMaterialsSurvive()
    {
        var route = workspace.NewRoute();
        var request = workspace.AddPendingSave(route);
        request.Status = SaveRequestStatus.Saved;
        route.Segments[0].Status = SegmentStatus.Saved;
        var path = workspace.CreateVideo();
        var media = new MediaAsset { ObsSessionId = request.ObsSessionId, FilePath = path, Kind = MediaKind.ReplayBuffer };
        route.Media.Add(media);
        route.Segments[0].Media.Add(new MediaReference { MediaId = media.Id, StartTimestamp = 100, EndTimestamp = 500 });
        workspace.Store.Save(route);

        var report = Recover();
        var loaded = workspace.Store.Load(route.RouteId);
        Assert.Equal(RouteStatus.Interrupted, loaded.Status);
        Assert.Null(loaded.CurrentLife);
        Assert.Equal(SegmentStatus.Saved, loaded.Segments[0].Status);
        Assert.Equal(media, loaded.Media[0]);
        Assert.True(File.Exists(path));
        Assert.True(report.OutputsMayStillBeActive);
        Assert.Single(report.InterruptedRoutes);
        var before = File.ReadAllBytes(workspace.Store.GetManifestPath(route.RouteId));
        Assert.Empty(Recover().InterruptedRoutes);
        Assert.Equal(before, File.ReadAllBytes(workspace.Store.GetManifestPath(route.RouteId)));
    }

    [Fact]
    public void UniqueReplayRecoversAllCoalescedEndpointsWithoutInventingAnEventTime()
    {
        var route = workspace.NewRoute();
        var request = workspace.AddPendingSave(route);
        route.Segments.Add(route.Segments[0] with { Id = Guid.NewGuid(), StartTimestamp = 500, EndTimestamp = 600, Media = [] });
        var path = workspace.CreateVideo();
        workspace.Store.Save(route);

        var report = Recover();
        var loaded = workspace.Store.Load(route.RouteId);
        Assert.Equal(1, report.RecoveredSaves);
        Assert.Equal(SaveRequestStatus.Saved, loaded.SaveRequests[0].Status);
        Assert.Equal(path, loaded.SaveRequests[0].OutputPath);
        var media = Assert.Single(loaded.Media);
        Assert.True(media.RecoveredAfterCrash);
        Assert.Equal(500, media.SaveRequestedAtTimestamp);
        Assert.Null(media.SaveEventAtTimestamp);
        Assert.Null(media.SaveEventAtUtc);
        Assert.All(loaded.Segments, s => {
            Assert.Equal(SegmentStatus.Saved, s.Status);
            Assert.Equal(media.Id, Assert.Single(s.Media).MediaId);
        });
        Assert.Equal(0, Recover().RecoveredSaves);
        Assert.Single(workspace.Store.Load(route.RouteId).Media);
    }

    [Fact]
    public void MissingReplayMarksEndpointsFailedAndKeepsTheirLogicalRanges()
    {
        var route = workspace.NewRoute();
        workspace.AddPendingSave(route);
        workspace.Store.Save(route);
        Assert.Equal(1, Recover().FailedSaves);
        var loaded = workspace.Store.Load(route.RouteId);
        Assert.Equal(SaveRequestStatus.Failed, loaded.SaveRequests[0].Status);
        Assert.Equal(SegmentStatus.SaveFailed, loaded.Segments[0].Status);
        Assert.Equal(100, loaded.Segments[0].StartTimestamp);
        Assert.Equal(500, loaded.Segments[0].EndTimestamp);
        Assert.Empty(loaded.Media);
    }

    [Fact]
    public void MultipleReplayCandidatesArePreservedAndReportedAsAmbiguous()
    {
        var route = workspace.NewRoute();
        workspace.AddPendingSave(route);
        var one = workspace.CreateVideo("one.mkv");
        var two = workspace.CreateVideo("two.mp4");
        workspace.Store.Save(route);
        Assert.Equal(1, Recover().FailedSaves);
        Assert.Empty(workspace.Store.Load(route.RouteId).Media);
        Assert.True(File.Exists(one));
        Assert.True(File.Exists(two));
    }

    [Fact]
    public void PreexistingAndEmptyFilesCannotSatisfyAPendingRequest()
    {
        var route = workspace.NewRoute();
        var request = workspace.AddPendingSave(route);
        request.ExistingFiles.Add(workspace.CreateVideo("existing.mkv"));
        workspace.CreateVideo("incomplete.mp4", empty: true);
        workspace.Store.Save(route);
        Assert.Equal(1, Recover().FailedSaves);
        Assert.Empty(workspace.Store.Load(route.RouteId).Media);
    }

    [Fact]
    public void FilesOutsideTheSaveTimeWindowAreIgnored()
    {
        var route = workspace.NewRoute();
        workspace.AddPendingSave(route);
        var old = workspace.CreateVideo("old.mkv");
        File.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddHours(-1));
        var late = workspace.CreateVideo("late.mkv");
        File.SetLastWriteTimeUtc(late, DateTime.UtcNow.AddHours(1));
        workspace.Store.Save(route);
        Assert.Equal(1, Recover().FailedSaves);
    }

    [Fact]
    public void AnotherRoutesRegisteredMaterialCannotBeAdopted()
    {
        var saved = workspace.NewRoute() with { Status = RouteStatus.Completed, CurrentLife = null };
        saved.Media.Add(new MediaAsset
        {
            ObsSessionId = saved.ObsSessions[0].Id, FilePath = workspace.CreateVideo(), Kind = MediaKind.ReplayBuffer
        });
        workspace.Store.Save(saved);
        var pending = workspace.NewRoute();
        workspace.AddPendingSave(pending);
        workspace.Store.Save(pending);
        Assert.Equal(1, Recover().FailedSaves);
        Assert.Empty(workspace.Store.Load(pending.RouteId).Media);
    }

    [Fact]
    public void AFileMatchingTwoRequestsCannotBeAssignedToEither()
    {
        var first = workspace.NewRoute();
        workspace.AddPendingSave(first);
        workspace.Store.Save(first);
        var second = workspace.NewRoute();
        workspace.AddPendingSave(second);
        workspace.Store.Save(second);
        workspace.CreateVideo();
        Assert.Equal(2, Recover().FailedSaves);
        Assert.Empty(workspace.Store.Load(first.RouteId).Media);
        Assert.Empty(workspace.Store.Load(second.RouteId).Media);
    }

    [Fact]
    public void QueuedEndpointWithoutAnIssuedRequestIsMarkedFailed()
    {
        var route = workspace.NewRoute();
        workspace.AddPendingSave(route);
        route.Segments[0].SaveRequestId = null;
        route.SaveRequests.Clear();
        workspace.Store.Save(route);
        Recover();
        Assert.Equal(SegmentStatus.SaveFailed, workspace.Store.Load(route.RouteId).Segments[0].Status);
    }

    [Theory]
    [InlineData(RouteStatus.Completed)]
    [InlineData(RouteStatus.Ended)]
    [InlineData(RouteStatus.Interrupted)]
    public void FinishedRoutesKeepTheirStatus(RouteStatus status)
    {
        var route = workspace.NewRoute() with { Status = status, CurrentLife = null };
        workspace.Store.Save(route);
        Assert.Empty(Recover().InterruptedRoutes);
        Assert.Equal(status, workspace.Store.Load(route.RouteId).Status);
    }

    [Fact]
    public void CorruptAndFutureManifestsRemainUntouchedWhileHealthyRoutesRecover()
    {
        var healthy = workspace.NewRoute();
        workspace.Store.Save(healthy);
        var corruptPath = workspace.Store.GetManifestPath(Guid.NewGuid());
        Directory.CreateDirectory(Path.GetDirectoryName(corruptPath)!);
        File.WriteAllText(corruptPath, "{broken json");
        var future = workspace.NewRoute() with { SchemaVersion = 999 };
        AtomicJsonFile.Write(workspace.Store.GetManifestPath(future.RouteId), future);
        var before = File.ReadAllBytes(workspace.Store.GetManifestPath(future.RouteId));
        var report = Recover();
        Assert.Equal(2, report.Warnings.Count);
        Assert.Single(report.InterruptedRoutes);
        Assert.Equal("{broken json", File.ReadAllText(corruptPath));
        Assert.Equal(before, File.ReadAllBytes(workspace.Store.GetManifestPath(future.RouteId)));
    }

    [Fact]
    public void ObsDirectoriesAndUnfinishedJobsSurviveRestartWithoutAutomaticRestoration()
    {
        var directories = new ObsDirectoryRecoveryRecord
        {
            Endpoint = "ws://127.0.0.1:4455", ProfileName = "Profile A",
            OriginalSimpleDirectory = "D:\\simple", OriginalAdvancedDirectory = "E:\\advanced",
            TemporaryDirectory = workspace.VideoDirectory
        };
        workspace.Store.SaveObsRecovery(directories);
        foreach (var state in Enum.GetValues<BackgroundJobState>())
            workspace.Store.SaveJobStatus(new BackgroundJobStatus { JobId = Guid.NewGuid(), RouteId = Guid.NewGuid(), State = state });
        var report = Recover();
        Assert.Equal(directories, report.ObsDirectoryRecovery);
        Assert.True(File.Exists(workspace.Store.ObsRecoveryPath));
        Assert.Equal(3, report.UnfinishedJobs.Count);
        Assert.DoesNotContain(report.UnfinishedJobs, s => s.State == BackgroundJobState.Succeeded);
        Assert.Equal(4, workspace.Store.EnumerateJobStatusPaths().Count());
    }
}
