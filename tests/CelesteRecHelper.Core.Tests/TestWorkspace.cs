using CelesteRecHelper.Core.Models;
using CelesteRecHelper.Core.Persistence;

namespace CelesteRecHelper.Core.Tests;

public sealed class TestWorkspace : IDisposable
{
    private readonly string directoryName = $"CelesteRecHelper-tests-{Guid.NewGuid():N}";
    public string Root { get; }
    public string VideoDirectory { get; }
    public ManifestStore Store { get; }

    public TestWorkspace()
    {
        Root = Path.Combine(Path.GetTempPath(), directoryName);
        VideoDirectory = Path.Combine(Root, "录像素材");
        Directory.CreateDirectory(VideoDirectory);
        Store = new ManifestStore(Path.Combine(Root, "Saves", "CelesteRecHelper"));
    }

    public RouteManifest NewRoute()
    {
        var session = new ObsSession
        {
            Endpoint = "ws://127.0.0.1:4455", ProfileName = "测试配置", OutputDirectory = VideoDirectory,
            ReplayBufferWasActive = true
        };
        return new RouteManifest
        {
            SaveSlot = 2, MapSid = "Celeste/1-ForsakenCity", MapName = "遗忘之城", ObsSessions = [session],
            CurrentLife = new CurrentLife
            {
                ObsSessionId = session.Id, StartedAtTimestamp = 100, PendingStartTimestamp = 100
            }
        };
    }

    public ReplaySaveRequest AddPendingSave(RouteManifest route)
    {
        var request = new ReplaySaveRequest
        {
            ObsSessionId = route.ObsSessions[0].Id, ExpectedDirectory = VideoDirectory,
            RequestedAtTimestamp = 500, RequestedAtUtc = DateTimeOffset.UtcNow.AddSeconds(-5),
            DeadlineUtc = DateTimeOffset.UtcNow.AddSeconds(10)
        };
        route.SaveRequests.Add(request);
        route.Segments.Add(new LogicalSegment
        {
            LifeId = route.CurrentLife!.Id, ObsSessionId = request.ObsSessionId,
            StartTimestamp = 100, EndTimestamp = 500, SaveRequestId = request.Id
        });
        return request;
    }

    public string CreateVideo(string name = "replay.mkv", bool empty = false)
    {
        var path = Path.Combine(VideoDirectory, name);
        File.WriteAllBytes(path, empty ? [] : [1, 2, 3]);
        return path;
    }

    public void Dispose()
    {
        var expected = Path.GetFullPath(Path.Combine(Path.GetTempPath(), directoryName));
        if (!string.Equals(Path.GetFullPath(Root), expected, StringComparison.Ordinal) ||
            !Path.GetFileName(Root).StartsWith("CelesteRecHelper-tests-", StringComparison.Ordinal))
            throw new InvalidOperationException("Refusing to delete an unexpected test directory.");
        Directory.Delete(Root, recursive: true);
    }
}
