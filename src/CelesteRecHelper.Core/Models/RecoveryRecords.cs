namespace CelesteRecHelper.Core.Models;

/// <summary>Stored separately before modifying OBS. Contains no OBS password.</summary>
public sealed record ObsDirectoryRecoveryRecord
{
    public required string Endpoint { get; init; }
    public required string ProfileName { get; init; }
    public required string OriginalSimpleDirectory { get; init; }
    public required string OriginalAdvancedDirectory { get; init; }
    public required string TemporaryDirectory { get; init; }
    public DateTimeOffset CreatedAtUtc { get; init; } = DateTimeOffset.UtcNow;
}

public enum BackgroundJobState { Pending, Running, Succeeded, Failed }

public sealed record BackgroundJobStatus
{
    public required Guid JobId { get; init; }
    public required Guid RouteId { get; init; }
    public required BackgroundJobState State { get; init; }
    public string? Error { get; init; }
    public string Kind { get; init; } = "unknown";
    public string? RequestPath { get; init; }
    public string? ResultPath { get; init; }
    public DateTimeOffset UpdatedAtUtc { get; init; } = DateTimeOffset.UtcNow;
}
