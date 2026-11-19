namespace CelesteRecHelper.Core.Recording;

public sealed record SegmentCutApplyResult(string? SourcePathToDelete, Guid? SourceMediaIdToRemove);
