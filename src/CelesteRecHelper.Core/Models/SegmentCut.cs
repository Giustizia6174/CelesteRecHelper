namespace CelesteRecHelper.Core.Models;

/// <summary>Immutable input passed to the media worker for one saved replay file.</summary>
public sealed record SegmentCutRequest
{
    public required Guid RouteId { get; init; }
    public required Guid SaveRequestId { get; init; }
    public required Guid SourceMediaId { get; init; }
    public required Guid ObsSessionId { get; init; }
    public required string SourcePath { get; init; }
    public required string OutputDirectory { get; init; }
    public required string MapName { get; init; }
    public required long ClockFrequency { get; init; }
    public required long SaveRequestedAtTimestamp { get; init; }
    public List<SegmentCutItem> Segments { get; init; } = [];
}

public sealed record SegmentCutItem
{
    public required Guid SegmentId { get; init; }
    public required int Sequence { get; init; }
    public required long StartTimestamp { get; init; }
    public required long EndTimestamp { get; init; }
}

public sealed record SegmentCutResult
{
    public required Guid RouteId { get; init; }
    public required Guid SaveRequestId { get; init; }
    public required Guid SourceMediaId { get; init; }
    public required Guid ObsSessionId { get; init; }
    public required string SourcePath { get; init; }
    public List<SegmentCutItemResult> Segments { get; init; } = [];
}

public sealed record SegmentCutItemResult
{
    public required Guid SegmentId { get; init; }
    public required int Sequence { get; init; }
    public bool Succeeded { get; init; }
    public string? FilePath { get; init; }
    public MediaProbeResult? Probe { get; init; }
    public double? StartSeconds { get; init; }
    public double? EndSeconds { get; init; }
    public string? Error { get; init; }
}
