namespace OpusMac.Core.Operations;

/// <summary>Verified operation-owned storage associated with one journal record.</summary>
public sealed record OperationStorageAreaReview(
    string Path,
    string Purpose,
    long Bytes,
    int Files,
    bool Reclaimable);

/// <summary>Storage and protection state for one operation journal record.</summary>
public sealed record OperationStorageRecordReview(
    string OperationId,
    DateTimeOffset StartedAt,
    FileOperationKind Kind,
    string Status,
    bool CanUndo,
    bool RecoveryProtected,
    bool Retired,
    bool DiscardPending,
    bool CanDiscard,
    long OwnedBytes,
    int OwnedFiles,
    long ReclaimableBytes,
    int ReclaimableFiles,
    IReadOnlyList<OperationStorageAreaReview> Areas,
    IReadOnlyList<string> Errors);

/// <summary>A UI-ready review of live, recovery-protected and reclaimable operation storage.</summary>
public sealed record OperationStorageReview(
    IReadOnlyList<OperationStorageRecordReview> Records,
    int UndoableRecords,
    int RecoveryProtectedRecords,
    int ReclaimableRecords,
    long ReclaimableBytes,
    int ReclaimableFiles,
    IReadOnlyList<string> Errors);

/// <summary>Result of explicitly forfeiting safe live Undo history and reclaiming its owned storage.</summary>
public sealed record OperationStorageDiscardResult(
    IReadOnlyList<string> DiscardedOperationIds,
    IReadOnlyList<string> RefusedOperationIds,
    IReadOnlyList<string> PendingOperationIds,
    IReadOnlyList<string> MissingOperationIds,
    long ReclaimedBytes,
    int ReclaimedFiles,
    IReadOnlyList<string> Errors);
