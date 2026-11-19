namespace CelesteRecHelper.Core.Models;

public enum RecordingFailureKind { ObsRejected, Authentication, Timeout, Disconnected, OutputStopped, Storage, Media, Unknown }
public enum RecordingFailureStage { Start, Save, Manifest, Stop, Buffer }
public sealed record RecordingFailure(RecordingFailureKind Kind, RecordingFailureStage Stage, string MessageKey,
    string Detail, int? ObsCode = null, string? ObsRequest = null, string? ObsComment = null);
public sealed record ReplaySaveSnapshot(Guid RouteId, RouteStatus RouteStatus, int RecordedSegments, int SavedSegments,
    int FailedSegments, bool SaveInFlight, bool ReplayBufferOwned, RecordingFailure? Failure);
