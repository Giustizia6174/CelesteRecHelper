using CelesteRecHelper.Core.Models;
using CelesteRecHelper.Core.Persistence;

namespace CelesteRecHelper.Core.Tests;

public sealed class SegmentCutRecoveryTests : IDisposable
{
    private readonly TestWorkspace workspace = new();
    public void Dispose() => workspace.Dispose();

    [Theory]
    [InlineData(BackgroundJobState.Pending)]
    [InlineData(BackgroundJobState.Running)]
    [InlineData(BackgroundJobState.Failed)]
    [InlineData(BackgroundJobState.Succeeded)]
    public async Task RestartReusesAJobAndPublishesClipReferencesBeforeDeletingItsReplay(BackgroundJobState state)
    {
        var (route, source) = SavedRoute();
        var request = new Core.Recording.SegmentStateMachine(route, route.ObsSessions[0].Id).CreateCutRequest(route.SaveRequests[0].Id);
        var id = Guid.NewGuid();
        var requestPath = workspace.Store.GetJobRequestPath(id);
        AtomicJsonFile.Write(requestPath, request);
        workspace.Store.SaveJobStatus(new BackgroundJobStatus
        {
            JobId = id, RouteId = route.RouteId, State = state, Kind = "segment-cut", RequestPath = requestPath,
            ResultPath = workspace.Store.GetJobResultPath(id)
        });
        var calls = 0;
        var jobs = new SegmentCutJobRunner(workspace.Store, (input, output, _) =>
        {
            calls++;
            Assert.Equal(requestPath, input);
            var snapshot = AtomicJsonFile.Read<SegmentCutRequest>(input);
            AtomicJsonFile.Write(output, SegmentCutTests.Success(snapshot, workspace.CreateVideo("clip.mkv")));
            return Task.CompletedTask;
        });
        new StartupRecovery(workspace.Store).Recover();
        var report = await new SegmentCutRecovery(workspace.Store).RecoverAsync(jobs.CutAsync);
        Assert.Equal(1, calls);
        Assert.Equal(1, report.SucceededSegments);
        Assert.Equal(1, report.RemovedReplays);
        Assert.Empty(report.Warnings);
        Assert.False(File.Exists(source));
        var durable = workspace.Store.Load(route.RouteId);
        Assert.Equal(SegmentSliceStatus.Succeeded, durable.Segments[0].SliceStatus);
        Assert.Equal(MediaKind.SegmentClip, Assert.Single(durable.Media).Kind);
        Assert.Equal(BackgroundJobState.Succeeded, AtomicJsonFile.Read<BackgroundJobStatus>(workspace.Store.EnumerateJobStatusPaths().Single()).State);
        Assert.Single(workspace.Store.EnumerateJobStatusPaths());
        Assert.Equal(0, (await new SegmentCutRecovery(workspace.Store).RecoverAsync(jobs.CutAsync)).SucceededSegments);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task SavedReplayWithoutAJobIsRecoveredAndWorkerFailureRetainsTheSource()
    {
        var (route, source) = SavedRoute();
        var recovery = new SegmentCutRecovery(workspace.Store);
        var failed = await recovery.RecoverAsync((_, _) => throw new IOException("worker unavailable"));
        Assert.Equal(1, failed.FailedSegments);
        Assert.True(File.Exists(source));
        var durable = workspace.Store.Load(route.RouteId);
        Assert.Equal(SaveRequestStatus.Saved, durable.SaveRequests[0].Status);
        Assert.Equal(SegmentSliceStatus.Failed, durable.Segments[0].SliceStatus);
        Assert.Equal(1, (await recovery.RecoverAsync((request, _) => Task.FromResult(
            SegmentCutTests.Success(request, workspace.CreateVideo("retry.mkv"))))).SucceededSegments);
        Assert.False(File.Exists(source));
    }

    [Fact]
    public async Task RestartFinishesCleanupAfterReferencesWereSavedAndSourceDeletionWasInterrupted()
    {
        var (route, source) = SavedRoute();
        var machine = new Core.Recording.SegmentStateMachine(route, route.ObsSessions[0].Id);
        var request = machine.CreateCutRequest(route.SaveRequests[0].Id);
        machine.ApplyCutResult(SegmentCutTests.Success(request, workspace.CreateVideo("clip.mkv")));
        workspace.Store.Save(route);
        var report = await new SegmentCutRecovery(workspace.Store).RecoverAsync((_, _) =>
            throw new InvalidOperationException("No new cut should run."));
        Assert.Equal(1, report.RemovedReplays);
        Assert.False(File.Exists(source));
        Assert.Equal(MediaKind.SegmentClip, Assert.Single(workspace.Store.Load(route.RouteId).Media).Kind);
    }

    [Fact]
    public async Task JobStatusRecordsPerSegmentFailuresAndCanReuseTheSameJobOnRetry()
    {
        var (route, _) = SavedRoute();
        var request = new Core.Recording.SegmentStateMachine(route, route.ObsSessions[0].Id).CreateCutRequest(route.SaveRequests[0].Id);
        var attempts = 0;
        var jobs = new SegmentCutJobRunner(workspace.Store, (input, output, _) =>
        {
            var snapshot = AtomicJsonFile.Read<SegmentCutRequest>(input);
            var result = SegmentCutTests.Success(snapshot, workspace.CreateVideo("clip.mkv"));
            if (attempts++ == 0) result = result with { Segments = [result.Segments[0] with { Succeeded = false, Error = "invalid media" }] };
            AtomicJsonFile.Write(output, result);
            return Task.CompletedTask;
        });
        await jobs.CutAsync(request);
        var statusPath = workspace.Store.EnumerateJobStatusPaths().Single();
        Assert.Equal(BackgroundJobState.Failed, AtomicJsonFile.Read<BackgroundJobStatus>(statusPath).State);
        await jobs.CutAsync(request);
        Assert.Equal(BackgroundJobState.Succeeded, AtomicJsonFile.Read<BackgroundJobStatus>(statusPath).State);
        Assert.Single(workspace.Store.EnumerateJobStatusPaths());
    }

    [Fact]
    public async Task DifferentReplayJobsAreRunSerially()
    {
        var (route, _) = SavedRoute();
        var request = new Core.Recording.SegmentStateMachine(route, route.ObsSessions[0].Id).CreateCutRequest(route.SaveRequests[0].Id);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var running = 0;
        var jobs = new SegmentCutJobRunner(workspace.Store, async (input, output, _) =>
        {
            Assert.Equal(1, Interlocked.Increment(ref running));
            entered.TrySetResult();
            await release.Task;
            var snapshot = AtomicJsonFile.Read<SegmentCutRequest>(input);
            AtomicJsonFile.Write(output, SegmentCutTests.Success(snapshot, workspace.CreateVideo($"clip-{snapshot.SaveRequestId:N}.mkv")));
            Interlocked.Decrement(ref running);
        });
        var first = jobs.CutAsync(request);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = jobs.CutAsync(request with { SaveRequestId = Guid.NewGuid() });
        try { Assert.False(second.IsCompleted); }
        finally { release.TrySetResult(); }
        await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task CorruptRoutesAndJobRecordsDoNotPreventHealthyCutRecovery()
    {
        var corruptId = Guid.Parse("00000000-0000-0000-0000-000000000001");
        var invalid = workspace.NewRoute() with { RouteId = corruptId, SchemaVersion = 999 };
        AtomicJsonFile.Write(workspace.Store.GetManifestPath(corruptId), invalid);
        var (route, source) = SavedRoute();
        var job = Guid.NewGuid();
        Directory.CreateDirectory(workspace.Store.GetJobDirectory(job));
        await File.WriteAllTextAsync(Path.Combine(workspace.Store.GetJobDirectory(job), "status.json"), "null");
        var jobs = new SegmentCutJobRunner(workspace.Store, (input, output, _) =>
        {
            var request = AtomicJsonFile.Read<SegmentCutRequest>(input);
            AtomicJsonFile.Write(output, SegmentCutTests.Success(request, workspace.CreateVideo("healthy.mkv")));
            return Task.CompletedTask;
        });
        var report = await new SegmentCutRecovery(workspace.Store).RecoverAsync(jobs.CutAsync);
        Assert.Single(report.Warnings);
        Assert.Equal(1, report.SucceededSegments);
        Assert.False(File.Exists(source));
        Assert.Equal(SegmentSliceStatus.Succeeded, workspace.Store.Load(route.RouteId).Segments[0].SliceStatus);
        Assert.Equal(999, AtomicJsonFile.Read<RouteManifest>(workspace.Store.GetManifestPath(corruptId)).SchemaVersion);
    }

    private (RouteManifest Route, string Source) SavedRoute()
    {
        var route = workspace.NewRoute() with { Status = RouteStatus.Ended };
        var request = workspace.AddPendingSave(route);
        route.CurrentLife = null;
        request.Status = SaveRequestStatus.Saved;
        var source = workspace.CreateVideo("source.mkv");
        request.OutputPath = source;
        var media = new MediaAsset
        {
            ObsSessionId = request.ObsSessionId, FilePath = source, Kind = MediaKind.ReplayBuffer, SaveRequestId = request.Id,
            SaveRequestedAtTimestamp = request.RequestedAtTimestamp
        };
        route.Media.Add(media);
        route.Segments[0].Status = SegmentStatus.Saved;
        route.Segments[0].Media.Add(new MediaReference { MediaId = media.Id, StartTimestamp = 100, EndTimestamp = 500 });
        workspace.Store.Save(route);
        return (route, source);
    }
}
