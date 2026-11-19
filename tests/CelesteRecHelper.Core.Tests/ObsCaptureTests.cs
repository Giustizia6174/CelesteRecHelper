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

    [Fact]
    public async Task RecordingWaitsForRunningAndStoppedEventsAndReturnsTheClosedFile()
    {
        var backend = new FakeOutputs(workspace);
        await using var server = await ObsTestServer.StartAsync(backend.RunAsync);
        await using var client = new ObsWebSocketClient(server.Endpoint);
        await client.ConnectAsync();
        var api = new ObsApi(client);
        var recording = new ObsRecordingSession(api, workspace.VideoDirectory);
        var started = await recording.StartAsync();
        Assert.True(started.StartedAtTimestamp >= started.RequestedAtTimestamp);
        Assert.True(recording.OwnsRecording);
        var saved = await recording.StopAsync();
        Assert.NotNull(saved);
        Assert.Equal(started, saved.Start);
        Assert.True(saved.StoppedAtTimestamp >= saved.StopRequestedAtTimestamp);
        Assert.True(File.Exists(saved.FilePath));
        Assert.False(recording.OwnsRecording);
        Assert.Null(await recording.StopAsync());
        Assert.Equal(1, backend.Commands.Count(c => c == "StopRecord"));
    }

    [Fact]
    public async Task ExistingRecordingIsNeitherStartedNorStopped()
    {
        var backend = new FakeOutputs(workspace) { RecordingActive = true };
        await using var server = await ObsTestServer.StartAsync(backend.RunAsync);
        await using var client = new ObsWebSocketClient(server.Endpoint);
        await client.ConnectAsync();
        var recording = new ObsRecordingSession(new ObsApi(client), workspace.VideoDirectory);
        await Assert.ThrowsAsync<InvalidOperationException>(() => recording.StartAsync());
        Assert.Null(await recording.StopAsync());
        Assert.DoesNotContain("StartRecord", backend.Commands);
        Assert.DoesNotContain("StopRecord", backend.Commands);
    }

    [Fact]
    public async Task SuccessfulStartResponseWithoutRunningEventTimesOutAndCleanupRetainsTheRecording()
    {
        var backend = new FakeOutputs(workspace) { EmitRecordingStartEvent = false };
        await using var server = await ObsTestServer.StartAsync(backend.RunAsync);
        await using var client = new ObsWebSocketClient(server.Endpoint);
        await client.ConnectAsync();
        var recording = new ObsRecordingSession(new ObsApi(client), workspace.VideoDirectory);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => recording.StartAsync(TimeSpan.FromMilliseconds(100)));
        Assert.True(recording.OwnsRecording);
        await Assert.ThrowsAsync<InvalidDataException>(() => recording.StopAsync());
        Assert.False(recording.OwnsRecording);
        Assert.True(File.Exists(Path.Combine(workspace.VideoDirectory, "record.mkv")));
        Assert.Equal(1, backend.Commands.Count(c => c == "StopRecord"));
    }

    [Fact]
    public async Task ReconnectedTransportDoesNotReuseOldRecordingOrReplayOwnership()
    {
        var backend = new FakeOutputs(workspace);
        await using var server = await ObsTestServer.StartAsync(backend.RunAsync);
        await using var client = new ObsWebSocketClient(server.Endpoint, reconnectDelay: TimeSpan.FromMilliseconds(10));
        await client.ConnectAsync();
        var api = new ObsApi(client);
        var buffer = new ObsCaptureSession(api, workspace.Store);
        await buffer.StartAsync(server.Endpoint.AbsoluteUri);
        var recording = new ObsRecordingSession(api, workspace.VideoDirectory);
        await recording.StartAsync();
        var originalConnection = client.ConnectionId;
        var connected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.StateChanged += state => { if (state == ObsConnectionState.Connected && client.ConnectionId != originalConnection) connected.TrySetResult(); };
        await Assert.ThrowsAsync<IOException>(() => client.RequestAsync("Disconnect"));
        await connected.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.ThrowsAsync<IOException>(() => recording.StopAsync());
        await Assert.ThrowsAsync<IOException>(() => buffer.SaveAsync());
        await Assert.ThrowsAsync<IOException>(() => buffer.StopAsync(server.Endpoint.AbsoluteUri));
        Assert.DoesNotContain("StopRecord", backend.Commands);
        Assert.DoesNotContain("StopReplayBuffer", backend.Commands);
        Assert.DoesNotContain("SaveReplayBuffer", backend.Commands);
    }

    [Fact]
    public async Task CancellingDuringHandoffClosesBothOwnedOutputsAndKeepsFailureEvidence()
    {
        var backend = new FakeOutputs(workspace);
        await using var server = await ObsTestServer.StartAsync(backend.RunAsync);
        await using var client = new ObsWebSocketClient(server.Endpoint);
        await client.ConnectAsync();
        using var cancellation = new CancellationTokenSource();
        client.EventReceived += message =>
        {
            if (message.Type == "RecordStateChanged" && message.Data.GetProperty("outputState").GetString() == "OBS_WEBSOCKET_OUTPUT_STARTED")
                cancellation.CancelAfter(50);
        };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            ObsHandoffTest.RunAsync(new ObsApi(client), workspace.Store, server.Endpoint.AbsoluteUri, ct: cancellation.Token));
        Assert.False(backend.RecordingActive);
        Assert.False(backend.ReplayActive);
        Assert.Equal(1, backend.Commands.Count(c => c == "StopRecord"));
        Assert.Equal(1, backend.Commands.Count(c => c == "StopReplayBuffer"));
        Assert.DoesNotContain("SaveReplayBuffer", backend.Commands);
        Assert.True(File.Exists(Path.Combine(workspace.VideoDirectory, "record.mkv")));
        Assert.Single(Directory.EnumerateFiles(workspace.Store.RootDirectory, "error.json", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task FailedHandoffStartupRestoresCustomDirectoriesWithoutStoppingForeignOutputs()
    {
        var backend = new FakeOutputs(workspace) { StartReplayError = 500 };
        await using var server = await ObsTestServer.StartAsync(backend.RunAsync);
        await using var client = new ObsWebSocketClient(server.Endpoint);
        await client.ConnectAsync();
        var custom = Path.Combine(workspace.Root, "custom");
        await Assert.ThrowsAsync<ObsRequestException>(() =>
            ObsHandoffTest.RunAsync(new ObsApi(client), workspace.Store, server.Endpoint.AbsoluteUri, custom));
        Assert.Equal(workspace.VideoDirectory, backend.SimpleDirectory);
        Assert.Equal(Path.Combine(workspace.Root, "advanced"), backend.AdvancedDirectory);
        Assert.Null(workspace.Store.LoadObsRecovery());
        Assert.DoesNotContain("StopReplayBuffer", backend.Commands);
        Assert.DoesNotContain("StopRecord", backend.Commands);
        Assert.Single(Directory.EnumerateFiles(workspace.Store.RootDirectory, "error.json", SearchOption.AllDirectories));
        Assert.Empty(Directory.EnumerateFiles(workspace.Store.RootDirectory, "cleanup-error.json", SearchOption.AllDirectories));
    }

    private sealed class FakeOutputs(TestWorkspace workspace)
    {
        public int? ReplayStatusError { get; init; }
        public int? StartReplayError { get; init; }
        public bool ReplayEnabled { get; init; } = true;
        public bool RecordingActive { get; set; }
        public bool ReplayActive { get; set; }
        public bool EmitSaveEvent { get; set; } = true;
        public bool EmitRecordingStartEvent { get; init; } = true;
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
                if (type == "Disconnect") { socket.Abort(); return; }
                if (type == "GetReplayBufferStatus" && ReplayStatusError is { } errorCode)
                {
                    await ObsTestServer.Reply(socket, request, null, ct, success: false, errorCode: errorCode);
                    continue;
                }
                if (type == "StartReplayBuffer" && StartReplayError is { } startError)
                {
                    await ObsTestServer.Reply(socket, request, null, ct, success: false, errorCode: startError);
                    continue;
                }
                object result = new { };
                switch (type)
                {
                    case "GetVersion": result = new { obsVersion = "32.2.2", obsWebSocketVersion = "5.7.4", availableRequests = new[] { "SetRecordDirectory" } }; break;
                    case "GetProfileList": result = new { currentProfileName = "Test" }; break;
                    case "GetRecordStatus": result = new { outputActive = RecordingActive }; break;
                    case "GetReplayBufferStatus": result = new { outputActive = ReplayActive }; break;
                    case "StartRecord":
                        RecordingActive = true;
                        // Reply first: the caller must still wait for the later running event.
                        await ObsTestServer.Reply(socket, request, new { }, ct);
                        if (EmitRecordingStartEvent)
                        {
                            await Task.Delay(25, ct);
                            await ObsTestServer.Event(socket, "RecordStateChanged", new { outputActive = true, outputState = "OBS_WEBSOCKET_OUTPUT_STARTED", outputPath = (string?)null }, ct);
                        }
                        continue;
                    case "StopRecord":
                        RecordingActive = false;
                        var recordPath = Path.Combine(SimpleDirectory, "record.mkv");
                        await File.WriteAllBytesAsync(recordPath, [4, 5, 6], ct);
                        await ObsTestServer.Reply(socket, request, new { outputPath = recordPath }, ct);
                        await Task.Delay(25, ct);
                        await ObsTestServer.Event(socket, "RecordStateChanged", new { outputActive = false, outputState = "OBS_WEBSOCKET_OUTPUT_STOPPED", outputPath = recordPath }, ct);
                        continue;
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
