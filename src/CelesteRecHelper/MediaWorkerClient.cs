using System.Diagnostics;
using CelesteRecHelper.Core.Models;
using CelesteRecHelper.Core.Persistence;

namespace Celeste.Mod.CelesteRecHelper;

internal sealed class MediaWorkerClient
{
    private readonly string executable;
    private readonly SegmentCutJobRunner jobs;

    public MediaWorkerClient(ManifestStore store, string executable)
    {
        this.executable = executable;
        jobs = new SegmentCutJobRunner(store, (request, result, ct) =>
            RunAsync(["cut-segments", request, "--output", result], ct));
    }

    public Task<SegmentCutResult> CutSegmentsAsync(SegmentCutRequest request, CancellationToken ct = default) =>
        jobs.CutAsync(request, ct);

    private async Task RunAsync(IEnumerable<string> arguments, CancellationToken ct)
    {
        var info = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        using var process = Process.Start(info) ?? throw new IOException("Could not start the media worker.");
        var output = process.StandardOutput.ReadToEndAsync(ct);
        var error = process.StandardError.ReadToEndAsync(ct);
        try
        {
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
            await output.ConfigureAwait(false);
            var message = await error.ConfigureAwait(false);
            if (process.ExitCode != 0)
                throw new IOException($"Media worker failed: {message.Trim()}");
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
    }
}
