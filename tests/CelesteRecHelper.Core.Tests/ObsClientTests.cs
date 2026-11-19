using System.Text.Json;
using CelesteRecHelper.Core.Obs;

namespace CelesteRecHelper.Core.Tests;

public sealed class ObsClientTests
{
    [Fact]
    public async Task AuthenticatesAndMatchesOutOfOrderResponses()
    {
        await using var server = await ObsTestServer.StartAsync(async (socket, _, ct) =>
        {
            var first = await ObsTestServer.Receive(socket, ct);
            var second = await ObsTestServer.Receive(socket, ct);
            await ObsTestServer.Reply(socket, second, new { name = second.GetProperty("requestType").GetString() }, ct);
            await ObsTestServer.Reply(socket, first, new { name = first.GetProperty("requestType").GetString() }, ct);
            await Task.Delay(Timeout.Infinite, ct);
        }, authenticate: true);
        await using var client = new ObsWebSocketClient(server.Endpoint, "password");
        await client.ConnectAsync();
        var one = client.RequestAsync("One");
        var two = client.RequestAsync("Two");
        Assert.Equal("One", (await one).GetProperty("name").GetString());
        Assert.Equal("Two", (await two).GetProperty("name").GetString());
    }

    [Fact]
    public async Task WrongPasswordProducesAnAuthenticationError()
    {
        await using var server = await ObsTestServer.StartAsync((_, _, _) => Task.CompletedTask, authenticate: true);
        await using var client = new ObsWebSocketClient(server.Endpoint, "wrong");
        await Assert.ThrowsAsync<ObsAuthenticationException>(() => client.ConnectAsync());
    }

    [Fact]
    public async Task DispatchesAllOutputEventsEvenWhenAnotherObserverThrows()
    {
        await using var server = await ObsTestServer.StartAsync(async (socket, _, ct) =>
        {
            var request = await ObsTestServer.Receive(socket, ct);
            foreach (var name in new[] { "ReplayBufferSaved", "ReplayBufferStateChanged", "RecordStateChanged" })
                await ObsTestServer.Event(socket, name, new { outputActive = true, outputState = "OBS_WEBSOCKET_OUTPUT_STARTED", savedReplayPath = "test" }, ct);
            await ObsTestServer.Reply(socket, request, new { }, ct);
            await Task.Delay(Timeout.Infinite, ct);
        });
        await using var client = new ObsWebSocketClient(server.Endpoint);
        var events = new List<ObsEvent>();
        client.EventReceived += _ => throw new InvalidOperationException("Observer error.");
        client.EventReceived += events.Add;
        await client.ConnectAsync();
        await client.RequestAsync("Events");
        Assert.Equal(new[] { "ReplayBufferSaved", "ReplayBufferStateChanged", "RecordStateChanged" }, events.Select(e => e.Type));
        Assert.All(events, e => Assert.True(e.Timestamp > 0));
    }

    [Fact]
    public async Task RequestErrorsAndTimeoutsDoNotBlockLaterRequests()
    {
        await using var server = await ObsTestServer.StartAsync(async (socket, _, ct) =>
        {
            while (true)
            {
                var request = await ObsTestServer.Receive(socket, ct);
                if (request.GetProperty("requestType").GetString() == "NoReply") continue;
                await ObsTestServer.Reply(socket, request, new { }, ct,
                    request.GetProperty("requestType").GetString() != "Error");
            }
        });
        await using var client = new ObsWebSocketClient(server.Endpoint, timeout: TimeSpan.FromSeconds(1));
        await client.ConnectAsync();
        var error = await Assert.ThrowsAsync<ObsRequestException>(() => client.RequestAsync("Error"));
        Assert.Equal(500, error.Code);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.RequestAsync("NoReply"));
        await client.RequestAsync("Success");
    }

    [Fact]
    public async Task ReconnectsWithoutReplayingAnAmbiguousCommand()
    {
        var reconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var savedCommands = 0;
        await using var server = await ObsTestServer.StartAsync(async (socket, index, ct) =>
        {
            if (index == 2) reconnected.TrySetResult();
            var request = await ObsTestServer.Receive(socket, ct);
            if (request.GetProperty("requestType").GetString() == "SaveReplayBuffer")
            {
                Interlocked.Increment(ref savedCommands);
                socket.Abort();
                return;
            }
            await ObsTestServer.Reply(socket, request, new { }, ct);
            await Task.Delay(Timeout.Infinite, ct);
        });
        await using var client = new ObsWebSocketClient(server.Endpoint, reconnectDelay: TimeSpan.FromMilliseconds(20));
        await client.ConnectAsync();
        await Assert.ThrowsAsync<IOException>(() => client.RequestAsync("SaveReplayBuffer"));
        await reconnected.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await client.ConnectAsync();
        await client.RequestAsync("GetVersion");
        Assert.Equal(1, savedCommands);
    }
}
