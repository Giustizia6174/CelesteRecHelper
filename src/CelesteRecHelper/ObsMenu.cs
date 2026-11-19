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
        void Begin(bool testSave)
        {
            if (disposed) return;
            var settings = CelesteRecHelperModule.Settings;
            var host = settings.ObsHost.Trim();
            var port = settings.ObsPort;
            var secret = settings.ObsPassword;
            var directory = settings.OutputDirectory;
            check.Disabled = save.Disabled = true;
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
                    if (testSave)
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
                        var probe = await ProbeWithWorkerAsync(replay.FilePath, output, lifetime.Token).ConfigureAwait(false);
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
                    _ = MainThreadHelper.Schedule(() => { if (!disposed) check.Disabled = save.Disabled = false; });
                }
            });
        }
        check.Pressed(() => Begin(false));
        save.Pressed(() => Begin(true));
    }

    private async Task<MediaProbeResult> ProbeWithWorkerAsync(string video, string output, CancellationToken ct)
    {
        var directory = module.Metadata.PathDirectory;
        var executable = Path.Combine(directory, "worker", "CelesteRecHelper.Worker.exe");
        var info = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (var argument in new[] { "probe", video, "--output", output }) info.ArgumentList.Add(argument);
        using var process = Process.Start(info) ?? throw new IOException("Could not start the media worker.");
        var stdout = process.StandardOutput.ReadToEndAsync(ct);
        var stderr = process.StandardError.ReadToEndAsync(ct);
        try
        {
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
            await stdout.ConfigureAwait(false);
            var error = await stderr.ConfigureAwait(false);
            if (process.ExitCode != 0) throw new IOException($"Media probe failed: {error.Trim()}");
            return AtomicJsonFile.Read<MediaProbeResult>(output);
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
