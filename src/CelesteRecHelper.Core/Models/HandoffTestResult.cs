using CelesteRecHelper.Core.Obs;

namespace CelesteRecHelper.Core.Models;

public sealed record RecordingStarted(long RequestedAtTimestamp, long StartedAtTimestamp, DateTimeOffset StartedAtUtc);
public sealed record SavedRecording(string FilePath, RecordingStarted Start, long StopRequestedAtTimestamp,
    long StoppedAtTimestamp, DateTimeOffset StoppedAtUtc);
public sealed record HandoffCapture(Guid TestId, long ClockFrequency, ObsDiagnostics Configuration,
    double RequestedOverlapSeconds, SavedReplay Replay, SavedRecording Recording);
public enum OverlapStatus { Verified, Inconclusive }
public sealed record VideoFrameHash(double Seconds, string Hash);
public sealed record OverlapEvidence(OverlapStatus Status, string Explanation, int MatchedFrames,
    int DistinctFrames, double OverlapSeconds, double? ReplaySeamSeconds, double? RecordingSeamSeconds);
public sealed record HandoffVerification(HandoffCapture Capture, MediaProbeResult ReplayProbe,
    MediaProbeResult RecordingProbe, OverlapEvidence Evidence, double RecordStartupSeconds,
    double SaveCompletionSeconds, double MinimumSafetyMarginSeconds);
