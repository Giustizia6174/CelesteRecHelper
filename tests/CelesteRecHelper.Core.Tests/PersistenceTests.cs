using System.Text.Json;
using CelesteRecHelper.Core.Models;
using CelesteRecHelper.Core.Persistence;

namespace CelesteRecHelper.Core.Tests;

public sealed class PersistenceTests : IDisposable
{
    private readonly TestWorkspace workspace = new();
    public void Dispose() => workspace.Dispose();

    [Fact]
    public void ManifestRoundTripsAcrossStoreInstancesWithRawTimesAndSharedMedia()
    {
        var route = workspace.NewRoute();
        var request = workspace.AddPendingSave(route);
        request.Status = SaveRequestStatus.Saved;
        var media = new MediaAsset
        {
            ObsSessionId = request.ObsSessionId, FilePath = workspace.CreateVideo(), Kind = MediaKind.ReplayBuffer,
            SaveRequestId = request.Id, SaveRequestedAtTimestamp = 500, SaveEventAtTimestamp = 520,
            SaveEventAtUtc = DateTimeOffset.UtcNow
        };
        route.Media.Add(media);
        route.Segments[0].Status = SegmentStatus.Saved;
        route.Segments[0].Media.Add(new MediaReference { MediaId = media.Id, StartTimestamp = 100, EndTimestamp = 500 });
        route.CurrentLife!.PendingMedia.Add(new MediaReference { MediaId = media.Id, StartTimestamp = 500, EndTimestamp = 510 });
        workspace.Store.Save(route);

        var loaded = new ManifestStore(workspace.Store.RootDirectory).Load(route.RouteId);
        Assert.Equal("遗忘之城", loaded.MapName);
        Assert.Equal(route.ObsSessions[0].Clock, loaded.ObsSessions[0].Clock);
        Assert.Equal(520, loaded.Media[0].SaveEventAtTimestamp);
        Assert.Equal(500, loaded.Media[0].SaveRequestedAtTimestamp);
        Assert.Equal(loaded.Segments[0].Media[0].MediaId, loaded.CurrentLife!.PendingMedia[0].MediaId);
        Assert.Equal(media.FilePath, loaded.Media[0].FilePath);
        Assert.Contains("\"status\": \"saved\"", File.ReadAllText(workspace.Store.GetManifestPath(route.RouteId)));
    }

    [Fact]
    public void FailedSerializationLeavesThePreviousDocumentIntactAndRemovesTemporaryFile()
    {
        var path = Path.Combine(workspace.Root, "atomic.json");
        File.WriteAllText(path, "previous complete document");
        Assert.Throws<InvalidOperationException>(() => AtomicJsonFile.Write(path, new Unserializable()));
        Assert.Equal("previous complete document", File.ReadAllText(path));
        Assert.Empty(Directory.EnumerateFiles(workspace.Root, "*.tmp"));
    }

    private sealed class Unserializable
    {
        public string Value => throw new InvalidOperationException("Injected serialization failure.");
    }

    [Fact]
    public void ReplacingAManifestPublishesTheNewCompleteDocument()
    {
        var route = workspace.NewRoute();
        workspace.Store.Save(route);
        workspace.Store.Save(route with { Status = RouteStatus.Completed, MapName = "已通关" });
        var loaded = workspace.Store.Load(route.RouteId);
        Assert.Equal(RouteStatus.Completed, loaded.Status);
        Assert.Equal("已通关", loaded.MapName);
        Assert.Empty(Directory.EnumerateFiles(Path.GetDirectoryName(workspace.Store.GetManifestPath(route.RouteId))!, "*.tmp"));
    }

    [Fact]
    public void InterruptedTemporaryWriteIsNotEnumeratedAsARoute()
    {
        var id = Guid.NewGuid();
        var directory = Path.GetDirectoryName(workspace.Store.GetManifestPath(id))!;
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, ".manifest.json.partial.tmp"), "{broken");
        Assert.Empty(workspace.Store.EnumerateRouteIds());
    }

    [Fact]
    public void UnsupportedSchemaIsRejectedBeforeReplacingTheExistingFile()
    {
        var route = workspace.NewRoute();
        workspace.Store.Save(route);
        var before = File.ReadAllBytes(workspace.Store.GetManifestPath(route.RouteId));
        Assert.Throws<InvalidDataException>(() => workspace.Store.Save(route with { SchemaVersion = 999 }));
        Assert.Equal(before, File.ReadAllBytes(workspace.Store.GetManifestPath(route.RouteId)));
    }

    [Fact]
    public void AManifestCannotImpersonateAnotherRouteDirectory()
    {
        var route = workspace.NewRoute();
        var incorrectId = Guid.NewGuid();
        AtomicJsonFile.Write(workspace.Store.GetManifestPath(incorrectId), route);
        Assert.Throws<InvalidDataException>(() => workspace.Store.Load(incorrectId));
    }

    [Fact]
    public void NullCollectionsAndInvalidIntervalsAreRejected()
    {
        var route = workspace.NewRoute();
        Assert.Throws<InvalidDataException>(() => workspace.Store.Save(route with { Segments = null! }));
        workspace.AddPendingSave(route);
        route.Segments[0] = route.Segments[0] with { EndTimestamp = 0 };
        Assert.Throws<InvalidDataException>(() => workspace.Store.Save(route));
    }

    [Fact]
    public void ForeignMediaReferencesAndDuplicateIdsAreRejected()
    {
        var route = workspace.NewRoute();
        workspace.AddPendingSave(route);
        route.Segments[0].Media.Add(new MediaReference { MediaId = Guid.NewGuid(), StartTimestamp = 100, EndTimestamp = 500 });
        Assert.Throws<InvalidDataException>(() => workspace.Store.Save(route));
        route.Segments[0].Media.Clear();
        route.ObsSessions.Add(route.ObsSessions[0]);
        Assert.Throws<InvalidDataException>(() => workspace.Store.Save(route));
    }

    [Fact]
    public void NullJsonAndUnknownEnumsAreRejected()
    {
        var route = workspace.NewRoute();
        var path = workspace.Store.GetManifestPath(route.RouteId);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "null");
        Assert.Throws<InvalidDataException>(() => workspace.Store.Load(route.RouteId));
        workspace.Store.Save(route);
        File.WriteAllText(path, File.ReadAllText(path).Replace("\"recording\"", "\"unknown-state\""));
        Assert.Throws<JsonException>(() => workspace.Store.Load(route.RouteId));
    }
}
