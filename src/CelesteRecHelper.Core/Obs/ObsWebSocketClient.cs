using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CelesteRecHelper.Core.Obs;

public enum ObsConnectionState { Disconnected, Connecting, Connected, Reconnecting, AuthenticationFailed }
public sealed record ObsEvent(string Type, JsonElement Data, long Timestamp, DateTimeOffset ReceivedAtUtc);
public sealed class ObsRequestException(string requestType, int code, string comment)
    : Exception($"OBS {requestType} failed ({code}): {comment}")
{
    public int Code { get; } = code;
    public string RequestType { get; } = requestType;
    public string Comment { get; } = comment;
}
public sealed class ObsReplayStoppedException(string message) : Exception(message);
public sealed class ObsTransportException(string message) : IOException(message);
public sealed class ObsAuthenticationException(string message) : Exception(message);

/// <summary>RPC 1 client. Reconnects the transport, but never retries state-changing requests.</summary>
public sealed class ObsWebSocketClient : IAsyncDisposable
{
    private readonly Uri endpoint;
    private readonly string password;
    private readonly TimeSpan timeout;
    private readonly TimeSpan reconnectDelay;
    private readonly CancellationTokenSource lifetime = new();
    private readonly SemaphoreSlim sendGate = new(1);
    private readonly object sync = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonElement>> pending = new();
    private TaskCompletionSource connected = NewSignal();
    private ClientWebSocket? socket;
    private Task? runner;
    private bool disposed;
    public ObsConnectionState State { get; private set; }
    public Guid ConnectionId { get; private set; }
    public string? LastError { get; private set; }
    public event Action<ObsEvent>? EventReceived;
    public event Action<ObsConnectionState>? StateChanged;

    public ObsWebSocketClient(Uri endpoint, string password = "", TimeSpan? timeout = null,
        TimeSpan? reconnectDelay = null)
    {
        if (endpoint.Scheme is not ("ws" or "wss") || !string.IsNullOrEmpty(endpoint.UserInfo))
            throw new ArgumentException("Use a ws:// or wss:// endpoint without embedded credentials.", nameof(endpoint));
        this.endpoint = endpoint;
        this.password = password;
        this.timeout = timeout ?? TimeSpan.FromSeconds(10);
        this.reconnectDelay = reconnectDelay ?? TimeSpan.FromSeconds(2);
    }

    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        Task wait;
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (runner is null || runner.IsCompleted)
            {
                connected = NewSignal();
                runner = Task.Run(RunAsync);
            }
            wait = connected.Task;
        }
        await wait.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
    }

    public async Task<JsonElement> RequestAsync(string type, object? data = null,
        CancellationToken cancellationToken = default)
    {
        ClientWebSocket current;
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            current = State == ObsConnectionState.Connected && socket is { } active ? active
                : throw new ObsTransportException("OBS is disconnected. The request was not sent.");
        }
        var id = Guid.NewGuid().ToString("N");
        var response = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        pending[id] = response;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Token);
        deadline.CancelAfter(timeout);
        try
        {
            await SendAsync(current, new { op = 6, d = new { requestType = type, requestId = id, requestData = data } }, deadline.Token)
                .ConfigureAwait(false);
            return await response.Task.WaitAsync(deadline.Token).ConfigureAwait(false);
        }
        finally { pending.TryRemove(id, out _); }
    }

    public static string ComputeAuthentication(string password, string salt, string challenge)
    {
        var secret = Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(password + salt)));
        return Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(secret + challenge)));
    }

    private async Task RunAsync()
    {
        var reconnecting = false;
        while (!lifetime.IsCancellationRequested)
        {
            using var current = new ClientWebSocket();
            try
            {
                SetState(reconnecting ? ObsConnectionState.Reconnecting : ObsConnectionState.Connecting);
                using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token))
                {
                    deadline.CancelAfter(timeout);
                    await current.ConnectAsync(endpoint, deadline.Token).ConfigureAwait(false);
                    var hello = await ReceiveAsync(current, deadline.Token).ConfigureAwait(false);
                    if (hello.GetProperty("op").GetInt32() != 0)
                        throw new InvalidDataException("OBS did not send Hello.");
                    var identify = new Dictionary<string, object> { ["rpcVersion"] = 1, ["eventSubscriptions"] = 66 };
                    var helloData = hello.GetProperty("d");
                    if (helloData.TryGetProperty("authentication", out var auth) && auth.ValueKind == JsonValueKind.Object)
                        identify["authentication"] = ComputeAuthentication(password,
                            auth.GetProperty("salt").GetString()!, auth.GetProperty("challenge").GetString()!);
                    await SendAsync(current, new { op = 1, d = identify }, deadline.Token).ConfigureAwait(false);
                    var identified = await ReceiveAsync(current, deadline.Token).ConfigureAwait(false);
                    if (identified.GetProperty("op").GetInt32() != 2 ||
                        identified.GetProperty("d").GetProperty("negotiatedRpcVersion").GetInt32() != 1)
                        throw new InvalidDataException("OBS did not negotiate RPC version 1.");
                }
                lock (sync) { socket = current; ConnectionId = Guid.NewGuid(); }
                LastError = null;
                SetState(ObsConnectionState.Connected);
                lock (sync) { connected.TrySetResult(); }
                while (!lifetime.IsCancellationRequested)
                {
                    var packet = await ReceiveAsync(current, lifetime.Token).ConfigureAwait(false);
                    var op = packet.GetProperty("op").GetInt32();
                    var data = packet.GetProperty("d");
                    if (op == 7 && pending.TryRemove(data.GetProperty("requestId").GetString()!, out var completion))
                    {
                        var status = data.GetProperty("requestStatus");
                        if (status.GetProperty("result").GetBoolean())
                            completion.TrySetResult(data.TryGetProperty("responseData", out var result)
                                ? result.Clone() : JsonSerializer.SerializeToElement(new { }));
                        else
                            completion.TrySetException(new ObsRequestException(data.GetProperty("requestType").GetString()!,
                                status.GetProperty("code").GetInt32(),
                                status.TryGetProperty("comment", out var comment) ? comment.GetString() ?? "" : ""));
                    }
                    else if (op == 5)
                    {
                        Notify(EventReceived, new ObsEvent(data.GetProperty("eventType").GetString()!,
                            data.GetProperty("eventData").Clone(), Stopwatch.GetTimestamp(), DateTimeOffset.UtcNow));
                    }
                }
            }
            catch (ObsAuthenticationException e)
            {
                LastError = e.Message;
                lock (sync) { connected.TrySetException(e); }
                SetState(ObsConnectionState.AuthenticationFailed);
                return;
            }
            catch (Exception e) when (e is WebSocketException or IOException or JsonException or OperationCanceledException
                       or InvalidDataException or InvalidOperationException or KeyNotFoundException)
            {
                if (!lifetime.IsCancellationRequested)
                    LastError = e is OperationCanceledException ? "OBS connection timed out." : e.Message;
            }
            finally
            {
                lock (sync)
                {
                    socket = null;
                    if (connected.Task.IsCompleted)
                        connected = NewSignal();
                }
                foreach (var pair in pending)
                    if (pending.TryRemove(pair.Key, out var completion))
                        completion.TrySetException(new ObsTransportException("OBS disconnected before responding; the request will not be retried."));
            }
            if (!lifetime.IsCancellationRequested)
            {
                reconnecting = true;
                SetState(ObsConnectionState.Reconnecting);
                try { await Task.Delay(reconnectDelay, lifetime.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }
        }
        SetState(ObsConnectionState.Disconnected);
    }

    private async Task SendAsync(ClientWebSocket current, object packet, CancellationToken cancellationToken)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(packet);
        await sendGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { await current.SendAsync(bytes.AsMemory(), WebSocketMessageType.Text, true, cancellationToken).ConfigureAwait(false); }
        finally { sendGate.Release(); }
    }

    private static async Task<JsonElement> ReceiveAsync(ClientWebSocket current, CancellationToken cancellationToken)
    {
        using var payload = new MemoryStream();
        var buffer = new byte[8192];
        ValueWebSocketReceiveResult result;
        do
        {
            result = await current.ReceiveAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                if ((int?)current.CloseStatus == 4009)
                    throw new ObsAuthenticationException("OBS authentication failed. Check the WebSocket password.");
                throw new ObsTransportException($"OBS closed the connection ({(int?)current.CloseStatus}).");
            }
            if (result.MessageType != WebSocketMessageType.Text || payload.Length + result.Count > 4 * 1024 * 1024)
                throw new InvalidDataException("Invalid or oversized OBS protocol message.");
            payload.Write(buffer, 0, result.Count);
        } while (!result.EndOfMessage);
        using var document = JsonDocument.Parse(payload.ToArray());
        return document.RootElement.Clone();
    }

    private void SetState(ObsConnectionState state)
    {
        lock (sync) { State = state; }
        Notify(StateChanged, state);
    }
    private static void Notify<T>(Action<T>? handlers, T value)
    {
        if (handlers is null) return;
        foreach (Action<T> handler in handlers.GetInvocationList())
            try { handler(value); } catch { /* An observer must not kill the receive loop. */ }
    }
    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    public async ValueTask DisposeAsync()
    {
        Task? task;
        lock (sync)
        {
            if (disposed) return;
            disposed = true;
            lifetime.Cancel();
            socket?.Abort();
            connected.TrySetCanceled();
            task = runner;
        }
        if (task is not null) await task.ConfigureAwait(false);
        lifetime.Dispose();
        // A request may still be unwinding its finally block; do not dispose the send semaphore here.
    }
}
