using System.Diagnostics;
using System.Globalization;
using CelesteRecHelper.Core.Models;

namespace CelesteRecHelper.Worker.Media;

/// <summary>Creates reviewable, stream-copied clips from one OBS replay file.</summary>
public sealed class SegmentCutter(string ffmpeg, string ffprobe)
{
    private const double EndPaddingSeconds = 0.5;

    public static string FindExecutable()
    {
        var configured = Environment.GetEnvironmentVariable("CELESTE_REC_FFMPEG");
        if (!string.IsNullOrWhiteSpace(configured)) return Path.GetFullPath(configured);
        foreach (var path in new[] { Path.Combine(AppContext.BaseDirectory, "ffmpeg.exe"),
                     Path.Combine(AppContext.BaseDirectory, "..", "tools", "ffmpeg.exe"),
                     Path.Combine(Environment.CurrentDirectory, "tools", "ffmpeg.exe") })
            if (File.Exists(path)) return Path.GetFullPath(path);
        return OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg";
    }

    public async Task<SegmentCutResult> CutAsync(SegmentCutRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateRequest(request);
        var sourcePath = Path.GetFullPath(request.SourcePath);
        if (!File.Exists(sourcePath)) throw new FileNotFoundException("The replay source does not exist.", sourcePath);
        var outputDirectory = Path.Combine(request.OutputDirectory, "CelesteRecHelper",
            $"{SafeName(request.MapName)}_{request.RouteId.ToString("N")[..8]}");
        Directory.CreateDirectory(outputDirectory);
        // A worker may outlive a crashed game. Serialize retries across processes as well as within the mod.
        var lockPath = Path.Combine(outputDirectory, $".{request.SaveRequestId:N}.lock");
        var ownership = await LockSourceAsync(lockPath, ct).ConfigureAwait(false);
        try
        {
            var source = await new MediaProbe(ffprobe).ProbeForStreamCopyAsync(sourcePath, ct).ConfigureAwait(false);
            var keyframes = await KeyframesAsync(sourcePath, source, ct).ConfigureAwait(false);
            var extension = Path.GetExtension(sourcePath);
            if (string.IsNullOrWhiteSpace(extension)) extension = ".mkv";

            var result = new SegmentCutResult
            {
                RouteId = request.RouteId, SaveRequestId = request.SaveRequestId,
                SourceMediaId = request.SourceMediaId, ObsSessionId = request.ObsSessionId,
                SourcePath = sourcePath
            };
            foreach (var item in request.Segments.OrderBy(item => item.Sequence))
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    var bounds = SourceBounds(request, item, source.DurationSeconds);
                    var keyframe = keyframes.LastOrDefault(frame => frame.Time <= bounds.Start + 0.000001);
                    if (keyframe is null)
                        throw new InvalidDataException("No preceding video keyframe covers the logical start.");
                    var outputPath = Path.Combine(outputDirectory,
                        FormattableString.Invariant($"{item.Sequence:000}_{(bounds.End - bounds.Start):0.0}s{extension}"));
                    var probe = await EnsureClipAsync(sourcePath, outputPath, bounds, keyframe, source, ct).ConfigureAwait(false);
                    var start = probe.FirstVideoFrameSeconds + bounds.Start - keyframe.Time;
                    var end = probe.FirstVideoFrameSeconds + bounds.End - keyframe.Time;
                    if (start < -0.001 || end > probe.DurationSeconds + 0.1)
                        throw new InvalidDataException("The clip does not cover the exact logical interval.");
                    result.Segments.Add(new SegmentCutItemResult
                    {
                        SegmentId = item.SegmentId, Sequence = item.Sequence, Succeeded = true,
                        FilePath = outputPath, Probe = probe, StartSeconds = Math.Max(0, start), EndSeconds = end
                    });
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception e)
                {
                    result.Segments.Add(new SegmentCutItemResult
                    {
                        SegmentId = item.SegmentId, Sequence = item.Sequence, Succeeded = false,
                        Error = e.Message
                    });
                }
            }
            return result;
        }
        finally
        {
            ownership.Dispose();
            // A new owner may already hold this path; FileShare.None makes deletion fail safely on Windows.
            TryDelete(lockPath);
        }
    }

    private static async Task<FileStream> LockSourceAsync(string path, CancellationToken ct)
    {
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException e) when ((e.HResult & 0xffff) is 32 or 33 or 11)
            { await Task.Delay(100, ct).ConfigureAwait(false); }
        }
    }

    private async Task<MediaProbeResult> EnsureClipAsync(string sourcePath, string outputPath,
        (double Start, double End) bounds, Keyframe keyframe, MediaProbeResult source, CancellationToken ct)
    {
        // The source lock makes this deterministic temporary path safe to replace after a crash.
        var temporaryPath = Path.Combine(Path.GetDirectoryName(outputPath)!,
            $".{Path.GetFileNameWithoutExtension(outputPath)}.part{Path.GetExtension(outputPath)}");
        TryDelete(temporaryPath);
        if (File.Exists(outputPath))
        {
            try
            {
                var existing = await new MediaProbe(ffprobe).ProbeForStreamCopyAsync(outputPath, ct).ConfigureAwait(false);
                ValidateClip(existing, source, bounds.End - keyframe.Time);
                await ValidateFirstKeyframeAsync(outputPath, keyframe, ct).ConfigureAwait(false);
                return existing;
            }
            catch (Exception) when (!ct.IsCancellationRequested)
            {
                TryDelete(outputPath);
            }
        }

        // Output seeking at zero removes packets before the selected decode boundary.
        // Seeking by PTS alone can retain an entire previous GOP when B frames are present.
        var seekStart = Math.Max(0, keyframe.DecodeTime - 0.001);
        var requestedEnd = Math.Min(source.DurationSeconds, bounds.End + EndPaddingSeconds);
        var duration = requestedEnd - seekStart;
        if (duration <= 0) throw new InvalidDataException("The requested clip is outside the replay file.");
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        // Publish only validated outputs; an interrupted ffmpeg leaves no reusable final file.
        try
        {
            var arguments = new List<string> { "-hide_banner", "-loglevel", "error", "-ss", Number(seekStart), "-i", sourcePath };
            if (seekStart > 0) arguments.AddRange(["-ss", "0"]);
            arguments.AddRange(["-t", Number(duration), "-map", "0", "-c", "copy", "-avoid_negative_ts", "make_zero", "-y", temporaryPath]);
            await RunFfmpegAsync(arguments, ct).ConfigureAwait(false);
            var probe = await new MediaProbe(ffprobe).ProbeForStreamCopyAsync(temporaryPath, ct).ConfigureAwait(false);
            ValidateClip(probe, source, bounds.End - keyframe.Time);
            await ValidateFirstKeyframeAsync(temporaryPath, keyframe, ct).ConfigureAwait(false);
            File.Move(temporaryPath, outputPath, overwrite: true);
            return probe with { FilePath = outputPath };
        }
        finally { TryDelete(temporaryPath); }
    }

    private sealed record Keyframe(double Time, double DecodeTime, string Hash);

    private async Task ValidateFirstKeyframeAsync(string path, Keyframe expected, CancellationToken ct)
    {
        var first = (await KeyframesAsync(path, null, ct, firstOnly: true).ConfigureAwait(false)).FirstOrDefault();
        if (first is null || first.Hash != expected.Hash)
            throw new InvalidDataException("The clip does not begin at the selected source keyframe.");
    }

    private async Task<List<Keyframe>> KeyframesAsync(string sourcePath, MediaProbeResult? source,
        CancellationToken ct, bool firstOnly = false)
    {
        var origin = source is not null && source.RawMetadata.GetProperty("format").TryGetProperty("start_time", out var value) &&
            double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0;
        var arguments = new List<string> { "-v", "error", "-select_streams", "v:0", "-show_packets",
            "-show_entries", "packet=pts_time,dts_time,flags,data_hash", "-show_data_hash", "sha256", "-of", "compact=p=0:nk=0" };
        if (firstOnly) arguments.AddRange(["-read_intervals", "%+#1"]);
        arguments.Add(sourcePath);
        using var process = Start(ffprobe, arguments);
        var error = process.StandardError.ReadToEndAsync(ct);
        var keyframes = new List<Keyframe>();
        try
        {
            while (await process.StandardOutput.ReadLineAsync(ct).ConfigureAwait(false) is { } line)
            {
                var fields = line.Split('|');
                if (!fields.Any(field => field.StartsWith("flags=", StringComparison.Ordinal) && field[6..].Contains('K')))
                    continue;
                var pts = fields.FirstOrDefault(field => field.StartsWith("pts_time=", StringComparison.Ordinal));
                var dts = fields.FirstOrDefault(field => field.StartsWith("dts_time=", StringComparison.Ordinal));
                var hash = fields.FirstOrDefault(field => field.StartsWith("data_hash=", StringComparison.Ordinal));
                if (pts is not null && hash is not null &&
                    double.TryParse(pts[9..], NumberStyles.Float, CultureInfo.InvariantCulture, out var time) && double.IsFinite(time))
                {
                    var decode = dts is not null && double.TryParse(dts[9..], NumberStyles.Float,
                        CultureInfo.InvariantCulture, out var parsedDecode) && double.IsFinite(parsedDecode) ? parsedDecode : time;
                    keyframes.Add(new Keyframe(time - origin, decode - origin, hash[10..]));
                }
            }
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
            var message = await error.ConfigureAwait(false);
            if (process.ExitCode != 0) throw new InvalidDataException($"ffprobe keyframe scan failed: {message.Trim()}");
        }
        finally { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        return keyframes.OrderBy(frame => frame.Time).ToList();
    }

    private static void ValidateClip(MediaProbeResult clip, MediaProbeResult source, double requiredDuration)
    {
        if (clip.FileSizeBytes <= 0 || clip.DurationSeconds <= 0 || clip.LastVideoFrameSeconds + FrameDuration(source) + 0.1 < clip.FirstVideoFrameSeconds + requiredDuration)
            throw new InvalidDataException("The cut file is shorter than the logical segment or has no duration.");
        var sourceStreams = source.RawMetadata.GetProperty("streams").GetArrayLength();
        var clipStreams = clip.RawMetadata.GetProperty("streams").GetArrayLength();
        if (sourceStreams != clipStreams || source.AudioTracks.Count != clip.AudioTracks.Count ||
            source.VideoCodec != clip.VideoCodec || source.Width != clip.Width || source.Height != clip.Height ||
            source.AudioTracks.Where((track, index) => track.Codec != clip.AudioTracks[index].Codec ||
                track.Channels != clip.AudioTracks[index].Channels || track.SampleRate != clip.AudioTracks[index].SampleRate).Any())
            throw new InvalidDataException("The cut file does not preserve the source audio/video streams.");
    }

    private static double FrameDuration(MediaProbeResult probe)
    {
        var parts = probe.FrameRate.Split('/');
        return parts.Length == 2 && double.TryParse(parts[0], CultureInfo.InvariantCulture, out var numerator) &&
            double.TryParse(parts[1], CultureInfo.InvariantCulture, out var denominator) && numerator > 0 && denominator > 0
            ? denominator / numerator : 0.1;
    }

    private async Task RunFfmpegAsync(IEnumerable<string> arguments, CancellationToken ct)
    {
        using var process = Start(ffmpeg, arguments);
        var errorTask = process.StandardError.ReadToEndAsync(ct);
        try
        {
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
            var error = await errorTask.ConfigureAwait(false);
            if (process.ExitCode != 0)
                throw new InvalidDataException($"ffmpeg failed with exit code {process.ExitCode}: {error.Trim()}");
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
    }

    private static Process Start(string executable, IEnumerable<string> arguments)
    {
        var info = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        return Process.Start(info) ?? throw new InvalidOperationException("Could not start ffmpeg.");
    }

    private static (double Start, double End) SourceBounds(SegmentCutRequest request, SegmentCutItem item, double duration)
    {
        var start = duration - (request.SaveRequestedAtTimestamp - item.StartTimestamp) / (double)request.ClockFrequency;
        var end = duration - (request.SaveRequestedAtTimestamp - item.EndTimestamp) / (double)request.ClockFrequency;
        if (start < -0.25 || end > duration + 0.25)
            throw new InvalidDataException("The replay buffer no longer covers the complete logical segment.");
        start = Math.Clamp(start, 0, duration);
        end = Math.Clamp(end, 0, duration);
        if (!double.IsFinite(start) || !double.IsFinite(end) || end <= start)
            throw new InvalidDataException("The logical segment does not map to a positive replay interval.");
        return (start, end);
    }

    private static void ValidateRequest(SegmentCutRequest request)
    {
        if (request.RouteId == Guid.Empty || request.SaveRequestId == Guid.Empty || request.SourceMediaId == Guid.Empty ||
            request.ObsSessionId == Guid.Empty || request.ClockFrequency <= 0 || request.SaveRequestedAtTimestamp < 0 ||
            string.IsNullOrWhiteSpace(request.SourcePath) || !Path.IsPathFullyQualified(request.SourcePath) ||
            string.IsNullOrWhiteSpace(request.OutputDirectory) || !Path.IsPathFullyQualified(request.OutputDirectory) ||
            request.Segments.Count == 0 || request.Segments.Select(item => item.SegmentId).Distinct().Count() != request.Segments.Count ||
            request.Segments.Select(item => item.Sequence).Distinct().Count() != request.Segments.Count || request.Segments.Any(item => item.SegmentId == Guid.Empty ||
                item.Sequence <= 0 || item.StartTimestamp < 0 || item.EndTimestamp > request.SaveRequestedAtTimestamp || item.EndTimestamp <= item.StartTimestamp))
            throw new ArgumentException("The segment cut request is incomplete or invalid.", nameof(request));
    }

    private static string SafeName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var safe = new string((name ?? "map").Select(c => invalid.Contains(c) || char.IsControl(c) ? '_' : c).ToArray()).Trim();
        return string.IsNullOrWhiteSpace(safe) ? "map" : safe;
    }

    private static string Number(double value) => value.ToString("0.######", CultureInfo.InvariantCulture);

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
