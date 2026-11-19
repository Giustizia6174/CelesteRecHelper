using System.Diagnostics;

namespace CelesteRecHelper.Core.Models;

public enum RouteStatus { Recording, Completed, Ended, Interrupted }
public enum SegmentStatus { Recorded, Saved, SaveFailed }
public enum MediaKind { ReplayBuffer, Recording, SegmentClip }
public enum SegmentSliceStatus { Pending, Succeeded, Failed }
public enum SaveRequestStatus { Pending, Saved, Failed }

/// <summary>One process clock. Raw timestamps are meaningful only within this clock.</summary>
public sealed record RecordingClock
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public long Frequency { get; init; } = Stopwatch.Frequency;
    public long OriginTimestamp { get; init; } = Stopwatch.GetTimestamp();
    public DateTimeOffset OriginUtc { get; init; } = DateTimeOffset.UtcNow;
}

public sealed record ObsSession
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public RecordingClock Clock { get; init; } = new();
    public required string Endpoint { get; init; }
    public required string ProfileName { get; init; }
    public required string OutputDirectory { get; init; }
    public DateTimeOffset StartedAtUtc { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? EndedAtUtc { get; set; }
    // Last observed states survive a crash; they do not claim the outputs are still running.
    public bool ReplayBufferWasActive { get; set; }
    public bool RecordingWasActive { get; set; }
}

/// <summary>Only the current life is stored; deaths do not append historical life records.</summary>
public sealed record CurrentLife
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required Guid ObsSessionId { get; init; }
    public required long StartedAtTimestamp { get; init; }
    public required long PendingStartTimestamp { get; set; }
    public List<MediaReference> PendingMedia { get; init; } = [];
}

public sealed record LogicalSegment
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required Guid LifeId { get; init; }
    public required Guid ObsSessionId { get; init; }
    public required long StartTimestamp { get; init; }
    public required long EndTimestamp { get; init; }
    public SegmentStatus Status { get; set; } = SegmentStatus.Recorded;
    public Guid? SaveRequestId { get; set; }
    public string? FailureReason { get; set; }
    public SegmentSliceStatus SliceStatus { get; set; } = SegmentSliceStatus.Pending;
    public string? SliceFailureReason { get; set; }
    public List<MediaReference> Media { get; init; } = [];
}

/// <summary>Coverage on the source session's monotonic clock, before media time conversion.</summary>
public sealed record MediaReference
{
    public required Guid MediaId { get; init; }
    public required long StartTimestamp { get; init; }
    public required long EndTimestamp { get; init; }
    // Exact logical bounds in a validated clip, including any keyframe/audio timestamp offset.
    public double? StartSeconds { get; init; }
    public double? EndSeconds { get; init; }
}

public sealed record MediaAsset
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required Guid ObsSessionId { get; init; }
    public required string FilePath { get; init; }
    public required MediaKind Kind { get; init; }
    public Guid? SaveRequestId { get; init; }
    public long? SaveRequestedAtTimestamp { get; init; }
    public long? SaveEventAtTimestamp { get; init; }
    public long? RecordStartedAtTimestamp { get; init; }
    public DateTimeOffset? SaveEventAtUtc { get; init; }
    // Recovery by directory/time is an inference, to be checked by the later media probe.
    public bool RecoveredAfterCrash { get; init; }
    public MediaProbeResult? Probe { get; set; }
}

public sealed record ReplaySaveRequest
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required Guid ObsSessionId { get; init; }
    public required string ExpectedDirectory { get; init; }
    public required long RequestedAtTimestamp { get; init; }
    public required DateTimeOffset RequestedAtUtc { get; init; }
    public required DateTimeOffset DeadlineUtc { get; init; }
    public SaveRequestStatus Status { get; set; } = SaveRequestStatus.Pending;
    // Snapshot existing paths before the request; recovery must not adopt an older file.
    public List<string> ExistingFiles { get; init; } = [];
    public string? OutputPath { get; set; }
    public string? FailureReason { get; set; }
}

public sealed record RouteManifest
{
    public const int CurrentSchemaVersion = 1;
    public int SchemaVersion { get; init; } = CurrentSchemaVersion;
    public Guid RouteId { get; init; } = Guid.NewGuid();
    public required int SaveSlot { get; init; }
    public required string MapSid { get; init; }
    public required string MapName { get; init; }
    public int AreaMode { get; init; }
    public RouteStatus Status { get; set; } = RouteStatus.Recording;
    public DateTimeOffset CreatedAtUtc { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public CurrentLife? CurrentLife { get; set; }
    public RecordingFailure? LastFailure { get; set; }
    public List<ObsSession> ObsSessions { get; init; } = [];
    public List<LogicalSegment> Segments { get; init; } = [];
    public List<MediaAsset> Media { get; init; } = [];
    public List<ReplaySaveRequest> SaveRequests { get; init; } = [];
}
