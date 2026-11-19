using System.Net.WebSockets;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;

namespace CelesteRecHelper.Core.Tests;

internal sealed class ObsTestServer : IAsyncDisposable
{
    private readonly WebApplication app;
    private readonly CancellationTokenSource shutdown;
    public Uri Endpoint { get; }
    private ObsTestServer(WebApplication app, CancellationTokenSource shutdown)
    {
        this.app = app;
        this.shutdown = shutdown;
        Endpoint = new Uri(app.Urls.Single().Replace("http://", "ws://", StringComparison.Ordinal));
    }

    public static async Task<ObsTestServer> StartAsync(Func<WebSocket, int, CancellationToken, Task> handler,
        bool authenticate = false)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var app = builder.Build();
        app.UseWebSockets();
        var index = 0;
        var shutdown = new CancellationTokenSource();
        app.Run(async context =>
        {
            using var socket = await context.WebSockets.AcceptWebSocketAsync();
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, shutdown.Token);
            var ct = linked.Token;
            try
            {
                await Send(socket, new { op = 0, d = new { rpcVersion = 1, authentication = authenticate
                    ? new { salt = "salt", challenge = "challenge" } : null } }, ct);
                var identify = await Receive(socket, ct);
                if (authenticate && identify.GetProperty("authentication").GetString() != "zTM5ki6L2vVvBQiTG9ckH1Lh64AbnCf6XZ226UmnkIA=")
                {
                    await socket.CloseAsync((WebSocketCloseStatus)4009, "Authentication failed", ct);
                    return;
                }
                await Send(socket, new { op = 2, d = new { negotiatedRpcVersion = 1 } }, ct);
                await handler(socket, Interlocked.Increment(ref index), ct);
            }
            catch (Exception e) when (e is WebSocketException or OperationCanceledException or IOException) { }
        });
        await app.StartAsync();
        return new ObsTestServer(app, shutdown);
    }

    public static async Task<JsonElement> Receive(WebSocket socket, CancellationToken ct)
    {
        using var stream = new MemoryStream();
        var bytes = new byte[8192];
        WebSocketReceiveResult packet;
        do
        {
            packet = await socket.ReceiveAsync(bytes, ct);
            if (packet.MessageType == WebSocketMessageType.Close) throw new IOException("Client closed.");
            stream.Write(bytes, 0, packet.Count);
        } while (!packet.EndOfMessage);
        using var json = JsonDocument.Parse(stream.ToArray());
        return json.RootElement.GetProperty("d").Clone();
    }
    public static Task Send(WebSocket socket, object value, CancellationToken ct) =>
        socket.SendAsync(JsonSerializer.SerializeToUtf8Bytes(value), WebSocketMessageType.Text, true, ct);
    public static Task Reply(WebSocket socket, JsonElement request, object? result, CancellationToken ct, bool success = true, int errorCode = 500) =>
        Send(socket, new { op = 7, d = new { requestType = request.GetProperty("requestType").GetString(),
            requestId = request.GetProperty("requestId").GetString(), requestStatus = new { result = success, code = success ? 100 : errorCode, comment = "test error" },
            responseData = result ?? new { } } }, ct);
    public static Task Event(WebSocket socket, string name, object data, CancellationToken ct) =>
        Send(socket, new { op = 5, d = new { eventType = name, eventIntent = 64, eventData = data } }, ct);
    public async ValueTask DisposeAsync()
    {
        shutdown.Cancel();
        await app.DisposeAsync();
        shutdown.Dispose();
    }
}
