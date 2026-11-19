using System.Globalization;
using System.Text.Json;

namespace CelesteRecHelper.Core.Obs;

public sealed record ReplayConfiguration(string OutputMode, bool Enabled, double DurationSeconds,
    double MemoryLimitMb, string Encoder, bool OutputSupported, bool ActualLimitsRead, bool EncoderSharingExpected);
public sealed record ReplayCapacity(double Seconds, double HandoffSeconds, bool Verified, string Explanation);
public sealed record ObsDiagnostics(string ObsVersion, string WebSocketVersion, string ProfileName,
    string RecordDirectory, bool RecordingActive, bool ReplayActive, bool ReplayAvailable,
    ReplayConfiguration Configuration, ReplayCapacity Capacity, IReadOnlyList<string> Warnings);

public static class ReplayCapacityEstimator
{
    public const double MinimumSafetyMarginSeconds = 15;
    public static ReplayCapacity Estimate(ReplayConfiguration configuration, double? measuredBitsPerSecond = null)
    {
        if (configuration.DurationSeconds <= 0 || !double.IsFinite(configuration.DurationSeconds) ||
            configuration.MemoryLimitMb < 0 || !double.IsFinite(configuration.MemoryLimitMb))
            throw new ArgumentException("Invalid replay buffer limits.");
        var seconds = configuration.DurationSeconds;
        var measured = measuredBitsPerSecond is > 0 && double.IsFinite(measuredBitsPerSecond.Value);
        if (configuration.MemoryLimitMb > 0)
        {
            // Use a conservative initial bound for quality encoding, then revise from actual saved media.
            var bitsPerSecond = measured ? measuredBitsPerSecond!.Value * 1.5 : 50_000_000;
            seconds = Math.Min(seconds, configuration.MemoryLimitMb * 1024 * 1024 * 8 / bitsPerSecond);
        }
        var verified = configuration.OutputSupported && configuration.ActualLimitsRead &&
            (configuration.MemoryLimitMb == 0 || measured);
        var threshold = Math.Max(0, seconds - Math.Max(seconds * 0.3, MinimumSafetyMarginSeconds));
        if (!verified) threshold = Math.Min(threshold, 30);
        return new ReplayCapacity(seconds, threshold, verified,
            !configuration.OutputSupported ? "Unsupported recording output; estimate is unverified." :
            configuration.MemoryLimitMb > 0 && !measured ? "Quality encoding: awaiting measured bitrate; conservative early handoff." :
            !configuration.ActualLimitsRead ? "Profile limits only; actual replay output settings have not been checked." :
            configuration.MemoryLimitMb > 0 ? "Actual limits and measured bitrate, with a 50% bitrate margin." :
            "Actual replay output is limited by duration only.");
    }
}

public sealed class ObsInspector(ObsApi api)
{
    public async Task<ObsDiagnostics> InspectAsync(double? measuredBitsPerSecond = null, CancellationToken ct = default)
    {
        var version = await api.GetVersionAsync(ct).ConfigureAwait(false);
        var profile = await api.GetProfileListAsync(ct).ConfigureAwait(false);
        var record = await api.GetRecordStatusAsync(ct).ConfigureAwait(false);
        var directory = await api.GetRecordDirectoryAsync(ct).ConfigureAwait(false);
        var mode = await api.GetProfileParameterAsync("Output", "Mode", ct).ConfigureAwait(false) ?? "Simple";
        var category = mode == "Advanced" ? "AdvOut" : "SimpleOutput";
        var names = mode == "Advanced" ? new[] { "RecRB", "RecRBTime", "RecRBSize", "RecEncoder", "RecType" }
            : ["RecRB", "RecRBTime", "RecRBSize", "RecEncoder", "RecQuality"];
        var values = await Task.WhenAll(names.Select(name => api.GetProfileParameterAsync(category, name, ct))).ConfigureAwait(false);
        var warnings = new List<string>();
        var enabled = values[0] is "true" or "1";
        var duration = Number(values[1], 20);
        var size = Number(values[2], 512);
        var supported = mode == "Simple" ? values[4] != "Lossless" : mode == "Advanced" && values[4] == "Standard";
        if (mode == "Simple" && values[4] == "Stream") size = 0;
        var available = false;
        var active = false;
        try
        {
            var replay = await api.GetReplayBufferStatusAsync(ct).ConfigureAwait(false);
            active = replay.GetProperty("outputActive").GetBoolean();
            available = true;
        }
        catch (ObsRequestException e) when (e.Code is 504 or 604) { warnings.Add(e.Message); }
        var actual = false;
        if (available)
        {
            var outputs = await api.GetOutputListAsync(ct).ConfigureAwait(false);
            foreach (var output in outputs.GetProperty("outputs").EnumerateArray())
            {
                if (output.GetProperty("outputKind").GetString() != "replay_buffer") continue;
                var settings = (await api.GetOutputSettingsAsync(output.GetProperty("outputName").GetString()!, ct)
                    .ConfigureAwait(false)).GetProperty("outputSettings");
                if (settings.TryGetProperty("max_time_sec", out var time) && time.TryGetDouble(out var seconds) && seconds > 0 &&
                    settings.TryGetProperty("max_size_mb", out var memory) && memory.TryGetDouble(out var mb) && mb >= 0)
                {
                    duration = seconds;
                    size = mb;
                    actual = true;
                }
                break;
            }
        }
        if (duration < 60) warnings.Add("Replay buffer is short. Set a longer duration in OBS (for example 5 minutes).");
        if (!enabled) warnings.Add("Replay buffer is disabled in the current OBS profile.");
        var configuration = new ReplayConfiguration(mode, enabled, Math.Max(1, duration), size,
            values[3] ?? "unknown", supported, actual, supported);
        return new ObsDiagnostics(version.GetProperty("obsVersion").GetString()!,
            version.GetProperty("obsWebSocketVersion").GetString()!, profile.GetProperty("currentProfileName").GetString()!,
            directory.GetProperty("recordDirectory").GetString()!, record.GetProperty("outputActive").GetBoolean(),
            active, available, configuration, ReplayCapacityEstimator.Estimate(configuration, measuredBitsPerSecond), warnings);
    }

    private static double Number(string? value, double fallback) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && double.IsFinite(number)
            ? number : fallback;
}
