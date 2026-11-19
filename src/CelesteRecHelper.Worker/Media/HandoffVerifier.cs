using System.Diagnostics;
using System.Globalization;
using CelesteRecHelper.Core.Media;
using CelesteRecHelper.Core.Models;
using CelesteRecHelper.Core.Obs;
using CelesteRecHelper.Core.Persistence;

namespace CelesteRecHelper.Worker.Media;

public sealed class HandoffVerifier(string ffmpeg, string ffprobe)
{
    public static string FindFfmpeg()
    {
        var configured = Environment.GetEnvironmentVariable("CELESTE_REC_FFMPEG");
        if (!string.IsNullOrWhiteSpace(configured)) return Path.GetFullPath(configured);
        foreach (var path in new[] { Path.Combine(AppContext.BaseDirectory, "ffmpeg.exe"),
                     Path.Combine(AppContext.BaseDirectory, "..", "tools", "ffmpeg.exe"),
                     Path.Combine(Environment.CurrentDirectory, "tools", "ffmpeg.exe") })
            if (File.Exists(path)) return Path.GetFullPath(path);
        return OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg";
    }

    public async Task<HandoffVerification> VerifyAsync(HandoffCapture capture, string outputDirectory, CancellationToken ct = default)
    {
        if (capture.ClockFrequency <= 0) throw new InvalidDataException("The capture clock frequency is invalid.");
        var probe = new MediaProbe(ffprobe);
        var replay = await probe.ProbeAsync(capture.Replay.FilePath, ct).ConfigureAwait(false);
        var recording = await probe.ProbeAsync(capture.Recording.FilePath, ct).ConfigureAwait(false);
        // Limit frame hashing to the expected handoff area; report when it extends beyond the bounded window.
        var window = Math.Min(30, Math.Max(8, capture.RequestedOverlapSeconds + 5));
        var hashesA = await ReadHashesAsync(replay.FilePath,
            Math.Max(replay.FirstVideoFrameSeconds, replay.LastVideoFrameSeconds - window), window + 1, ct).ConfigureAwait(false);
        var hashesB = await ReadHashesAsync(recording.FilePath, recording.FirstVideoFrameSeconds, window + 1, ct).ConfigureAwait(false);
        AtomicJsonFile.Write(Path.Combine(outputDirectory, "replay-frames.json"), hashesA);
        AtomicJsonFile.Write(Path.Combine(outputDirectory, "recording-frames.json"), hashesB);
        var evidence = OverlapMatcher.Match(hashesA, hashesB);
        var startup = (capture.Recording.Start.StartedAtTimestamp - capture.Recording.Start.RequestedAtTimestamp) / (double)capture.ClockFrequency;
        var save = (capture.Replay.EventAtTimestamp - capture.Replay.RequestedAtTimestamp) / (double)capture.ClockFrequency;
        if (startup < 0 || save < 0) throw new InvalidDataException("The capture event timestamps are out of order.");
        // Includes the intentional overlap, twice the measured RPC/event latency, and a fixed floor.
        var safety = Math.Max(ReplayCapacityEstimator.MinimumSafetyMarginSeconds, capture.RequestedOverlapSeconds + 2 * (startup + save) + 2);
        var result = new HandoffVerification(capture, replay, recording, evidence, startup, save, safety);
        AtomicJsonFile.Write(Path.Combine(outputDirectory, "verification.json"), result);
        return result;
    }

    public async Task<IReadOnlyList<VideoFrameHash>> ReadHashesAsync(string path, double startSeconds,
        double durationSeconds, CancellationToken ct = default)
    {
        if (!double.IsFinite(startSeconds) || !double.IsFinite(durationSeconds) || startSeconds < 0 || durationSeconds <= 0)
            throw new ArgumentOutOfRangeException(nameof(startSeconds));
        var info = new ProcessStartInfo(ffmpeg)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        var start = startSeconds.ToString("R", CultureInfo.InvariantCulture);
        var end = (startSeconds + durationSeconds).ToString("R", CultureInfo.InvariantCulture);
        foreach (var argument in new[] { "-v", "error", "-i", Path.GetFullPath(path), "-map", "0:v:0", "-an",
                     "-vf", $"trim=start={start}:end={end},scale=160:90:flags=area,format=gray", "-fps_mode", "passthrough",
                     "-f", "framemd5", "-" }) info.ArgumentList.Add(argument);
        using var process = Process.Start(info) ?? throw new IOException("Could not start ffmpeg frame comparison.");
        var stderr = process.StandardError.ReadToEndAsync(ct);
        var frames = new List<VideoFrameHash>();
        var timeBase = 0.0;
        try
        {
            while (await process.StandardOutput.ReadLineAsync(ct).ConfigureAwait(false) is { } line)
            {
                if (line.StartsWith("#tb 0:", StringComparison.Ordinal))
                {
                    var parts = line[6..].Trim().Split('/');
                    if (parts.Length == 2) timeBase = double.Parse(parts[0], CultureInfo.InvariantCulture) / double.Parse(parts[1], CultureInfo.InvariantCulture);
                }
                else if (!line.StartsWith('#') && !string.IsNullOrWhiteSpace(line))
                {
                    var fields = line.Split(',', StringSplitOptions.TrimEntries);
                    if (fields.Length != 6 || timeBase <= 0) throw new InvalidDataException("Invalid ffmpeg frame hash output.");
                    frames.Add(new VideoFrameHash(long.Parse(fields[2], CultureInfo.InvariantCulture) * timeBase, fields[5]));
                    if (frames.Count > 10_000) throw new InvalidDataException("The frame comparison window exceeded its limit.");
                }
            }
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
            var error = await stderr.ConfigureAwait(false);
            if (process.ExitCode != 0) throw new InvalidDataException($"ffmpeg frame comparison failed: {error.Trim()}");
            return frames;
        }
        finally { if (!process.HasExited) process.Kill(entireProcessTree: true); }
    }
}
