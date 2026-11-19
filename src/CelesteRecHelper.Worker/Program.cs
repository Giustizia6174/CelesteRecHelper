using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using CelesteRecHelper.Core.Obs;
using CelesteRecHelper.Core.Persistence;
using CelesteRecHelper.Worker.Media;

var json = new JsonSerializerOptions { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };
if (args.Length == 0 || args is ["--help"] or ["-h"])
{
    Console.WriteLine("CelesteRecHelper.Worker — stage 4");
    Console.WriteLine("routes <data-directory>");
    Console.WriteLine("probe <video> [--ffprobe <executable>] [--output <json>]");
    Console.WriteLine("cut-segments <request.json> [--ffmpeg <executable>] [--ffprobe <executable>] [--output <json>]");
    Console.WriteLine("obs-check [--endpoint <ws://host:port>] [--password-env <variable>]");
    Console.WriteLine("obs-save --data-dir <Saves/CelesteRecHelper directory> [--endpoint <uri>] [--password-env <variable>] [--wait-seconds <seconds>]");
    Console.WriteLine("         [--output-directory <videos>] [--ffprobe <exe>] [--output <json>]");
    Console.WriteLine("obs-handoff --data-dir <directory> [--endpoint <uri>] [--password-env <variable>]");
    Console.WriteLine("            [--repeat <1..10>] [--overlap-seconds <0.1..10>] [--output-directory <videos>] [--ffmpeg <exe>] [--ffprobe <exe>] [--output <json>]");
    Console.WriteLine("verify-handoff <capture.json> [--ffmpeg <exe>] [--ffprobe <exe>] [--output <json>]");
    Console.WriteLine("OBS password defaults to CELESTE_REC_OBS_PASSWORD. obs-check is read-only; obs-save owns and stops its replay buffer.");
    return 0;
}
using var shutdown = new CancellationTokenSource(TimeSpan.FromMinutes(30));
Console.CancelKeyPress += (_, e) => { e.Cancel = true; shutdown.Cancel(); };
try
{
    var options = new Dictionary<string, string>(StringComparer.Ordinal);
    var positional = new List<string>();
    for (var i = 1; i < args.Length; i++)
    {
        if (!args[i].StartsWith("--", StringComparison.Ordinal)) { positional.Add(args[i]); continue; }
        if (i + 1 >= args.Length || !options.TryAdd(args[i], args[++i]))
            throw new ArgumentException("Each option requires one value and may occur only once.");
    }
    var allowed = args[0] switch
    {
        "routes" => Array.Empty<string>(),
        "probe" => ["--ffprobe", "--output"],
        "cut-segments" => ["--ffmpeg", "--ffprobe", "--output"],
        "obs-check" => ["--endpoint", "--password-env"],
        "verify-handoff" => ["--ffmpeg", "--ffprobe", "--output"],
        "obs-handoff" => ["--endpoint", "--password-env", "--data-dir", "--output-directory", "--overlap-seconds", "--repeat", "--ffmpeg", "--ffprobe", "--output"],
        "obs-save" => ["--endpoint", "--password-env", "--wait-seconds", "--data-dir", "--output-directory", "--ffprobe", "--output"],
        _ => throw new ArgumentException("Unknown command. Use --help for usage.")
    };
    if (options.Keys.Any(key => !allowed.Contains(key))) throw new ArgumentException("Unknown option. Use --help for usage.");
    string Option(string key, string fallback) => options.GetValueOrDefault(key, fallback);
    object result;
    var resultCode = 0;
    if (args[0] == "routes" && positional.Count == 1)
    {
        var store = new ManifestStore(positional[0]);
        result = store.EnumerateRouteIds().Select(store.Load).Select(r => new
        {
            r.RouteId, r.MapSid, r.MapName, r.SaveSlot, Status = r.Status.ToString(),
            SegmentCount = r.Segments.Count, MediaCount = r.Media.Count
        }).ToList();
    }
    else if (args[0] == "probe" && positional.Count == 1)
    {
        if (options.TryGetValue("--output", out var target) && string.Equals(Path.GetFullPath(target), Path.GetFullPath(positional[0]),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new ArgumentException("Output JSON must not overwrite the input video.");
        result = await new MediaProbe(Option("--ffprobe", MediaProbe.FindExecutable())).ProbeAsync(positional[0], shutdown.Token);
    }
    else if (args[0] == "cut-segments" && positional.Count == 1)
    {
        if (options.TryGetValue("--output", out var target) && string.Equals(Path.GetFullPath(target), Path.GetFullPath(positional[0]),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new ArgumentException("Output JSON must not overwrite the input request.");
        var request = AtomicJsonFile.Read<CelesteRecHelper.Core.Models.SegmentCutRequest>(positional[0]);
        result = await new SegmentCutter(Option("--ffmpeg", SegmentCutter.FindExecutable()),
            Option("--ffprobe", MediaProbe.FindExecutable())).CutAsync(request, shutdown.Token);
    }
    else if (args[0] == "verify-handoff" && positional.Count == 1)
    {
        var capture = AtomicJsonFile.Read<CelesteRecHelper.Core.Models.HandoffCapture>(positional[0]);
        var report = await new HandoffVerifier(Option("--ffmpeg", HandoffVerifier.FindFfmpeg()),
            Option("--ffprobe", MediaProbe.FindExecutable())).VerifyAsync(capture, Path.GetDirectoryName(Path.GetFullPath(positional[0]))!, shutdown.Token);
        result = report;
        if (report.Evidence.Status != CelesteRecHelper.Core.Models.OverlapStatus.Verified) resultCode = 3;
    }
    else if (args[0] is "obs-check" or "obs-save" or "obs-handoff" && positional.Count == 0)
    {
        if (args[0] != "obs-check" && !options.ContainsKey("--data-dir"))
            throw new ArgumentException(args[0] + " requires --data-dir pointing to the fixed Saves/CelesteRecHelper directory.");
        var endpoint = Option("--endpoint", "ws://127.0.0.1:4455");
        var password = Environment.GetEnvironmentVariable(Option("--password-env", "CELESTE_REC_OBS_PASSWORD")) ?? "";
        await using var client = new ObsWebSocketClient(new Uri(endpoint), password);
        await client.ConnectAsync(shutdown.Token);
        var api = new ObsApi(client);
        if (args[0] == "obs-check") result = await new ObsInspector(api).InspectAsync(ct: shutdown.Token);
        else if (args[0] == "obs-handoff")
        {
            var repeat = int.Parse(Option("--repeat", "3"), CultureInfo.InvariantCulture);
            var overlap = double.Parse(Option("--overlap-seconds", "2"), CultureInfo.InvariantCulture);
            if (repeat is < 1 or > 10) throw new ArgumentException("Repeat count must be between 1 and 10.");
            var store = new ManifestStore(options["--data-dir"]);
            var verifier = new HandoffVerifier(Option("--ffmpeg", HandoffVerifier.FindFfmpeg()), Option("--ffprobe", MediaProbe.FindExecutable()));
            var reports = new List<CelesteRecHelper.Core.Models.HandoffVerification>();
            for (var attempt = 0; attempt < repeat; attempt++)
            {
                var capture = await ObsHandoffTest.RunAsync(api, store, endpoint, options.GetValueOrDefault("--output-directory"), overlap, shutdown.Token);
                var folder = Path.Combine(store.RootDirectory, "diagnostics", "handoff", capture.TestId.ToString("N"));
                reports.Add(await verifier.VerifyAsync(capture, folder, shutdown.Token));
            }
            result = reports;
            if (reports.Any(r => r.Evidence.Status != CelesteRecHelper.Core.Models.OverlapStatus.Verified)) resultCode = 3;
        }
        else
        {
            var wait = double.Parse(Option("--wait-seconds", "3"), CultureInfo.InvariantCulture);
            if (!double.IsFinite(wait) || wait is < 1 or > 300) throw new ArgumentException("Wait time must be between 1 and 300 seconds.");
            var store = new ManifestStore(options["--data-dir"]);
            var capture = new ObsCaptureSession(api, store);
            CelesteRecHelper.Core.Obs.SavedReplay replay;
            await capture.StartAsync(endpoint, options.GetValueOrDefault("--output-directory"), shutdown.Token);
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(wait), shutdown.Token);
                replay = await capture.SaveAsync(ct: shutdown.Token);
            }
            finally
            {
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                await capture.StopAsync(endpoint, cleanup.Token);
            }
            var probe = await new MediaProbe(Option("--ffprobe", MediaProbe.FindExecutable())).ProbeAsync(replay.FilePath, shutdown.Token);
            capture.ObserveProbe(replay, probe.DurationSeconds);
            result = new { Replay = replay, Probe = probe, Diagnostics = await new ObsInspector(api).InspectAsync(capture.MeasuredBitsPerSecond, shutdown.Token) };
        }
    }
    else throw new ArgumentException("Invalid arguments. Use --help for usage.");
    if (options.TryGetValue("--output", out var output)) AtomicJsonFile.Write(output, result);
    Console.WriteLine(JsonSerializer.Serialize(result, json));
    return resultCode;
}
catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException or JsonException
    or ArgumentException or InvalidOperationException or NotSupportedException or TimeoutException
    or OperationCanceledException or ObsRequestException or ObsAuthenticationException or System.ComponentModel.Win32Exception
    or System.Net.WebSockets.WebSocketException)
{
    Console.Error.WriteLine(e is OperationCanceledException ? "The operation was cancelled or timed out." : e.Message);
    return 1;
}
