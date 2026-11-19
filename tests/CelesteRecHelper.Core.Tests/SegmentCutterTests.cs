using System.Diagnostics;
using CelesteRecHelper.Core.Models;
using CelesteRecHelper.Worker.Media;

namespace CelesteRecHelper.Core.Tests;

public sealed class SegmentCutterTests : IDisposable
{
    private readonly TestWorkspace workspace = new();
    public void Dispose() => workspace.Dispose();

    [MediaToolsFact]
    public async Task CutsAReplayUsingTheClockMappingAndPreservesStreams()
    {
        var source = Path.Combine(workspace.VideoDirectory, "source.mkv");
        await RunFfmpegAsync(["-v", "error", "-f", "lavfi", "-i", "testsrc=size=64x64:rate=10:duration=4",
            "-f", "lavfi", "-i", "anullsrc=r=48000:cl=stereo", "-map", "0:v", "-map", "1:a",
            "-t", "4", "-c:v", "mpeg4", "-g", "10", "-c:a", "aac", "-y", source]);
        var request = new SegmentCutRequest
        {
            RouteId = Guid.NewGuid(), SaveRequestId = Guid.NewGuid(), SourceMediaId = Guid.NewGuid(),
            ObsSessionId = Guid.NewGuid(), SourcePath = source, OutputDirectory = workspace.VideoDirectory,
            MapName = "Map: test", ClockFrequency = 1000, SaveRequestedAtTimestamp = 5000,
            Segments = [new SegmentCutItem
            {
                SegmentId = Guid.NewGuid(), Sequence = 1, StartTimestamp = 2000, EndTimestamp = 3500
            }]
        };

        var result = await new SegmentCutter(MediaToolsFactAttribute.Tool("ffmpeg"),
            MediaToolsFactAttribute.Tool("ffprobe")).CutAsync(request);
        var item = Assert.Single(result.Segments);
        Assert.True(item.Succeeded, item.Error);
        Assert.True(File.Exists(item.FilePath));
        Assert.NotNull(item.Probe);
        Assert.InRange(item.Probe!.DurationSeconds, 1.45, 2.8);
        Assert.InRange(item.EndSeconds!.Value - item.StartSeconds!.Value, 1.499, 1.501);
        Assert.InRange(item.StartSeconds.Value, 0, 1.1);
        var sourceProbe = await new MediaProbe(MediaToolsFactAttribute.Tool("ffprobe")).ProbeAsync(source);
        Assert.Equal(sourceProbe.RawMetadata.GetProperty("streams").GetArrayLength(),
            item.Probe.RawMetadata.GetProperty("streams").GetArrayLength());
        Assert.Equal(sourceProbe.AudioTracks.Count, item.Probe.AudioTracks.Count);
        Assert.True(File.Exists(source));
        Assert.Empty(Directory.EnumerateFiles(workspace.VideoDirectory, "*.lock", SearchOption.AllDirectories));
        var timestamp = File.GetLastWriteTimeUtc(item.FilePath!);
        var partial = Path.Combine(Path.GetDirectoryName(item.FilePath!)!,
            $".{Path.GetFileNameWithoutExtension(item.FilePath)}.part{Path.GetExtension(item.FilePath)}");
        await File.WriteAllTextAsync(partial, "interrupted output");
        var repeated = await new SegmentCutter(MediaToolsFactAttribute.Tool("ffmpeg"),
            MediaToolsFactAttribute.Tool("ffprobe")).CutAsync(request);
        Assert.True(repeated.Segments[0].Succeeded, repeated.Segments[0].Error);
        Assert.Equal(timestamp, File.GetLastWriteTimeUtc(item.FilePath!));
        Assert.False(File.Exists(partial));
        Assert.Empty(Directory.EnumerateFiles(workspace.VideoDirectory, "*.lock", SearchOption.AllDirectories));
    }

    [MediaToolsFact]
    public async Task BFramesAndMultipleAudioTracksKeepTheWholeIntervalFromItsPrecedingKeyframe()
    {
        var source = Path.Combine(workspace.VideoDirectory, "bframes.mkv");
        await RunFfmpegAsync(["-v", "error", "-f", "lavfi", "-i", "testsrc2=size=64x64:rate=30:duration=8",
            "-f", "lavfi", "-i", "anullsrc=r=48000:cl=stereo", "-map", "0:v", "-map", "1:a", "-map", "1:a",
            "-t", "8", "-c:v", "libx264", "-g", "90", "-keyint_min", "90", "-sc_threshold", "0", "-bf", "2",
            "-c:a", "aac", "-y", source]);
        var probe = new MediaProbe(MediaToolsFactAttribute.Tool("ffprobe"));
        var decodedBounds = await probe.ProbeAsync(source);
        var packetBounds = await probe.ProbeForStreamCopyAsync(source);
        Assert.Equal(decodedBounds.FirstVideoFrameSeconds, packetBounds.FirstVideoFrameSeconds, 3);
        Assert.Equal(decodedBounds.LastVideoFrameSeconds, packetBounds.LastVideoFrameSeconds, 3);
        var request = new SegmentCutRequest
        {
            RouteId = Guid.NewGuid(), SaveRequestId = Guid.NewGuid(), SourceMediaId = Guid.NewGuid(),
            ObsSessionId = Guid.NewGuid(), SourcePath = source, OutputDirectory = workspace.VideoDirectory,
            MapName = "B frames", ClockFrequency = 1000, SaveRequestedAtTimestamp = 8000,
            Segments = [new SegmentCutItem
            {
                SegmentId = Guid.NewGuid(), Sequence = 1, StartTimestamp = 4600, EndTimestamp = 6300
            }, new SegmentCutItem
            {
                SegmentId = Guid.NewGuid(), Sequence = 2, StartTimestamp = 6300, EndTimestamp = 7600
            }]
        };
        var result = await new SegmentCutter(MediaToolsFactAttribute.Tool("ffmpeg"), MediaToolsFactAttribute.Tool("ffprobe")).CutAsync(request);
        Assert.Equal(2, result.Segments.Count);
        foreach (var item in result.Segments)
        {
            Assert.True(item.Succeeded, item.Error);
            Assert.Equal(2, item.Probe!.AudioTracks.Count);
            Assert.InRange(item.StartSeconds!.Value, 0, 3.1);
            Assert.InRange(item.EndSeconds!.Value, 0, item.Probe.DurationSeconds + 0.1);
            Assert.InRange(item.Probe.DurationSeconds, item.EndSeconds.Value, item.EndSeconds.Value + 0.8);
            await RunFfmpegAsync(["-v", "error", "-xerror", "-i", item.FilePath!, "-map", "0", "-f", "null", "-"]);
        }
        Assert.InRange(result.Segments[0].EndSeconds!.Value - result.Segments[0].StartSeconds!.Value, 1.699, 1.701);
        Assert.InRange(result.Segments[1].EndSeconds!.Value - result.Segments[1].StartSeconds!.Value, 1.299, 1.301);
    }

    [MediaToolsFact]
    public async Task AReplayThatLostTheLogicalStartFailsWithoutPublishingAClip()
    {
        var source = Path.Combine(workspace.VideoDirectory, "short.mkv");
        await RunFfmpegAsync(["-v", "error", "-f", "lavfi", "-i", "testsrc2=size=64x64:rate=10:duration=1",
            "-c:v", "mpeg4", "-y", source]);
        var request = new SegmentCutRequest
        {
            RouteId = Guid.NewGuid(), SaveRequestId = Guid.NewGuid(), SourceMediaId = Guid.NewGuid(),
            ObsSessionId = Guid.NewGuid(), SourcePath = source, OutputDirectory = workspace.VideoDirectory,
            MapName = "missing coverage", ClockFrequency = 1000, SaveRequestedAtTimestamp = 5000,
            Segments = [new SegmentCutItem { SegmentId = Guid.NewGuid(), Sequence = 1, StartTimestamp = 1000, EndTimestamp = 4500 }]
        };
        var item = Assert.Single((await new SegmentCutter(MediaToolsFactAttribute.Tool("ffmpeg"),
            MediaToolsFactAttribute.Tool("ffprobe")).CutAsync(request)).Segments);
        Assert.False(item.Succeeded);
        Assert.Contains("no longer covers", item.Error);
        Assert.True(File.Exists(source));
        Assert.Null(item.FilePath);
        Assert.Empty(Directory.EnumerateFiles(workspace.VideoDirectory, "*.lock", SearchOption.AllDirectories));
    }

    [MediaToolsFact]
    public async Task ProbeFailureReleasesAndDeletesTheReplayLock()
    {
        var source = workspace.CreateVideo("invalid.mkv");
        var request = new SegmentCutRequest
        {
            RouteId = Guid.NewGuid(), SaveRequestId = Guid.NewGuid(), SourceMediaId = Guid.NewGuid(),
            ObsSessionId = Guid.NewGuid(), SourcePath = source, OutputDirectory = workspace.VideoDirectory,
            MapName = "invalid media", ClockFrequency = 1000, SaveRequestedAtTimestamp = 5000,
            Segments = [new SegmentCutItem { SegmentId = Guid.NewGuid(), Sequence = 1, StartTimestamp = 1000, EndTimestamp = 4500 }]
        };
        var cutter = new SegmentCutter(MediaToolsFactAttribute.Tool("ffmpeg"), MediaToolsFactAttribute.Tool("ffprobe"));
        await Assert.ThrowsAsync<InvalidDataException>(() => cutter.CutAsync(request));
        Assert.True(File.Exists(source));
        Assert.Empty(Directory.EnumerateFiles(workspace.VideoDirectory, "*.lock", SearchOption.AllDirectories));
    }

    private static async Task RunFfmpegAsync(IEnumerable<string> arguments)
    {
        var info = new ProcessStartInfo(MediaToolsFactAttribute.Tool("ffmpeg"))
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        using var process = Process.Start(info)!;
        var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20));
        Assert.Equal(0, process.ExitCode);
        Assert.True(string.IsNullOrWhiteSpace(await error));
    }
}
