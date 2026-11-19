using System.Diagnostics;
using Celeste.Mod.Helpers;
using CelesteRecHelper.Core.Models;
using CelesteRecHelper.Core.Obs;
using CelesteRecHelper.Core.Persistence;

namespace Celeste.Mod.CelesteRecHelper;

internal sealed class ObsMenu(CelesteRecHelperModule module) : IAsyncDisposable
{
    private readonly SemaphoreSlim action = new(1);
    private readonly CancellationTokenSource lifetime = new();
    private ObsWebSocketClient? client;
    private ObsCaptureSession? capture;
    private string? endpoint;
    private string? password;
    private bool disposed;

    public void AddTo(TextMenu menu)
    {
        var status = new TextMenu.SubHeader(Dialog.Clean("rechelper_obs_notchecked"));
        menu.Add(status);
        var detail = new TextMenu.SubHeader("");
        menu.Add(detail);
        var check = new TextMenu.Button(Dialog.Clean("rechelper_obs_check"));
        var save = new TextMenu.Button(Dialog.Clean("rechelper_obs_testsave"));
        menu.Add(check);
        menu.Add(save);
        var handoff = new TextMenu.Button(Dialog.Clean("rechelper_obs_handoff"));
        menu.Add(handoff);
        void Begin(int mode)
        {
            if (disposed) return;
            var settings = CelesteRecHelperModule.Settings;
            var host = settings.ObsHost.Trim();
            var port = settings.ObsPort;
            var secret = settings.ObsPassword;
            var directory = settings.OutputDirectory;
            check.Disabled = save.Disabled = handoff.Disabled = true;
            status.Title = Dialog.Clean("rechelper_obs_busy");
            detail.Title = "";
            _ = Task.Run(async () =>
            {
                await action.WaitAsync().ConfigureAwait(false);
                try
                {
                    if (disposed) return;
                    if (module.Manifests is null) throw new IOException("Manifest storage is unavailable.");
                    var uri = new UriBuilder("ws", host, port).Uri;
                    if (client is null || endpoint != uri.AbsoluteUri || password != secret)
                    {
                        if (client is not null) await client.DisposeAsync().ConfigureAwait(false);
                        client = new ObsWebSocketClient(uri, secret);
                        endpoint = uri.AbsoluteUri;
                        password = secret;
                        capture = new ObsCaptureSession(new ObsApi(client), module.Manifests);
                    }
                    await client.ConnectAsync(lifetime.Token).ConfigureAwait(false);
                    var restoreWarning = await capture!.RestoreDirectoryAsync(endpoint!, lifetime.Token).ConfigureAwait(false);
                    if (restoreWarning is not null) Logger.Log(LogLevel.Warn, "CelesteRecHelper", restoreWarning);
                    var api = new ObsApi(client);
                    var diagnostics = await new ObsInspector(api).InspectAsync(capture.MeasuredBitsPerSecond, lifetime.Token).ConfigureAwait(false);
                    HandoffVerification? handoffReport = null;
                    if (mode == 2)
                    {
                        var test = await ObsHandoffTest.RunAsync(api, module.Manifests, endpoint!, directory, ct: lifetime.Token).ConfigureAwait(false);
                        var folder = Path.Combine(module.Manifests.RootDirectory, "diagnostics", "handoff", test.TestId.ToString("N"));
                        var output = Path.Combine(folder, "verification.json");
                        await RunWorkerAsync(["verify-handoff", Path.Combine(folder, "capture.json"), "--output", output], lifetime.Token,
                            allowInconclusive: true).ConfigureAwait(false);
                        handoffReport = AtomicJsonFile.Read<HandoffVerification>(output);
                        capture.ObserveProbe(test.Replay, handoffReport.ReplayProbe.DurationSeconds);
                        diagnostics = await new ObsInspector(api).InspectAsync(capture.MeasuredBitsPerSecond, lifetime.Token).ConfigureAwait(false);
                        Logger.Log(LogLevel.Info, "CelesteRecHelper", $"Handoff {test.TestId:N}: {handoffReport.Evidence.Status}, overlap {handoffReport.Evidence.OverlapSeconds:0.###}s. Report: {output}");
                    }
                    else if (mode == 1)
                    {
                        SavedReplay replay;
                        try
                        {
                            await capture.StartAsync(endpoint!, directory, lifetime.Token).ConfigureAwait(false);
                            await Task.Delay(TimeSpan.FromSeconds(3), lifetime.Token).ConfigureAwait(false);
                            replay = await capture.SaveAsync(ct: lifetime.Token).ConfigureAwait(false);
                        }
                        finally
                        {
                            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                            await capture.StopAsync(endpoint!, cleanup.Token).ConfigureAwait(false);
                        }
                        var output = Path.Combine(module.Manifests.RootDirectory, "diagnostics", $"probe-{Guid.NewGuid():N}.json");
                        await RunWorkerAsync(["probe", replay.FilePath, "--output", output], lifetime.Token).ConfigureAwait(false);
                        var probe = AtomicJsonFile.Read<MediaProbeResult>(output);
                        capture.ObserveProbe(replay, probe.DurationSeconds);
                        diagnostics = await new ObsInspector(api).InspectAsync(capture.MeasuredBitsPerSecond, lifetime.Token).ConfigureAwait(false);
                        AtomicJsonFile.Write(Path.Combine(module.Manifests.RootDirectory, "diagnostics", "last-replay.json"),
                            new { Replay = replay, Probe = probe, Diagnostics = diagnostics });
                        Logger.Log(LogLevel.Info, "CelesteRecHelper", $"Test replay saved and probed: {replay.FilePath}");
                    }
                    var result = diagnostics;
                    _ = MainThreadHelper.Schedule(() =>
                    {
                        if (disposed) return;
                        status.Title = Dialog.Clean("rechelper_obs_connected") + " / " +
                            Dialog.Clean(result.ReplayAvailable ? "rechelper_obs_available" : "rechelper_obs_unavailable") +
                            $" / {result.Capacity.Seconds:0.#}s / " +
                            Dialog.Clean(result.Capacity.Verified ? "rechelper_obs_verified" : "rechelper_obs_unverified");
                        detail.Title = result.RecordingActive || result.ReplayActive ? Dialog.Clean("rechelper_obs_inuse") :
                            result.Configuration.DurationSeconds < 60 ? Dialog.Clean("rechelper_obs_shortbuffer") :
                            !result.Capacity.Verified ? Dialog.Clean("rechelper_obs_unverified_help") : "";
                        if (handoffReport is { } report)
                            detail.Title = report.Evidence.Status == OverlapStatus.Verified
                                ? Dialog.Clean("rechelper_obs_handoff_verified") + $" {report.Evidence.OverlapSeconds:0.##}s"
                                : Dialog.Clean("rechelper_obs_handoff_inconclusive");
                    });
                    foreach (var warning in diagnostics.Warnings) Logger.Log(LogLevel.Warn, "CelesteRecHelper", warning);
                }
                catch (Exception e)
                {
                    Logger.Log(LogLevel.Warn, "CelesteRecHelper", $"OBS check/test failed: {e.Message}");
                    _ = MainThreadHelper.Schedule(() => { if (!disposed) status.Title = Dialog.Clean("rechelper_obs_failed"); });
                }
                finally
                {
                    action.Release();
                    _ = MainThreadHelper.Schedule(() => { if (!disposed) check.Disabled = save.Disabled = handoff.Disabled = false; });
                }
            });
        }
        check.Pressed(() => Begin(0));
        save.Pressed(() => Begin(1));
        handoff.Pressed(() => Begin(2));
    }

    private async Task RunWorkerAsync(string[] arguments, CancellationToken ct, bool allowInconclusive = false)
    {
        var directory = module.Metadata.PathDirectory;
        var executable = Path.Combine(directory, "worker", "CelesteRecHelper.Worker.exe");
        var info = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        using var process = Process.Start(info) ?? throw new IOException("Could not start the media worker.");
        var stdout = process.StandardOutput.ReadToEndAsync(ct);
        var stderr = process.StandardError.ReadToEndAsync(ct);
        try
        {
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
            await stdout.ConfigureAwait(false);
            var error = await stderr.ConfigureAwait(false);
            if (process.ExitCode != 0 && !(allowInconclusive && process.ExitCode == 3))
                throw new IOException($"Media worker failed: {error.Trim()}");
        }
        finally { if (!process.HasExited) process.Kill(entireProcessTree: true); }
    }

    public async ValueTask DisposeAsync()
    {
        disposed = true;
        lifetime.Cancel();
        await action.WaitAsync().ConfigureAwait(false);
        try
        {
            if (client is not null) await client.DisposeAsync().ConfigureAwait(false);
        }
        finally { action.Release(); lifetime.Dispose(); }
    }
}
