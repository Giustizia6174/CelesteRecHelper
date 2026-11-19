using System.Diagnostics;
using CelesteRecHelper.Worker.Media;

namespace CelesteRecHelper.Core.Tests;

public sealed class MediaToolsFactAttribute : FactAttribute
{
    public static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "CelesteRecHelper.sln"))) return directory.FullName;
        throw new DirectoryNotFoundException("Could not locate the repository.");
    }
    public static string Tool(string name) => Environment.GetEnvironmentVariable($"CELESTE_REC_{name.ToUpperInvariant()}")
        ?? Path.Combine(RepositoryRoot(), "tools", name + (OperatingSystem.IsWindows() ? ".exe" : ""));
    public MediaToolsFactAttribute()
    {
        if (!File.Exists(Tool("ffmpeg")) || !File.Exists(Tool("ffprobe")))
            Skip = "Media integration tests require ffmpeg and ffprobe in tools/ or CELESTE_REC_FFMPEG / CELESTE_REC_FFPROBE.";
    }
}

public sealed class MediaProbeTests : IDisposable
{
    private readonly TestWorkspace workspace = new();
    public void Dispose() => workspace.Dispose();

    [MediaToolsFact]
    public async Task ReadsActualFrameBoundsAndMultipleAudioTracksFromAUnicodePath()
    {
        var path = Path.Combine(workspace.VideoDirectory, "带 空格.mkv");
        var info = new ProcessStartInfo(MediaToolsFactAttribute.Tool("ffmpeg"))
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true
        };
        foreach (var argument in new[] { "-v", "error", "-f", "lavfi", "-i", "testsrc=size=64x64:rate=10:duration=1",
                     "-f", "lavfi", "-i", "anullsrc=r=48000:cl=stereo", "-f", "lavfi", "-i", "anullsrc=r=44100:cl=mono",
                     "-map", "0:v", "-map", "1:a", "-map", "2:a", "-vf", "setpts=PTS+0.5/TB", "-t", "1.5",
                     "-c:v", "mpeg4", "-bf", "2", "-c:a", "aac", "-y", path }) info.ArgumentList.Add(argument);
        using var process = Process.Start(info)!;
        var error = process.StandardError.ReadToEndAsync();
        var output = process.StandardOutput.ReadToEndAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        try { await process.WaitForExitAsync(deadline.Token); }
        finally { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        await output;
        Assert.True(process.ExitCode == 0, await error);

        var result = await new MediaProbe(MediaToolsFactAttribute.Tool("ffprobe")).ProbeAsync(path, deadline.Token);
        Assert.Equal(64, result.Width);
        Assert.Equal(64, result.Height);
        Assert.InRange(result.FirstVideoFrameSeconds, 0.45, 0.55);
        Assert.InRange(result.LastVideoFrameSeconds, 1.35, 1.45);
        Assert.True(result.DurationSeconds > result.LastVideoFrameSeconds);
        Assert.Equal(2, result.AudioTracks.Count);
        Assert.Equal(new[] { 44100, 48000 }, result.AudioTracks.Select(a => a.SampleRate).Order());
        Assert.True(result.RawMetadata.GetProperty("streams").GetArrayLength() >= 3);
    }

    [MediaToolsFact]
    public async Task RejectsInvalidMediaWithoutReturningFabricatedTimestamps()
    {
        var path = workspace.CreateVideo("invalid.mkv");
        await Assert.ThrowsAsync<InvalidDataException>(() => new MediaProbe(MediaToolsFactAttribute.Tool("ffprobe")).ProbeAsync(path));
    }

    [MediaToolsFact]
    public async Task CancellationStopsTheProbe()
    {
        var path = workspace.CreateVideo("cancelled.mkv");
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new MediaProbe(MediaToolsFactAttribute.Tool("ffprobe")).ProbeAsync(path, cancelled.Token));
    }
}
