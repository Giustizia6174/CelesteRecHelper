using System.Text.Json;

namespace CelesteRecHelper.Core.Obs;

public sealed class ObsApi(ObsWebSocketClient client)
{
    public ObsWebSocketClient Client { get; } = client;
    public Task<JsonElement> GetVersionAsync(CancellationToken ct = default) => Client.RequestAsync("GetVersion", cancellationToken: ct);
    public Task<JsonElement> GetReplayBufferStatusAsync(CancellationToken ct = default) => Client.RequestAsync("GetReplayBufferStatus", cancellationToken: ct);
    public Task<JsonElement> GetRecordStatusAsync(CancellationToken ct = default) => Client.RequestAsync("GetRecordStatus", cancellationToken: ct);
    public Task<JsonElement> StartReplayBufferAsync(CancellationToken ct = default) => Client.RequestAsync("StartReplayBuffer", cancellationToken: ct);
    public Task<JsonElement> StopReplayBufferAsync(CancellationToken ct = default) => Client.RequestAsync("StopReplayBuffer", cancellationToken: ct);
    public Task<JsonElement> SaveReplayBufferAsync(CancellationToken ct = default) => Client.RequestAsync("SaveReplayBuffer", cancellationToken: ct);
    public Task<JsonElement> GetLastReplayBufferReplayAsync(CancellationToken ct = default) => Client.RequestAsync("GetLastReplayBufferReplay", cancellationToken: ct);
    public Task<JsonElement> StartRecordAsync(CancellationToken ct = default) => Client.RequestAsync("StartRecord", cancellationToken: ct);
    public Task<JsonElement> StopRecordAsync(CancellationToken ct = default) => Client.RequestAsync("StopRecord", cancellationToken: ct);
    public Task<JsonElement> GetRecordDirectoryAsync(CancellationToken ct = default) => Client.RequestAsync("GetRecordDirectory", cancellationToken: ct);
    public Task<JsonElement> GetProfileListAsync(CancellationToken ct = default) => Client.RequestAsync("GetProfileList", cancellationToken: ct);
    public Task<JsonElement> GetOutputListAsync(CancellationToken ct = default) => Client.RequestAsync("GetOutputList", cancellationToken: ct);
    public Task<JsonElement> GetOutputSettingsAsync(string name, CancellationToken ct = default) =>
        Client.RequestAsync("GetOutputSettings", new { outputName = name }, ct);
    public Task<JsonElement> SetRecordDirectoryAsync(string directory, CancellationToken ct = default) =>
        Client.RequestAsync("SetRecordDirectory", new { recordDirectory = directory }, ct);
    public Task<JsonElement> SetProfileParameterAsync(string category, string name, string value, CancellationToken ct = default) =>
        Client.RequestAsync("SetProfileParameter", new { parameterCategory = category, parameterName = name, parameterValue = value }, ct);
    public async Task<string?> GetProfileParameterAsync(string category, string name, CancellationToken ct = default)
    {
        var response = await Client.RequestAsync("GetProfileParameter", new { parameterCategory = category, parameterName = name }, ct).ConfigureAwait(false);
        var value = response.GetProperty("parameterValue");
        return value.ValueKind == JsonValueKind.Null ? response.GetProperty("defaultParameterValue").GetString() : value.GetString();
    }
}
