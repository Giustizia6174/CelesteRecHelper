using System.Collections.Concurrent;
using System.Net.WebSockets;
using CelesteRecHelper.Core.Models;
using CelesteRecHelper.Core.Obs;

namespace CelesteRecHelper.Core.Tests;

public sealed class ObsCaptureTests : IDisposable
{
    private readonly TestWorkspace workspace = new();
    public void Dispose() => workspace.Dispose();

    [Fact]
    public async Task SavesOnlyNewExpectedFilesAndRestoresBothDirectories()
    {
        var backend = new FakeOutputs(workspace) { SendWrongPathFirst = true };
        await using var server = await ObsTestServer.StartAsync(backend.RunAsync);
        await using var client = new ObsWebSocketClient(server.Endpoint);
        await client.ConnectAsync();
        var capture = new ObsCaptureSession(new ObsApi(client), workspace.Store);
        var custom = Path.Combine(workspace.Root, "custom");
        await capture.StartAsync(server.Endpoint.AbsoluteUri.TrimEnd('/'), custom);
        Assert.True(capture.OwnsReplayBuffer);
        Assert.Equal(custom, backend.SimpleDirectory);
        var first = await capture.SaveAsync();
        var second = await capture.SaveAsync();
        Assert.NotEqual(first.FilePath, second.FilePath);
        Assert.Equal(custom, Path.GetDirectoryName(first.FilePath));
        Assert.True(first.EventAtTimestamp >= first.RequestedAtTimestamp);
        Assert.True(File.Exists(first.FilePath));
        await capture.StopAsync(server.Endpoint.AbsoluteUri);
        Assert.Equal(workspace.VideoDirectory, backend.SimpleDirectory);
        Assert.Equal(Path.Combine(workspace.Root, "advanced"), backend.AdvancedDirectory);
        Assert.Null(workspace.Store.LoadObsRecovery());
        Assert.False(backend.ReplayActive);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task RefusesToTakeOverExistingOutputs(bool record, bool replay)
    {
        var backend = new FakeOutputs(workspace) { RecordingActive = record, ReplayActive = replay };
        await using var server = await ObsTestServer.StartAsync(backend.RunAsync);
        await using var client = new ObsWebSocketClient(server.Endpoint);
        await client.ConnectAsync();
        var capture = new ObsCaptureSession(new ObsApi(client), workspace.Store);
        await Assert.ThrowsAsync<InvalidOperationException>(() => capture.StartAsync(server.Endpoint.AbsoluteUri));
        Assert.DoesNotContain("StartReplayBuffer", backend.Commands);
        Assert.DoesNotContain("StopRecord", backend.Commands);
        Assert.DoesNotContain("StopReplayBuffer", backend.Commands);
    }

    [Fact]
    public async Task TimeoutBlocksFurtherSavesUntilTheOwnedBufferIsRestarted()
    {
        var backend = new FakeOutputs(workspace) { EmitSaveEvent = false };
        await using var server = await ObsTestServer.StartAsync(backend.RunAsync);
        await using var client = new ObsWebSocketClient(server.Endpoint);
        await client.ConnectAsync();
        var capture = new ObsCaptureSession(new ObsApi(client), workspace.Store);
        await capture.StartAsync(server.Endpoint.AbsoluteUri);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => capture.SaveAsync(TimeSpan.FromMilliseconds(100)));
        await Assert.ThrowsAsync<InvalidOperationException>(() => capture.SaveAsync());
        Assert.Equal(1, backend.Commands.Count(c => c == "SaveReplayBuffer"));
        await capture.StopAsync(server.Endpoint.AbsoluteUri);
        backend.EmitSaveEvent = true;
        await capture.StartAsync(server.Endpoint.AbsoluteUri);
        await capture.SaveAsync();
        await capture.StopAsync(server.Endpoint.AbsoluteUri);
    }

    [Fact]
    public async Task ProfileMismatchPreservesTheRecoveryRecordAndDoesNotChangeDirectories()
    {
        var backend = new FakeOutputs(workspace);
        await using var server = await ObsTestServer.StartAsync(backend.RunAsync);
        workspace.Store.SaveObsRecovery(new ObsDirectoryRecoveryRecord
        {
            Endpoint = server.Endpoint.AbsoluteUri, ProfileName = "Another profile", OriginalSimpleDirectory = "original-simple",
            OriginalAdvancedDirectory = "original-advanced", TemporaryDirectory = "temporary"
        });
        await using var client = new ObsWebSocketClient(server.Endpoint);
        await client.ConnectAsync();
        var capture = new ObsCaptureSession(new ObsApi(client), workspace.Store);
        Assert.NotNull(await capture.RestoreDirectoryAsync(server.Endpoint.AbsoluteUri));
        Assert.NotNull(workspace.Store.LoadObsRecovery());
        Assert.DoesNotContain("SetProfileParameter", backend.Commands);
    }

    [Theory]
    [InlineData(504, true)]
    [InlineData(604, true)]
    [InlineData(500, false)]
    public async Task OnlyAnExplicitDisabledOutputAllowsDirectoryRestoration(int errorCode, bool mayRestore)
    {
        var backend = new FakeOutputs(workspace) { ReplayStatusError = errorCode };
        await using var server = await ObsTestServer.StartAsync(backend.RunAsync);
        workspace.Store.SaveObsRecovery(new ObsDirectoryRecoveryRecord
        {
            Endpoint = server.Endpoint.AbsoluteUri, ProfileName = "Test", OriginalSimpleDirectory = workspace.VideoDirectory,
            OriginalAdvancedDirectory = workspace.VideoDirectory, TemporaryDirectory = "temporary"
        });
        await using var client = new ObsWebSocketClient(server.Endpoint);
        await client.ConnectAsync();
        var capture = new ObsCaptureSession(new ObsApi(client), workspace.Store);
        if (mayRestore)
        {
            Assert.Null(await capture.RestoreDirectoryAsync(server.Endpoint.AbsoluteUri));
            Assert.Null(workspace.Store.LoadObsRecovery());
        }
        else
        {
            await Assert.ThrowsAsync<ObsRequestException>(() => capture.RestoreDirectoryAsync(server.Endpoint.AbsoluteUri));
            Assert.NotNull(workspace.Store.LoadObsRecovery());
            Assert.DoesNotContain("SetProfileParameter", backend.Commands);
        }
    }

    [Theory]
    [InlineData(504)]
    [InlineData(604)]
    public async Task ConnectionCheckReportsAnUnavailableReplayWithoutFailingAuthentication(int errorCode)
    {
        var backend = new FakeOutputs(workspace) { ReplayStatusError = errorCode, ReplayEnabled = false };
        await using var server = await ObsTestServer.StartAsync(backend.RunAsync);
        await using var client = new ObsWebSocketClient(server.Endpoint);
        await client.ConnectAsync();
        var api = new ObsApi(client);
        var diagnostics = await new ObsInspector(api).InspectAsync();
        Assert.Equal(ObsConnectionState.Connected, client.State);
        Assert.False(diagnostics.ReplayAvailable);
        Assert.False(diagnostics.ReplayActive);
        Assert.False(diagnostics.Configuration.Enabled);
        Assert.NotEmpty(diagnostics.Warnings);
        Assert.DoesNotContain("GetOutputList", backend.Commands);
        var capture = new ObsCaptureSession(api, workspace.Store);
        await Assert.ThrowsAsync<InvalidOperationException>(() => capture.StartAsync(server.Endpoint.AbsoluteUri));
        Assert.DoesNotContain("StartReplayBuffer", backend.Commands);
    }

    private sealed class FakeOutputs(TestWorkspace workspace)
    {
        public int? ReplayStatusError { get; init; }
        public bool ReplayEnabled { get; init; } = true;
        public bool RecordingActive { get; set; }
        public bool ReplayActive { get; set; }
        public bool EmitSaveEvent { get; set; } = true;
        public bool SendWrongPathFirst { get; init; }
        public string SimpleDirectory { get; private set; } = workspace.VideoDirectory;
        public string AdvancedDirectory { get; private set; } = Path.Combine(workspace.Root, "advanced");
        public ConcurrentQueue<string> Commands { get; } = new();
        private int fileNumber;

        public async Task RunAsync(WebSocket socket, int _, CancellationToken ct)
        {
            while (true)
            {
                var request = await ObsTestServer.Receive(socket, ct);
                var type = request.GetProperty("requestType").GetString()!;
                Commands.Enqueue(type);
                if (type == "GetReplayBufferStatus" && ReplayStatusError is { } errorCode)
                {
                    await ObsTestServer.Reply(socket, request, null, ct, success: false, errorCode: errorCode);
                    continue;
                }
                object result = new { };
                switch (type)
                {
                    case "GetVersion": result = new { obsVersion = "32.2.2", obsWebSocketVersion = "5.7.4", availableRequests = new[] { "SetRecordDirectory" } }; break;
                    case "GetProfileList": result = new { currentProfileName = "Test" }; break;
                    case "GetRecordStatus": result = new { outputActive = RecordingActive }; break;
                    case "GetReplayBufferStatus": result = new { outputActive = ReplayActive }; break;
                    case "GetRecordDirectory": result = new { recordDirectory = SimpleDirectory }; break;
                    case "GetOutputList": result = new { outputs = Array.Empty<object>() }; break;
                    case "GetProfileParameter":
                        var data = request.GetProperty("requestData");
                        var name = data.GetProperty("parameterName").GetString();
                        var value = name switch { "Mode" => "Simple", "RecRB" => ReplayEnabled ? "true" : "false", "RecRBTime" => "300", "RecRBSize" => "512",
                            "RecEncoder" => "x264", "RecQuality" => "Stream", "FilePath" => SimpleDirectory, "RecFilePath" => AdvancedDirectory, _ => "" };
                        result = new { parameterValue = value, defaultParameterValue = (string?)null };
                        break;
                    case "SetRecordDirectory":
                        SimpleDirectory = AdvancedDirectory = request.GetProperty("requestData").GetProperty("recordDirectory").GetString()!;
                        break;
                    case "SetProfileParameter":
                        var parameter = request.GetProperty("requestData");
                        if (parameter.GetProperty("parameterCategory").GetString() == "SimpleOutput") SimpleDirectory = parameter.GetProperty("parameterValue").GetString()!;
                        else AdvancedDirectory = parameter.GetProperty("parameterValue").GetString()!;
                        break;
                    case "StartReplayBuffer": ReplayActive = true; break;
                    case "StopReplayBuffer": ReplayActive = false; break;
                    case "SaveReplayBuffer":
                        if (EmitSaveEvent)
                        {
                            if (SendWrongPathFirst) await ObsTestServer.Event(socket, "ReplayBufferSaved", new { savedReplayPath = Path.Combine(workspace.Root, "foreign.mkv") }, ct);
                            var path = Path.Combine(SimpleDirectory, $"save-{++fileNumber}.mkv");
                            await File.WriteAllBytesAsync(path, [1, 2, 3], ct);
                            await ObsTestServer.Event(socket, "ReplayBufferSaved", new { savedReplayPath = path }, ct);
                        }
                        break;
                }
                await ObsTestServer.Reply(socket, request, result, ct);
            }
        }
    }
}
