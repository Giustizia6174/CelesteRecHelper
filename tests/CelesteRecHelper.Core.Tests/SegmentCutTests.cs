using System.Text.Json;
using CelesteRecHelper.Core.Models;
using CelesteRecHelper.Core.Obs;
using CelesteRecHelper.Core.Persistence;
using CelesteRecHelper.Core.Recording;

namespace CelesteRecHelper.Core.Tests;

public sealed class SegmentCutTests : IDisposable
{
    private readonly TestWorkspace workspace = new();
    public void Dispose() => workspace.Dispose();

    [Fact]
    public void ApplyingVerifiedClipsRepointsSegmentsAndReleasesTheReplaySource()
    {
        var route = workspace.NewRoute();
        var session = route.ObsSessions[0];
        var machine = new SegmentStateMachine(route, session.Id);
        var end = machine.OnEndKey(500, true);
        var request = new ReplaySaveRequest
        {
            Id = Guid.NewGuid(), ObsSessionId = session.Id, ExpectedDirectory = workspace.VideoDirectory,
            RequestedAtTimestamp = 600, RequestedAtUtc = DateTimeOffset.UtcNow.AddSeconds(-1),
            DeadlineUtc = DateTimeOffset.UtcNow.AddSeconds(10)
        };
        machine.BeginSave(request, [end.SaveCommand!.SegmentId]);
        var source = workspace.CreateVideo("source.mkv");
        Assert.True(machine.OnSaveSucceeded(request.Id, new SavedReplay(source, 600, 601,
            request.RequestedAtUtc, request.RequestedAtUtc.AddMilliseconds(1), 3)));
        var cut = machine.CreateCutRequest(request.Id);
        var clip = Path.Combine(workspace.VideoDirectory, "CelesteRecHelper", "map", "001_0.4s.mkv");
        var result = new SegmentCutResult
        {
            RouteId = route.RouteId, SaveRequestId = request.Id, SourceMediaId = cut.SourceMediaId,
            ObsSessionId = session.Id, SourcePath = source,
            Segments = [new SegmentCutItemResult
            {
                SegmentId = end.SaveCommand.SegmentId, Sequence = 1, Succeeded = true, FilePath = clip,
                Probe = Probe(clip), StartSeconds = 0, EndSeconds = 0.4
            }]
        };

        var apply = machine.ApplyCutResult(result);
        Assert.Equal(source, apply.SourcePathToDelete);
        Assert.Equal(MediaKind.SegmentClip, Assert.Single(route.Media, media => media.FilePath == clip).Kind);
        Assert.Equal(SegmentSliceStatus.Succeeded, route.Segments[0].SliceStatus);
        Assert.DoesNotContain(route.Segments[0].Media, reference => reference.MediaId == cut.SourceMediaId);
        machine.RemoveMedia(apply.SourceMediaIdToRemove!.Value);
        Assert.DoesNotContain(route.Media, media => media.FilePath == source);
    }

    [Fact]
    public void AFailedCutKeepsTheReplaySourceAndCanBeRetried()
    {
        var route = workspace.NewRoute();
        var session = route.ObsSessions[0];
        var machine = new SegmentStateMachine(route, session.Id);
        var end = machine.OnEndKey(500, true);
        var request = new ReplaySaveRequest
        {
            Id = Guid.NewGuid(), ObsSessionId = session.Id, ExpectedDirectory = workspace.VideoDirectory,
            RequestedAtTimestamp = 600, RequestedAtUtc = DateTimeOffset.UtcNow.AddSeconds(-1),
            DeadlineUtc = DateTimeOffset.UtcNow.AddSeconds(10)
        };
        machine.BeginSave(request, [end.SaveCommand!.SegmentId]);
        var source = workspace.CreateVideo("source.mkv");
        machine.OnSaveSucceeded(request.Id, new SavedReplay(source, 600, 601,
            request.RequestedAtUtc, request.RequestedAtUtc.AddMilliseconds(1), 3));
        var cut = machine.CreateCutRequest(request.Id);
        var failed = new SegmentCutResult
        {
            RouteId = route.RouteId, SaveRequestId = request.Id, SourceMediaId = cut.SourceMediaId,
            ObsSessionId = session.Id, SourcePath = source,
            Segments = [new SegmentCutItemResult
            {
                SegmentId = end.SaveCommand.SegmentId, Sequence = 1, Succeeded = false, Error = "probe failed"
            }]
        };
        var apply = machine.ApplyCutResult(failed);
        Assert.Null(apply.SourcePathToDelete);
        Assert.Equal(SegmentSliceStatus.Failed, route.Segments[0].SliceStatus);
        Assert.Contains(route.Media, media => media.FilePath == source);
    }

    [Fact]
    public void APartialBatchKeepsTheSourceUntilItsRemainingSegmentIsRetried()
    {
        var (route, machine, request) = SavedBatch();
        var firstClip = workspace.CreateVideo("first.mkv");
        var secondClip = workspace.CreateVideo("second.mkv");
        var partial = Success(request, firstClip) with
        {
            Segments = [Success(request, firstClip).Segments[0], new SegmentCutItemResult
            {
                SegmentId = request.Segments[1].SegmentId, Sequence = 2, Error = "probe failed"
            }]
        };
        Assert.Null(machine.ApplyCutResult(partial).SourcePathToDelete);
        workspace.Store.Save(route);
        Assert.Equal(0, SegmentCutFiles.RemoveUnusedReplays(machine, workspace.Store.Save));
        Assert.True(File.Exists(request.SourcePath));
        var retry = machine.CreateCutRequest(request.SaveRequestId);
        Assert.Equal(request.Segments[1].SegmentId, Assert.Single(retry.Segments).SegmentId);
        Assert.Equal(2, retry.Segments[0].Sequence);
        machine.ApplyCutResult(Success(retry, secondClip));
        workspace.Store.Save(route);
        Assert.Equal(1, SegmentCutFiles.RemoveUnusedReplays(machine, workspace.Store.Save));
        Assert.False(File.Exists(request.SourcePath));
        var durable = workspace.Store.Load(route.RouteId);
        Assert.All(durable.Media, media => Assert.Equal(MediaKind.SegmentClip, media.Kind));
        Assert.All(durable.Segments, segment =>
        {
            Assert.Equal(SegmentSliceStatus.Succeeded, segment.SliceStatus);
            var reference = Assert.Single(segment.Media);
            Assert.Equal(0.4, reference.EndSeconds - reference.StartSeconds);
            Assert.DoesNotContain(durable.Media, media => media.Id == request.SourceMediaId);
        });
    }

    [Fact]
    public void InvalidOrMissingResultsCannotPartiallyReplaceReferences()
    {
        var (route, machine, request) = SavedBatch();
        var valid = Success(request, workspace.CreateVideo("clip.mkv"));
        Assert.Throws<ArgumentException>(() => machine.ApplyCutResult(valid)); // Missing second result.
        var invalid = valid with { Segments = [valid.Segments[0], valid.Segments[0] with
        {
            SegmentId = request.Segments[1].SegmentId, Sequence = 2, StartSeconds = -1
        }] };
        Assert.Throws<ArgumentException>(() => machine.ApplyCutResult(invalid));
        Assert.All(route.Segments, segment => Assert.Equal(request.SourceMediaId, Assert.Single(segment.Media).MediaId));
        Assert.Single(route.Media);
    }

    [Fact]
    public void CleanupNeverDeletesTheSourceBeforeClipReferencesAreDurable()
    {
        var (route, machine, request) = SavedBatch();
        var result = Success(request, workspace.CreateVideo("clip.mkv")) with
        {
            Segments = request.Segments.Select(item => SuccessItem(item, workspace.CreateVideo($"clip-{item.Sequence}.mkv"))).ToList()
        };
        machine.ApplyCutResult(result);
        workspace.Store.Save(route);
        var deleted = SegmentCutFiles.RemoveUnusedReplays(machine, changed =>
        {
            var durable = workspace.Store.Load(changed.RouteId);
            Assert.All(durable.Segments, segment =>
                Assert.DoesNotContain(segment.Media, reference => reference.MediaId == request.SourceMediaId));
            Assert.False(File.Exists(request.SourcePath));
            workspace.Store.Save(changed);
        });
        Assert.Equal(1, deleted);
    }

    private (RouteManifest Route, SegmentStateMachine Machine, SegmentCutRequest Request) SavedBatch()
    {
        var route = workspace.NewRoute();
        var machine = new SegmentStateMachine(route, route.ObsSessions[0].Id);
        var first = machine.OnEndKey(300, true);
        var second = machine.OnEndKey(500, true);
        var request = new ReplaySaveRequest
        {
            ObsSessionId = machine.ObsSessionId, ExpectedDirectory = workspace.VideoDirectory,
            RequestedAtTimestamp = 600, RequestedAtUtc = DateTimeOffset.UtcNow.AddSeconds(-1),
            DeadlineUtc = DateTimeOffset.UtcNow.AddSeconds(10)
        };
        machine.BeginSave(request, [first.SaveCommand!.SegmentId, second.SaveCommand!.SegmentId]);
        machine.OnSaveSucceeded(request.Id, new SavedReplay(workspace.CreateVideo("source.mkv"), 600, 601,
            request.RequestedAtUtc, request.RequestedAtUtc.AddMilliseconds(1), 3));
        return (route, machine, machine.CreateCutRequest(request.Id));
    }

    internal static SegmentCutResult Success(SegmentCutRequest request, string path) => new()
    {
        RouteId = request.RouteId, SaveRequestId = request.SaveRequestId, SourceMediaId = request.SourceMediaId,
        ObsSessionId = request.ObsSessionId, SourcePath = request.SourcePath,
        Segments = [SuccessItem(request.Segments[0], path)]
    };

    private static SegmentCutItemResult SuccessItem(SegmentCutItem item, string path) => new()
    {
        SegmentId = item.SegmentId, Sequence = item.Sequence, Succeeded = true, FilePath = path,
        Probe = Probe(path), StartSeconds = 0, EndSeconds = 0.4
    };

    private static MediaProbeResult Probe(string path) => new(path, 10, 0.5, 0, 0.4, 64, 64,
        "mpeg4", "10/1", [], JsonDocument.Parse("{\"streams\":[{\"codec_type\":\"video\"}]}" ).RootElement.Clone());
}
