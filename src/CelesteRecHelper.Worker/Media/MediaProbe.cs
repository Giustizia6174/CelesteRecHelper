using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using CelesteRecHelper.Core.Models;

namespace CelesteRecHelper.Worker.Media;

public sealed class MediaProbe(string executable)
{
    public static string FindExecutable()
    {
        var configured = Environment.GetEnvironmentVariable("CELESTE_REC_FFPROBE");
        if (!string.IsNullOrWhiteSpace(configured)) return Path.GetFullPath(configured);
        foreach (var path in new[] { Path.Combine(AppContext.BaseDirectory, "ffprobe.exe"),
                     Path.Combine(AppContext.BaseDirectory, "..", "tools", "ffprobe.exe"),
                     Path.Combine(Environment.CurrentDirectory, "tools", "ffprobe.exe") })
            if (File.Exists(path)) return Path.GetFullPath(path);
        return OperatingSystem.IsWindows() ? "ffprobe.exe" : "ffprobe";
    }

    public async Task<MediaProbeResult> ProbeAsync(string filePath, CancellationToken ct = default)
    {
        filePath = Path.GetFullPath(filePath);
        if (!File.Exists(filePath)) throw new FileNotFoundException("Video file does not exist.", filePath);
        var metadata = await MetadataAsync(filePath, ct).ConfigureAwait(false);
        var streams = metadata.GetProperty("streams").EnumerateArray().ToArray();
        var video = streams.FirstOrDefault(s => Text(s, "codec_type") == "video");
        if (video.ValueKind == JsonValueKind.Undefined) throw new InvalidDataException("No video stream was found.");
        var duration = Number(metadata.GetProperty("format"), "duration") ?? Number(video, "duration");
        if (duration is not > 0) throw new InvalidDataException("The video duration is missing or invalid.");
        var (first, last) = await FrameBoundsAsync(filePath, ct).ConfigureAwait(false);
        var audio = streams.Where(s => Text(s, "codec_type") == "audio").Select(s => new AudioTrackInfo(
            s.GetProperty("index").GetInt32(), Text(s, "codec_name") ?? "unknown",
            (int)(Number(s, "sample_rate") ?? 0), (int)(Number(s, "channels") ?? 0), Text(s, "channel_layout"),
            s.TryGetProperty("tags", out var tags) ? Text(tags, "language") : null)).ToList();
        return new MediaProbeResult(filePath, new FileInfo(filePath).Length, duration.Value, first, last,
            video.GetProperty("width").GetInt32(), video.GetProperty("height").GetInt32(),
            Text(video, "codec_name") ?? "unknown", Text(video, "avg_frame_rate") ?? "0/0", audio, metadata);
    }

    private async Task<JsonElement> MetadataAsync(string filePath, CancellationToken ct)
    {
        using var process = Start(["-v", "error", "-show_format", "-show_streams", "-of", "json", filePath]);
        var stderr = process.StandardError.ReadToEndAsync(ct);
        try
        {
            var output = await process.StandardOutput.ReadToEndAsync(ct).ConfigureAwait(false);
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
            CheckExit(process, await stderr.ConfigureAwait(false));
            using var document = JsonDocument.Parse(output);
            return document.RootElement.Clone();
        }
        finally { KillIfRunning(process); }
    }

    private async Task<(double First, double Last)> FrameBoundsAsync(string filePath, CancellationToken ct)
    {
        using var process = Start(["-v", "error", "-select_streams", "v:0", "-show_frames", "-show_entries",
            "frame=best_effort_timestamp_time", "-of", "compact=p=0:nk=0", filePath]);
        var stderr = process.StandardError.ReadToEndAsync(ct);
        double? first = null;
        double? last = null;
        try
        {
            while (await process.StandardOutput.ReadLineAsync(ct).ConfigureAwait(false) is { } line)
                foreach (var field in line.Split('|'))
                    if (field.StartsWith("best_effort_timestamp_time=", StringComparison.Ordinal) &&
                        double.TryParse(field[(field.IndexOf('=') + 1)..], NumberStyles.Float, CultureInfo.InvariantCulture, out var time) &&
                        double.IsFinite(time))
                    {
                        first = first is null ? time : Math.Min(first.Value, time);
                        last = last is null ? time : Math.Max(last.Value, time);
                    }
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
            CheckExit(process, await stderr.ConfigureAwait(false));
            if (first is null || last is null) throw new InvalidDataException("No decoded video frame timestamps were found.");
            return (first.Value, last.Value);
        }
        finally { KillIfRunning(process); }
    }

    private Process Start(string[] arguments)
    {
        var info = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        return Process.Start(info) ?? throw new IOException("Could not start ffprobe.");
    }
    private static void CheckExit(Process process, string error)
    {
        if (process.ExitCode != 0)
            throw new InvalidDataException($"ffprobe failed ({process.ExitCode}): {error.Trim()}");
    }
    private static void KillIfRunning(Process process)
    {
        if (!process.HasExited) process.Kill(entireProcessTree: true);
    }
    private static string? Text(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) ? value.ToString() : null;
    private static double? Number(JsonElement element, string property) =>
        double.TryParse(Text(element, property), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && double.IsFinite(value)
            ? value : null;
}
