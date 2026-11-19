using System.Diagnostics;
using CelesteRecHelper.Core.Media;
using CelesteRecHelper.Core.Models;
using CelesteRecHelper.Core.Obs;
using CelesteRecHelper.Core.Persistence;
using CelesteRecHelper.Worker.Media;

namespace CelesteRecHelper.Core.Tests;

public sealed class HandoffVerifierTests : IDisposable
{
    private readonly TestWorkspace workspace = new();
    public void Dispose() => workspace.Dispose();

    [MediaToolsFact]
    public async Task DecodedVideosWithKnownOverlapProduceMatchingSeamTimesAndPersistedEvidence()
    {
        var original = Path.Combine(workspace.VideoDirectory, "source.mkv");
        var replay = Path.Combine(workspace.VideoDirectory, "缓存 片段.mkv");
        var record = Path.Combine(workspace.VideoDirectory, "录制 片段.mkv");
        await Ffmpeg("-f", "lavfi", "-i", "testsrc2=size=96x64:rate=20:duration=8", "-c:v", "ffv1", "-g", "1", original);
        await Ffmpeg("-i", original, "-t", "5", "-c", "copy", replay);
        await Ffmpeg("-ss", "3", "-i", original, "-t", "5", "-c", "copy", record);
        var capture = Capture(replay, record);
        var folder = Path.Combine(workspace.Root, "report");
        var result = await new HandoffVerifier(MediaToolsFactAttribute.Tool("ffmpeg"), MediaToolsFactAttribute.Tool("ffprobe"))
            .VerifyAsync(capture, folder);
        Assert.Equal(OverlapStatus.Verified, result.Evidence.Status);
        Assert.InRange(result.Evidence.OverlapSeconds, 1.9, 2.1);
        Assert.Equal(3, result.Evidence.ReplaySeamSeconds!.Value - result.Evidence.RecordingSeamSeconds!.Value, 5);
        Assert.True(File.Exists(Path.Combine(folder, "replay-frames.json")));
        Assert.True(File.Exists(Path.Combine(folder, "recording-frames.json")));
        var saved = AtomicJsonFile.Read<HandoffVerification>(Path.Combine(folder, "verification.json"));
        Assert.Equal(result.Evidence, saved.Evidence);
        Assert.Equal(15, saved.MinimumSafetyMarginSeconds);
    }

    [MediaToolsFact]
    public async Task DisjointDecodedVideosRemainInconclusive()
    {
        var original = Path.Combine(workspace.VideoDirectory, "source.mkv");
        var replay = Path.Combine(workspace.VideoDirectory, "first.mkv");
        var record = Path.Combine(workspace.VideoDirectory, "later.mkv");
        await Ffmpeg("-f", "lavfi", "-i", "testsrc2=size=96x64:rate=20:duration=8", "-c:v", "ffv1", "-g", "1", original);
        await Ffmpeg("-i", original, "-t", "2", "-c", "copy", replay);
        await Ffmpeg("-ss", "5", "-i", original, "-t", "2", "-c", "copy", record);
        var verifier = new HandoffVerifier(MediaToolsFactAttribute.Tool("ffmpeg"), MediaToolsFactAttribute.Tool("ffprobe"));
        var result = await verifier.VerifyAsync(Capture(replay, record), Path.Combine(workspace.Root, "report"));
        Assert.Equal(OverlapStatus.Inconclusive, result.Evidence.Status);
        Assert.Null(result.Evidence.ReplaySeamSeconds);
        Assert.Null(result.Evidence.RecordingSeamSeconds);
    }

    private static HandoffCapture Capture(string replay, string record) => new(Guid.NewGuid(), 1000,
        new ObsDiagnostics("test", "test", "test", Path.GetDirectoryName(replay)!, false, false, true,
            new ReplayConfiguration("Advanced", true, 300, 0, "ffv1", true, true, true),
            new ReplayCapacity(300, 210, true, "test"), []), 2,
        new SavedReplay(replay, 5000, 5200, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, new FileInfo(replay).Length),
        new SavedRecording(record, new RecordingStarted(2900, 3000, DateTimeOffset.UtcNow), 8000, 8100, DateTimeOffset.UtcNow));

    private static async Task Ffmpeg(params string[] arguments)
    {
        var info = new ProcessStartInfo(MediaToolsFactAttribute.Tool("ffmpeg"))
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true
        };
        foreach (var argument in new[] { "-v", "error", "-y" }.Concat(arguments)) info.ArgumentList.Add(argument);
        using var process = Process.Start(info)!;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var error = process.StandardError.ReadToEndAsync(deadline.Token);
        var output = process.StandardOutput.ReadToEndAsync(deadline.Token);
        try { await process.WaitForExitAsync(deadline.Token); }
        finally { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        await output;
        Assert.True(process.ExitCode == 0, await error);
    }
}
