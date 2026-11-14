using System.Text.Json;
using OpusMac.Core.Platform;
using OpusMac.Core.Search;

namespace OpusMac.Core.Operations;

/// <summary>One serialized, journaled owner for all user-visible file mutations.</summary>
public sealed class FileOperationService : IDisposable
{
    private readonly IFilePlatform _platform;
    private readonly OperationJournal _journal;
    private readonly List<JournalOperation> _operations;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly object _historyGate = new();
    private IReadOnlyList<OperationHistoryItem> _history = Array.Empty<OperationHistoryItem>();
    private bool _canUndo;
    private int _disposed;
    private string? _journalFault;

    public FileOperationService(IFilePlatform platform, string stateDirectory)
        : this(platform, stateDirectory, OperationJournalLimits.Default) { }

    internal FileOperationService(IFilePlatform platform, string stateDirectory, OperationJournalLimits limits)
    {
        _platform = platform ?? throw new ArgumentNullException(nameof(platform));
        _journal = new OperationJournal(stateDirectory, limits);
        try { _operations = _journal.Load(); }
        catch { _journal.Dispose(); throw; }
        foreach (var operation in _operations.Where(o => o.Status is "Running" or "UndoRunning"))
        {
            operation.Status = "Interrupted";
            operation.Errors.Add("The application stopped before completion. Undo will reconcile recorded changes; recovery files remain in their recorded locations.");
            foreach (var item in operation.Mutations.Where(m => !m.Undone)) item.RecoveryRequired = true;
        }
        ResumeConfirmedDiscards();
        PublishHistory();
    }

    public IReadOnlyList<OperationHistoryItem> History { get { lock (_historyGate) return _history; } }
    public bool CanUndo { get { lock (_historyGate) return _canUndo; } }
    public event Action? HistoryChanged;

    public Task<FileOperationResult> ExecuteAsync(FileOperationRequest request,
        IProgress<FileOperationProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ArgumentNullException.ThrowIfNull(request);
        return Task.Run(() => ExecuteCoreAsync(request, progress, cancellationToken), CancellationToken.None);
    }

    public Task<FileOperationResult> UndoAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        return Task.Run(() => UndoCoreAsync(cancellationToken), CancellationToken.None);
    }

    public Task<OperationStorageReview> ReviewStorageAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        return Task.Run(() => ReviewStorageCoreAsync(cancellationToken), CancellationToken.None);
    }

    public Task<OperationStorageDiscardResult> DiscardUndoHistoryAsync(
        IReadOnlyCollection<string> reviewedOperationIds, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ArgumentNullException.ThrowIfNull(reviewedOperationIds);
        var ids = reviewedOperationIds.ToArray();
        if (ids.Any(string.IsNullOrWhiteSpace)) throw new ArgumentException("Operation ids cannot be empty.", nameof(reviewedOperationIds));
        return Task.Run(() => DiscardUndoHistoryCoreAsync(ids, cancellationToken), CancellationToken.None);
    }

    private async Task<OperationStorageReview> ReviewStorageCoreAsync(CancellationToken callerToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(callerToken, _shutdown.Token);
        await _gate.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            return BuildStorageInspection().Review;
        }
        finally { _gate.Release(); }
    }

    private async Task<OperationStorageDiscardResult> DiscardUndoHistoryCoreAsync(
        IReadOnlyCollection<string> reviewedOperationIds, CancellationToken callerToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(callerToken, _shutdown.Token);
        await _gate.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            if (_journalFault is not null)
                return new([], reviewedOperationIds.Distinct(StringComparer.Ordinal).ToArray(), [], [], 0, 0, [_journalFault]);

            linked.Token.ThrowIfCancellationRequested();
            var inspection = BuildStorageInspection();
            var selected = new List<StorageRecordInspection>();
            var refused = new List<string>();
            var missing = new List<string>();
            var errors = new List<string>();
            foreach (var id in reviewedOperationIds.Distinct(StringComparer.Ordinal))
            {
                var matches = inspection.Records.Where(record => string.Equals(record.Operation.Id, id, StringComparison.Ordinal)).ToArray();
                if (matches.Length == 0)
                {
                    missing.Add(id);
                    continue;
                }
                if (matches.Length != 1 || !matches[0].Review.CanDiscard)
                {
                    refused.Add(id);
                    var details = matches.SelectMany(record => record.ValidationErrors).Distinct(StringComparer.Ordinal).ToArray();
                    errors.Add(details.Length == 0
                        ? $"{id}: the reviewed Undo record is no longer safe to discard."
                        : $"{id}: {string.Join(" | ", details)}");
                    continue;
                }
                selected.Add(matches[0]);
            }
            if (selected.Count == 0)
                return new([], refused, [], missing, 0, 0, errors);

            foreach (var record in selected) record.Operation.DiscardRequested = true;
            try
            {
                // This commit is the irreversible boundary: after it succeeds Undo
                // is intentionally forfeited, and restart may finish owned cleanup.
                _journal.Save(_operations);
            }
            catch (Exception error)
            {
                foreach (var record in selected) record.Operation.DiscardRequested = false;
                refused.AddRange(selected.Select(record => record.Operation.Id));
                errors.Add("Undo history was not discarded because its intent could not be journaled: " + error.Message);
                return new([], refused.Distinct(StringComparer.Ordinal).ToArray(), [], missing, 0, 0, errors);
            }
            PublishHistory();

            long reclaimedBytes = 0;
            var reclaimedFiles = 0;
            var cleaned = new HashSet<JournalOperation>(ReferenceEqualityComparer.Instance);
            var pending = new List<string>();
            foreach (var record in selected)
            {
                if (TryCleanupConfirmedDiscard(record, ref reclaimedBytes, ref reclaimedFiles, out var cleanupError))
                    cleaned.Add(record.Operation);
                else
                {
                    pending.Add(record.Operation.Id);
                    AddDiscardError(record.Operation, cleanupError!);
                    errors.Add($"{record.Operation.Id}: {cleanupError}");
                }
            }

            var retained = _operations.Where(operation => !cleaned.Contains(operation)).ToList();
            try
            {
                _journal.Save(retained);
                _operations.Clear();
                _operations.AddRange(retained);
            }
            catch (Exception error)
            {
                foreach (var record in selected)
                    if (!pending.Contains(record.Operation.Id, StringComparer.Ordinal)) pending.Add(record.Operation.Id);
                errors.Add("Owned storage cleanup completed where possible, but the journal could not record completion: " + error.Message);
                if (error is not OperationHistoryCapacityException)
                    _journalFault = "The operation journal could not be saved. Further changes are blocked until the state directory is writable: " + error.Message;
                PublishHistory();
                return new([], refused, pending.Distinct(StringComparer.Ordinal).ToArray(), missing,
                    reclaimedBytes, reclaimedFiles, errors);
            }

            PublishHistory();
            var discarded = selected.Where(record => cleaned.Contains(record.Operation)).Select(record => record.Operation.Id).ToArray();
            return new(discarded, refused, pending.Distinct(StringComparer.Ordinal).ToArray(), missing,
                reclaimedBytes, reclaimedFiles, errors);
        }
        finally { _gate.Release(); }
    }

    private async Task<FileOperationResult> ExecuteCoreAsync(FileOperationRequest request,
        IProgress<FileOperationProgress>? progress, CancellationToken callerToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(callerToken, _shutdown.Token);
        var token = linked.Token;
        var operation = new JournalOperation { Id = Guid.NewGuid().ToString("N"), StartedAt = DateTimeOffset.UtcNow, Kind = request.Kind };
        try { await _gate.WaitAsync(token).ConfigureAwait(false); }
        catch (OperationCanceledException) { return new(operation.Id, false, true, [], []); }
        var canceled = false;
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            if (_journalFault is not null) throw new IOException(_journalFault);
            var plans = Prepare(request);
            long reservation = 4096;
            foreach (var plan in plans)
            {
                var sourceBytes = JsonEncodedText.Encode(plan.Source).EncodedUtf8Bytes.Length;
                var destinationBytes = JsonEncodedText.Encode(plan.Destination ?? plan.Source).EncodedUtf8Bytes.Length;
                // A record can retain source, destination, staging, backup and undo
                // paths, plus hashes, receipts and diagnostics. Reserve before native work.
                reservation = checked(reservation + 4096 + 16L * Math.Max(sourceBytes, destinationBytes));
            }
            _journal.EnsureCanBegin(_operations, reservation);
            _operations.Add(operation);
            Persist();
            if (request.Kind is FileOperationKind.Rename or FileOperationKind.BatchRename)
            {
                try { await RenameAsync(operation, plans, request.Collision, token).ConfigureAwait(false); }
                catch (Exception error)
                {
                    canceled = error is OperationCanceledException;
                    if (!canceled) operation.Errors.Add(error.Message);
                    await RollbackRenamesAsync(operation).ConfigureAwait(false);
                }
                Report(progress, new(operation.Id, "Rename", plans.Count, plans.Count));
            }
            else
            {
                var completed = 0;
                foreach (var plan in plans)
                {
                    token.ThrowIfCancellationRequested();
                    Report(progress, new(operation.Id, $"{request.Kind}: {Path.GetFileName(plan.Source.Length > 0 ? plan.Source : plan.Destination)}", completed, plans.Count));
                    var item = new JournalMutation { Source = plan.Source, Destination = plan.Destination, Action = request.Kind.ToString() };
                    operation.Mutations.Add(item);
                    Persist();
                    try
                    {
                        if (request.Kind == FileOperationKind.Trash) await TrashAsync(operation, item, token).ConfigureAwait(false);
                        else await TransferAsync(operation, item, request.Collision, token, request.ReviewedSource,
                            request.ReviewedDestination, request.ReviewedDestinationParentIdentity).ConfigureAwait(false);
                    }
                    catch (Exception error)
                    {
                        if (error is OperationCanceledException) canceled = true;
                        else operation.Errors.Add($"{Display(item)}: {error.Message}");
                        await CleanupFailedAsync(operation, item).ConfigureAwait(false);
                        if (canceled) break;
                    }
                    completed++;
                    Report(progress, new(operation.Id, request.Kind.ToString(), completed, plans.Count));
                }
            }
            operation.Status = canceled ? "Canceled" : operation.Errors.Count == 0 ? "Succeeded" :
                operation.Mutations.Any(m => m.Applied) ? "Partial" : "Failed";
            Persist();
        }
        catch (OperationCanceledException)
        {
            canceled = true;
            operation.Status = "Canceled";
            TryPersist(operation);
        }
        catch (Exception error)
        {
            operation.Errors.Add(error.Message);
            operation.Status = operation.Mutations.Any(m => m.Applied) ? "Partial" : "Failed";
            TryPersist(operation);
        }
        finally { PublishHistory(); _gate.Release(); }
        return new(operation.Id, !canceled && operation.Errors.Count == 0 && operation.Status == "Succeeded",
            canceled, operation.Changes.ToArray(), operation.Errors.ToArray());
    }

    private List<Plan> Prepare(FileOperationRequest request)
    {
        if (!Enum.IsDefined(request.Kind) || !Enum.IsDefined(request.Collision)) throw new ArgumentException("Unknown file operation or collision policy.");
        if ((request.ReviewedSource is not null || request.ReviewedDestination is not null) &&
            (request.Kind != FileOperationKind.Copy || request.Sources.Count != 1))
            throw new ArgumentException("Reviewed file identities require a single-file copy.");
        if (request.Kind == FileOperationKind.HardLink && request.Collision != CollisionPolicy.Fail)
            throw new ArgumentException("Hard links require a vacant destination.");
        var plans = new List<Plan>();
        if (request.Kind is FileOperationKind.CreateDirectory or FileOperationKind.CreateFile)
        {
            OperationPaths.ValidName(request.NewName);
            var directory = OperationPaths.DirectoryPath(request.DestinationDirectory ?? throw new ArgumentException("Choose a destination folder."));
            plans.Add(new("", Path.Combine(directory, request.NewName!)));
        }
        else if (request.Kind == FileOperationKind.BatchRename)
        {
            if (request.Renames is null || request.Renames.Count == 0) throw new ArgumentException("The rename plan is empty.");
            foreach (var mapping in request.Renames)
            {
                OperationPaths.ValidName(mapping.NewName);
                var source = OperationPaths.Entry(mapping.Source);
                plans.Add(new(source, Path.Combine(Path.GetDirectoryName(source)!, mapping.NewName)));
            }
        }
        else
        {
            if (request.Sources is null || request.Sources.Count == 0) throw new ArgumentException("Select at least one file or folder.");
            if (request.Kind == FileOperationKind.Rename && request.Sources.Count != 1) throw new ArgumentException("Rename requires exactly one source.");
            if (request.NewName is not null)
            {
                OperationPaths.ValidName(request.NewName);
                if (request.Sources.Count != 1) throw new ArgumentException("A new name can only be used with one source.");
            }
            if (request.Kind == FileOperationKind.Rename && request.NewName is null) throw new ArgumentException("A new name is required.");
            var directory = request.Kind is FileOperationKind.Copy or FileOperationKind.Move or FileOperationKind.HardLink
                ? OperationPaths.DirectoryPath(request.DestinationDirectory ?? throw new ArgumentException("Choose a destination folder.")) : null;
            foreach (var value in request.Sources)
            {
                var source = OperationPaths.Entry(value);
                if (request.Kind == FileOperationKind.HardLink &&
                    (File.GetAttributes(source) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
                    throw new IOException($"Hard links require a regular file: {source}");
                var destination = request.Kind == FileOperationKind.Trash ? null :
                    Path.Combine(directory ?? Path.GetDirectoryName(source)!, request.NewName ?? Path.GetFileName(source));
                plans.Add(new(source, destination));
            }
        }
        var sourceSet = new HashSet<string>(OperationPaths.Comparer);
        foreach (var plan in plans)
        {
            if (plan.Source.Length > 0)
            {
                GuardPath(plan.Source);
                if (!OperationPaths.Exists(plan.Source)) throw new FileNotFoundException("The source is unavailable.", plan.Source);
                if (!sourceSet.Add(plan.Source)) throw new ArgumentException($"The source was selected more than once: {plan.Source}");
            }
            if (plan.Destination is not null)
            {
                GuardPath(plan.Destination);
                if (plan.Source.Length > 0)
                {
                    var same = OperationPaths.Comparer.Equals(plan.Source, plan.Destination);
                    if (same && request.Kind == FileOperationKind.Copy && request.Collision != CollisionPolicy.KeepBoth)
                        throw new IOException("A file or folder cannot be copied onto itself. Choose Keep both to duplicate it.");
                    var caseOnlyRename = request.Kind is FileOperationKind.Rename or FileOperationKind.BatchRename &&
                        IsCaseOnlyRename(plan.Source, plan.Destination);
                    if (!same && !caseOnlyRename && (OperationPaths.Contains(plan.Source, plan.Destination) || OperationPaths.Contains(plan.Destination, plan.Source)))
                        throw new IOException("The source and destination cannot contain one another.");
                }
            }
        }
        if (request.Kind is FileOperationKind.Copy or FileOperationKind.Move or FileOperationKind.HardLink &&
            request.Collision is CollisionPolicy.Fail or CollisionPolicy.Replace)
        {
            // Conservatively reject case-folded duplicates on macOS as well: a request
            // must not build an ambiguous replacement chain on a case-insensitive volume.
            var comparer = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
            var destinations = new HashSet<string>(comparer);
            foreach (var plan in plans)
                if (plan.Destination is not null && !destinations.Add(plan.Destination))
                    throw new IOException("Selected items have the same destination name. Choose Keep both or process them separately.");
        }
        foreach (var first in plans.Where(p => p.Source.Length > 0))
            foreach (var second in plans.Where(p => p.Source.Length > 0))
                if (!ReferenceEquals(first, second) && !OperationPaths.Comparer.Equals(first.Source, second.Source) &&
                    OperationPaths.IsRealDirectory(first.Source) && OperationPaths.Contains(first.Source, second.Source))
                    throw new IOException("Select either a folder or its contained items, rather than both in the same operation.");
        return plans;
    }

    private void GuardPath(string path)
    {
        var full = Path.GetFullPath(path);
        if (OperationPaths.Contains(_journal.DirectoryPath, full) || OperationPaths.Contains(full, _journal.DirectoryPath))
            throw new IOException("The operation would affect its own journal or recovery state.");
        if (Path.GetFileName(full).StartsWith(".opusmac-", StringComparison.Ordinal))
            throw new IOException("Private operation recovery folders must be managed through operation recovery.");
    }

    private string? ChooseDestination(JournalOperation operation, JournalMutation item, CollisionPolicy policy)
    {
        var destination = item.Destination!;
        if (!OperationPaths.Exists(destination)) return destination;
        if (policy == CollisionPolicy.Skip)
        {
            item.Phase = "Skipped";
            operation.Changes.Add(new(item.Source, destination, "Skipped: destination exists"));
            Persist();
            return null;
        }
        if (policy == CollisionPolicy.KeepBoth)
        {
            destination = OperationPaths.AvailableName(destination);
            item.Destination = destination;
            Persist();
            return destination;
        }
        if (policy == CollisionPolicy.Fail) throw new IOException($"The destination already exists: {destination}");
        return destination;
    }

    private async Task TransferAsync(JournalOperation operation, JournalMutation item, CollisionPolicy collision, CancellationToken token,
        ReviewedFile? reviewedSource, ReviewedFile? reviewedDestination, string? reviewedParentIdentity)
    {
        if (item.Action == nameof(FileOperationKind.Move) && OperationPaths.Comparer.Equals(item.Source, item.Destination))
        {
            item.Phase = "Skipped";
            operation.Changes.Add(new(item.Source, item.Destination, "Skipped: already in destination"));
            Persist();
            return;
        }
        VerifyReviewedFile(item.Source, reviewedSource);
        VerifyReviewedFile(item.Destination!, reviewedDestination);
        if (ChooseDestination(operation, item, collision) is null) return;
        var parent = OperationPaths.DirectoryPath(Path.GetDirectoryName(item.Destination!)!);
        VerifyReviewedParent(parent, reviewedParentIdentity);
        var hardLink = item.Action == nameof(FileOperationKind.HardLink);
        if (item.Source.Length > 0 && !hardLink)
        {
            if (item.Action == nameof(FileOperationKind.Move) && !OperationPaths.Exists(item.Destination!))
            {
                // An exclusive atomic move never destroys file contents. Its inverse also
                // refuses collisions, so walking metadata is sufficient and avoids reading
                // every byte merely to rename a large media file or directory.
                item.FingerprintIncludesContent = false;
                item.SourceFingerprint = await OperationPaths.FingerprintAsync(item.Source, false, token).ConfigureAwait(false);
                item.Fingerprint = item.SourceFingerprint;
                item.Phase = "MovingDirect";
                Persist();
                OperationPaths.StableParent(item.Destination!, parent);
                OperationPaths.UnchangedLocation(item.Source);
                if (await _platform.TryMoveEntryAsync(item.Source, item.Destination!, token).ConfigureAwait(false))
                {
                    item.Applied = true;
                    item.SourceRemoved = true;
                    item.Phase = "Completed";
                    operation.Changes.Add(new(item.Source, item.Destination, "Move"));
                    Persist();
                    return;
                }
            }
            item.FingerprintIncludesContent = true;
            item.SourceFingerprint = await OperationPaths.FingerprintAsync(item.Source, true, token).ConfigureAwait(false);
        }
        item.Area = OperationPaths.CreateArea(parent, operation.Id, out var tokenValue);
        item.Token = tokenValue;
        item.Stage = Path.Combine(item.Area, "payload");
        item.Phase = "Staging";
        Persist();
        string? createdDirectoryIdentity = null;
        if (item.Action == nameof(FileOperationKind.CreateDirectory))
        {
            Directory.CreateDirectory(item.Stage);
            if (reviewedParentIdentity is not null)
                createdDirectoryIdentity = ListaryClient.ReadNativeItemIdentity(item.Stage) ??
                    throw new IOException("The new update folder identity could not be verified.");
        }
        else if (item.Action == nameof(FileOperationKind.CreateFile))
        {
            using var empty = new FileStream(item.Stage, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            empty.Flush(true);
        }
        else if (hardLink)
        {
            OperationPaths.UnchangedLocation(item.Source);
            var identity = ListaryClient.ReadNativeItemIdentity(item.Source)
                ?? throw new IOException("The hard-link source identity could not be verified.");
            await _platform.CreateHardLinkAsync(item.Source, item.Stage, token).ConfigureAwait(false);
            if (ListaryClient.ReadNativeItemIdentity(item.Stage) != identity ||
                ListaryClient.ReadNativeItemIdentity(item.Source) != identity)
                throw new IOException("The source changed while creating the hard link.");
            item.HardLinkIdentity = identity;
        }
        else
        {
            await CopyTreeAsync(item.Source, item.Stage, token).ConfigureAwait(false);
            var sourceNow = await OperationPaths.FingerprintAsync(item.Source, true, token).ConfigureAwait(false);
            if (!string.Equals(sourceNow, item.SourceFingerprint, StringComparison.Ordinal))
                throw new IOException("The source changed during copying; the staged copy was not committed.");
        }
        item.Fingerprint = await OperationPaths.FingerprintAsync(item.Stage, true, token).ConfigureAwait(false);
        if (item.SourceFingerprint is not null && !string.Equals(item.Fingerprint, item.SourceFingerprint, StringComparison.Ordinal))
            throw new IOException("The staged copy does not match the source contents and filesystem metadata; it was not committed.");
        item.Phase = "Staged";
        Persist();
        token.ThrowIfCancellationRequested();
        OperationPaths.VerifyArea(item.Area, item.Token);
        OperationPaths.StableParent(item.Destination!, parent);
        VerifyReviewedParent(parent, reviewedParentIdentity);
        VerifyReviewedFile(item.Source, reviewedSource);
        VerifyReviewedFile(item.Destination!, reviewedDestination);
        if (ChooseDestination(operation, item, collision) is null) { CleanupStage(item); return; }
        await BackupDestinationAsync(item, token, collision == CollisionPolicy.Replace, reviewedDestination).ConfigureAwait(false);
        VerifyReviewedFile(item.Source, reviewedSource);
        VerifyReviewedParent(parent, reviewedParentIdentity);
        item.Phase = "Publishing";
        Persist();
        VerifyHardLinkIdentity(item, item.Stage);
        if (!await _platform.TryMoveEntryAsync(item.Stage, item.Destination!, CancellationToken.None).ConfigureAwait(false))
            throw new IOException("The staging destination changed filesystem before commit.");
        item.Applied = true;
        item.Phase = "Published";
        operation.Changes.Add(new(item.Source, item.Destination,
            item.Action == nameof(FileOperationKind.Move) ? "Copy: move source retained" : item.Action, createdDirectoryIdentity));
        Persist();
        if (item.Action == nameof(FileOperationKind.Move))
            await RetireMoveSourceAsync(operation, item).ConfigureAwait(false);
        item.Phase = "Completed";
        CleanupStage(item);
        Persist();
    }

    private async Task BackupDestinationAsync(JournalMutation item, CancellationToken token, bool allowReplace,
        ReviewedFile? reviewedDestination = null)
    {
        VerifyReviewedFile(item.Destination!, reviewedDestination);
        if (!OperationPaths.Exists(item.Destination!)) return;
        if (!allowReplace) throw new IOException($"A new item appeared at the destination: {item.Destination}");
        OperationPaths.UnchangedLocation(item.Destination!);
        var fingerprint = await OperationPaths.FingerprintAsync(item.Destination!, true, token).ConfigureAwait(false);
        VerifyReviewedFile(item.Destination!, reviewedDestination);
        item.Backup = Path.Combine(item.Area!, ".previous");
        item.BackupFingerprint = fingerprint;
        item.Phase = "SavingBackup";
        Persist();
        OperationPaths.VerifyArea(item.Area!, item.Token!);
        if (!await _platform.TryMoveEntryAsync(item.Destination!, item.Backup, CancellationToken.None).ConfigureAwait(false))
            throw new IOException("The destination cannot be staged on the same filesystem.");
        try { VerifyReviewedFile(item.Backup, reviewedDestination); }
        catch (IOException)
        {
            // The native move won the race against a different destination object.
            // Record what was actually held so the normal rollback can restore it.
            item.BackupFingerprint = await OperationPaths.FingerprintAsync(item.Backup, true, CancellationToken.None).ConfigureAwait(false);
            Persist();
            throw;
        }
        item.Phase = "BackupSaved";
        Persist();
    }

    private async Task RetireMoveSourceAsync(JournalOperation operation, JournalMutation item)
    {
        var parent = OperationPaths.DirectoryPath(Path.GetDirectoryName(item.Source)!);
        item.SourceHoldArea = OperationPaths.CreateArea(parent, operation.Id, out var owner);
        item.SourceHoldToken = owner;
        item.SourceHold = Path.Combine(item.SourceHoldArea, "payload");
        item.Phase = "RetiringSource";
        Persist();
        OperationPaths.UnchangedLocation(item.Source);
        if (!await _platform.TryMoveEntryAsync(item.Source, item.SourceHold, CancellationToken.None).ConfigureAwait(false))
            throw new IOException("The source changed filesystem; the completed copy is retained and the source was not removed.");
        try
        {
            var fingerprint = await OperationPaths.FingerprintAsync(item.SourceHold, true, CancellationToken.None).ConfigureAwait(false);
            if (!string.Equals(fingerprint, item.SourceFingerprint, StringComparison.Ordinal))
                throw new IOException("The source changed before the move completed. Both versions have been retained.");
            OperationPaths.UnchangedLocation(item.Destination!);
            await VerifyFingerprintAsync(item.Destination!, item.Fingerprint, CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            if (!OperationPaths.Exists(item.Source))
                await _platform.TryMoveEntryAsync(item.SourceHold, item.Source, CancellationToken.None).ConfigureAwait(false);
            item.RecoveryRequired = OperationPaths.Exists(item.SourceHold);
            throw;
        }
        item.SourceRemoved = true;
        Persist();
        OperationPaths.RemoveOwned(item.SourceHoldArea, item.SourceHoldToken, item.SourceHold);
        OperationPaths.RemoveEmptyArea(item.SourceHoldArea, item.SourceHoldToken);
        operation.Changes.RemoveAll(c => c.Source == item.Source && c.Destination == item.Destination);
        operation.Changes.Add(new(item.Source, item.Destination, "Move"));
    }

    private async Task CopyTreeAsync(string source, string destination, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        OperationPaths.UnchangedLocation(source);
        OperationPaths.UnchangedLocation(destination);
        var sourceAttributes = File.GetAttributes(source);
        if ((sourceAttributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != FileAttributes.Directory)
        {
            await _platform.CopyLeafAsync(source, destination, token).ConfigureAwait(false);
            return;
        }
        if (OperationPaths.Exists(destination)) throw new IOException($"The staging path already exists: {destination}");
        Directory.CreateDirectory(destination);
        var children = Directory.EnumerateFileSystemEntries(source).OrderBy(Path.GetFileName, StringComparer.Ordinal).ToArray();
        foreach (var child in children)
        {
            token.ThrowIfCancellationRequested();
            if (!OperationPaths.IsRealDirectory(source)) throw new IOException($"The source directory changed into a link: {source}");
            await CopyTreeAsync(child, Path.Combine(destination, Path.GetFileName(child)), token).ConfigureAwait(false);
        }
        await _platform.CopyDirectoryMetadataAsync(source, destination, token).ConfigureAwait(false);
    }

    private async Task TrashAsync(JournalOperation operation, JournalMutation item, CancellationToken token)
    {
        item.SourceFingerprint = await OperationPaths.FingerprintAsync(item.Source, true, token).ConfigureAwait(false);
        item.Phase = "Trashing";
        Persist();
        var result = await _platform.TrashAsync(item.Source, token).ConfigureAwait(false);
        item.Destination = result.TrashedPath;
        item.Applied = true;
        item.SourceRemoved = true;
        item.Phase = "Completed";
        operation.Changes.Add(new(item.Source, result.TrashedPath, "Trash"));
        if (result.TrashedPath is not null)
            item.Fingerprint = await OperationPaths.FingerprintAsync(result.TrashedPath, true, CancellationToken.None).ConfigureAwait(false);
        Persist();
    }

    private async Task RenameAsync(JournalOperation operation, List<Plan> plans, CollisionPolicy collision, CancellationToken token)
    {
        var active = plans.Where(p => !string.Equals(p.Source, p.Destination, StringComparison.Ordinal)).ToList();
        foreach (var unchanged in plans.Except(active)) operation.Changes.Add(new(unchanged.Source, unchanged.Destination, "Skipped: unchanged name"));
        var selected = new HashSet<string>(active.Select(p => p.Source), OperationPaths.Comparer);
        var reserved = new HashSet<string>(OperationPaths.Comparer);
        foreach (var plan in active.ToArray())
        {
            if (OperationPaths.Exists(plan.Destination!) && !TargetsSelectedSource(plan.Destination!, selected))
            {
                if (collision == CollisionPolicy.Skip)
                {
                    active.Remove(plan);
                    operation.Changes.Add(new(plan.Source, plan.Destination, "Skipped: destination exists"));
                    continue;
                }
                if (collision == CollisionPolicy.Fail) throw new IOException($"The destination already exists: {plan.Destination}");
                if (collision == CollisionPolicy.KeepBoth) plan.Destination = OperationPaths.AvailableName(plan.Destination!, reserved);
            }
            if (!reserved.Add(plan.Destination!)) throw new IOException("Two selected items would receive the same destination name.");
        }
        var skippedMore = true;
        while (collision == CollisionPolicy.Skip && skippedMore)
        {
            skippedMore = false;
            selected = new HashSet<string>(active.Select(p => p.Source), OperationPaths.Comparer);
            foreach (var plan in active.ToArray())
                if (OperationPaths.Exists(plan.Destination!) && !TargetsSelectedSource(plan.Destination!, selected))
                {
                    active.Remove(plan);
                    operation.Changes.Add(new(plan.Source, plan.Destination, "Skipped: destination belongs to a skipped item"));
                    skippedMore = true;
                }
        }
        foreach (var plan in active)
        {
            token.ThrowIfCancellationRequested();
            var item = new JournalMutation { Source = plan.Source, Destination = plan.Destination, Action = "Rename" };
            operation.Mutations.Add(item);
            item.FingerprintIncludesContent = false;
            item.SourceFingerprint = await OperationPaths.FingerprintAsync(item.Source, false, token).ConfigureAwait(false);
            item.Fingerprint = item.SourceFingerprint;
            item.Area = OperationPaths.CreateArea(Path.GetDirectoryName(item.Source)!, operation.Id, out var owner);
            item.Token = owner;
            item.Stage = Path.Combine(item.Area, "payload");
            item.Phase = "StagingRename";
            Persist();
            OperationPaths.UnchangedLocation(item.Source);
            if (!await _platform.TryMoveEntryAsync(item.Source, item.Stage, token).ConfigureAwait(false))
                throw new IOException("A rename staging location unexpectedly crosses filesystems.");
            item.SourceRemoved = true;
            item.Phase = "StagedRename";
            Persist();
        }
        foreach (var item in operation.Mutations)
        {
            token.ThrowIfCancellationRequested();
            if (OperationPaths.Exists(item.Destination!))
            {
                if (collision != CollisionPolicy.Replace) throw new IOException($"A new item appeared at the rename destination: {item.Destination}");
                await BackupDestinationAsync(item, token, true).ConfigureAwait(false);
            }
            item.Phase = "PublishingRename";
            Persist();
            if (!await _platform.TryMoveEntryAsync(item.Stage!, item.Destination!, CancellationToken.None).ConfigureAwait(false))
                throw new IOException("A rename destination unexpectedly crosses filesystems.");
            item.Applied = true;
            item.Fingerprint = item.SourceFingerprint;
            item.Phase = "Completed";
            operation.Changes.Add(new(item.Source, item.Destination, "Rename"));
            Persist();
        }
        foreach (var item in operation.Mutations) CleanupStage(item);
    }

    private async Task RollbackRenamesAsync(JournalOperation operation)
    {
        foreach (var item in operation.Mutations.Where(m => m.Applied).Reverse())
        {
            try
            {
                await VerifyFingerprintAsync(item.Destination!, item.Fingerprint, CancellationToken.None, item.FingerprintIncludesContent).ConfigureAwait(false);
                OperationPaths.VerifyArea(item.Area!, item.Token!);
                if (!await _platform.TryMoveEntryAsync(item.Destination!, item.Stage!, CancellationToken.None).ConfigureAwait(false))
                    throw new IOException("The rename cannot be rolled back on its original filesystem.");
                item.Applied = false;
                item.Phase = "StagedRename";
                Persist();
            }
            catch (Exception error) { RecoveryError(operation, item, error.Message); }
        }
        foreach (var item in operation.Mutations)
        {
            try
            {
                if (item.Stage is not null && OperationPaths.Exists(item.Stage))
                {
                    OperationPaths.VerifyArea(item.Area!, item.Token!);
                    await VerifyFingerprintAsync(item.Stage, item.SourceFingerprint, CancellationToken.None, item.FingerprintIncludesContent).ConfigureAwait(false);
                    if (!await _platform.TryMoveEntryAsync(item.Stage, item.Source, CancellationToken.None).ConfigureAwait(false))
                        throw new IOException("The original source location changed filesystem.");
                    item.SourceRemoved = false;
                    item.Phase = "RolledBack";
                    item.RecoveryRequired = false;
                }
                await RestoreBackupAsync(item).ConfigureAwait(false);
                CleanupStage(item);
            }
            catch (Exception error) { RecoveryError(operation, item, error.Message); }
        }
        operation.Changes.RemoveAll(c => c.Action == "Rename" && !operation.Mutations.Any(m => m.Applied && m.Source == c.Source && m.Destination == c.Destination));
        TryPersist(operation);
    }

    private async Task CleanupFailedAsync(JournalOperation operation, JournalMutation item)
    {
        try
        {
            if (!item.Applied)
            {
                if (item.Action == "Move" && !OperationPaths.Exists(item.Source) && item.Destination is not null && OperationPaths.Exists(item.Destination))
                {
                    await VerifyFingerprintAsync(item.Destination, item.Fingerprint, CancellationToken.None, item.FingerprintIncludesContent).ConfigureAwait(false);
                    item.Applied = true;
                    item.SourceRemoved = true;
                    operation.Changes.Add(new(item.Source, item.Destination, "Move"));
                }
                else
                {
                    if (item.Stage is not null && OperationPaths.Exists(item.Stage)) OperationPaths.RemoveOwned(item.Area!, item.Token!, item.Stage);
                    await RestoreBackupAsync(item).ConfigureAwait(false);
                }
            }
            if (item.SourceHold is not null && OperationPaths.Exists(item.SourceHold) && !item.SourceRemoved)
            {
                OperationPaths.VerifyArea(item.SourceHoldArea!, item.SourceHoldToken!);
                if (!await _platform.TryMoveEntryAsync(item.SourceHold, item.Source, CancellationToken.None).ConfigureAwait(false))
                    throw new IOException("The source recovery location changed filesystem.");
            }
            CleanupStage(item);
            OperationPaths.RemoveEmptyArea(item.SourceHoldArea, item.SourceHoldToken);
            if (!item.Applied && !item.SourceRemoved && !item.RecoveryRequired) item.Phase = "RolledBack";
        }
        catch (Exception error) { RecoveryError(operation, item, error.Message); }
        TryPersist(operation);
    }

    private async Task RestoreBackupAsync(JournalMutation item, List<OperationChange>? changes = null)
    {
        if (item.Backup is null) return;
        var backup = item.Backup;
        OperationPaths.UnchangedLocation(item.Destination!);
        if (!OperationPaths.Exists(backup))
        {
            // The backup move or its inverse may have committed immediately before
            // interruption. Only the exact recorded original may close that intent.
            await VerifyFingerprintAsync(item.Destination!, item.BackupFingerprint, CancellationToken.None).ConfigureAwait(false);
            item.Backup = null;
            changes?.Add(new(backup, item.Destination, "Original destination already restored"));
            Persist();
            return;
        }
        OperationPaths.VerifyArea(item.Area!, item.Token!);
        await VerifyFingerprintAsync(backup, item.BackupFingerprint, CancellationToken.None).ConfigureAwait(false);
        if (!await _platform.TryMoveEntryAsync(backup, item.Destination!, CancellationToken.None).ConfigureAwait(false))
            throw new IOException("The original destination could not be restored on its filesystem.");
        item.Backup = null;
        changes?.Add(new(backup, item.Destination, "Restore original destination"));
        Persist();
    }

    private async Task RecoverUncommittedReplacementAsync(JournalMutation item, List<OperationChange> changes, CancellationToken token)
    {
        item.Phase = "RecoveringReplacement";
        item.RecoveryRequired = true;
        Persist();
        await RestoreBackupAsync(item, changes).ConfigureAwait(false);
        if (item.Stage is not null && OperationPaths.Exists(item.Stage))
        {
            OperationPaths.VerifyArea(item.Area!, item.Token!);
            await VerifyFingerprintAsync(item.Stage, item.Fingerprint, token, item.FingerprintIncludesContent).ConfigureAwait(false);
            if (item.Source.Length > 0 && !OperationPaths.Exists(item.Source))
                throw new IOException($"The original source is unavailable. The verified staged copy was retained: {item.Stage}");
        }
        CleanupStage(item);
        item.Undone = true;
        item.RecoveryRequired = false;
        item.Phase = "Undone";
        Persist();
    }

    private void CleanupStage(JournalMutation item)
    {
        if (item.Stage is not null && OperationPaths.Exists(item.Stage) && item.Action != "Rename")
            OperationPaths.RemoveOwned(item.Area!, item.Token!, item.Stage);
        OperationPaths.RemoveEmptyArea(item.Area, item.Token);
    }

    private async Task<FileOperationResult> UndoCoreAsync(CancellationToken callerToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(callerToken, _shutdown.Token);
        var token = linked.Token;
        var id = Guid.NewGuid().ToString("N");
        try { await _gate.WaitAsync(token).ConfigureAwait(false); }
        catch (OperationCanceledException) { return new(id, false, true, [], []); }
        JournalOperation? operation = null;
        var changes = new List<OperationChange>();
        var errors = new List<string>();
        var canceled = false;
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            if (_journalFault is not null) throw new IOException(_journalFault);
            operation = _operations.LastOrDefault(CanUndoOperation);
            if (operation is null) return new(id, false, false, [], ["There is no safely reversible operation."]);
            await ReconcileAsync(operation, token, changes, errors).ConfigureAwait(false);
            foreach (var completed in operation.Mutations.Where(m => m.Undone && m.Backup is not null))
            {
                await RestoreBackupAsync(completed, changes).ConfigureAwait(false);
                completed.RecoveryRequired = false;
                OperationPaths.RemoveEmptyArea(completed.Area, completed.Token);
            }
            if (operation.Kind is FileOperationKind.Rename or FileOperationKind.BatchRename &&
                operation.Mutations.Any(m => !m.Applied && m.SourceRemoved && !m.Undone))
            {
                var interrupted = operation.Mutations.Where(m => !m.Undone && (m.Applied || m.SourceRemoved)).ToArray();
                await RollbackRenamesAsync(operation).ConfigureAwait(false);
                foreach (var recovered in interrupted.Where(m => !m.Applied && !m.SourceRemoved && !m.RecoveryRequired))
                {
                    recovered.Undone = true;
                    changes.Add(new(recovered.Stage ?? recovered.Destination ?? recovered.Source, recovered.Source, "Recovered interrupted rename"));
                }
                if (operation.Mutations.Any(m => m.RecoveryRequired)) throw new IOException("Some interrupted rename items need recovery from the paths in history.");
            }
            var items = operation.Mutations.Where(m => m.Applied && !m.Undone).ToList();
            var heldDestinations = new HashSet<string>(items.Where(m => m.Destination is not null).Select(m => m.Destination!), OperationPaths.Comparer);
            foreach (var item in items)
            {
                token.ThrowIfCancellationRequested();
                if (item.Destination is null || item.Fingerprint is null)
                    throw new IOException("This platform did not return a reversible Trash location.");
                GuardPath(item.Source.Length > 0 ? item.Source : item.Destination);
                OperationPaths.UnchangedLocation(item.Destination);
                VerifyHardLinkIdentity(item, item.Destination);
                await VerifyFingerprintAsync(item.Destination, item.Fingerprint, token, item.FingerprintIncludesContent).ConfigureAwait(false);
                if (item.Backup is not null)
                {
                    OperationPaths.VerifyArea(item.Area!, item.Token!);
                    await VerifyFingerprintAsync(item.Backup, item.BackupFingerprint, token).ConfigureAwait(false);
                }
                if (item.SourceRemoved)
                {
                    OperationPaths.UnchangedLocation(item.Source);
                    if (OperationPaths.Exists(item.Source) && !heldDestinations.Contains(item.Source))
                        throw new IOException($"Undo would overwrite a new item at the original source: {item.Source}");
                }
            }
            operation.Status = "UndoRunning";
            Persist();
            // Holding all current names first permits cycle-safe undo of batch renames.
            foreach (var item in items.Where(m => m.Action != "Trash"))
            {
                token.ThrowIfCancellationRequested();
                item.UndoArea = !item.SourceRemoved && _platform is DesktopFilePlatform
                    ? OperationPaths.CreateUndoArea(item.Destination!, operation.Id, out var owner)
                    : OperationPaths.CreateArea(Path.GetDirectoryName(item.Destination!)!, operation.Id, out owner);
                item.UndoToken = owner;
                item.UndoHold = Path.Combine(item.UndoArea, "payload");
                item.Phase = "HoldingUndo";
                Persist();
                OperationPaths.UnchangedLocation(item.Destination!);
                if (!await _platform.TryMoveEntryAsync(item.Destination!, item.UndoHold, token).ConfigureAwait(false))
                    throw new IOException("The undo staging folder changed filesystem.");
                item.Phase = "UndoHeld";
                Persist();
            }
            foreach (var item in items)
            {
                token.ThrowIfCancellationRequested();
                if (item.Action == "Trash")
                {
                    item.Phase = "RestoringTrash";
                    Persist();
                    try
                    {
                        await _platform.RestoreTrashAsync(item.Destination!, item.Source, token).ConfigureAwait(false);
                        changes.Add(new(item.Destination!, item.Source, "Restore from Trash"));
                    }
                    catch (TrashRestoreMetadataException error) when (error.RestoreCommitted)
                    {
                        // The payload has moved. Never replay that committed restore.
                        changes.Add(new(error.TrashedPath, error.RestoredPath, "Restored from Trash; recycle metadata cleanup required"));
                        errors.Add(error.Message + " Restored item: " + error.RestoredPath);
                        operation.Errors.Add(errors[^1]);
                    }
                }
                else if (item.SourceRemoved)
                {
                    await MoveUndoToSourceAsync(operation, item, token, changes).ConfigureAwait(false);
                    changes.RemoveAll(c => c.Source == item.Destination && c.Destination == item.Source && c.Action == "Undo copy committed; verification pending");
                    changes.Add(new(item.Destination!, item.Source, "Undo " + item.Action));
                }
                else
                {
                    OperationPaths.VerifyArea(item.UndoArea!, item.UndoToken!);
                    item.Phase = "UndoTrashing";
                    Persist();
                    var trashed = await _platform.TrashAsync(item.UndoHold!, token).ConfigureAwait(false);
                    item.UndoTrashPath = trashed.TrashedPath;
                    changes.Add(new(item.Destination!, trashed.TrashedPath, "Undo: moved created item to Trash"));
                }
                item.Undone = true;
                item.RecoveryRequired = false;
                item.Phase = "Undone";
                Persist();
                await RestoreBackupAsync(item, changes).ConfigureAwait(false);
                OperationPaths.RemoveEmptyArea(item.UndoArea, item.UndoToken);
                OperationPaths.RemoveEmptyArea(item.Area, item.Token);
            }
            operation.Status = errors.Count == 0 ? "Undone" : "UndoPartial";
            Persist();
        }
        catch (Exception error)
        {
            canceled = error is OperationCanceledException;
            if (!canceled) errors.Add(error.Message);
            if (operation is not null)
            {
                foreach (var item in operation.Mutations.Where(m => !m.Undone && m.UndoHold is not null))
                {
                    try
                    {
                        if (OperationPaths.Exists(item.UndoHold!))
                        {
                            OperationPaths.VerifyArea(item.UndoArea!, item.UndoToken!);
                            if (!await _platform.TryMoveEntryAsync(item.UndoHold!, item.Destination!, CancellationToken.None).ConfigureAwait(false))
                                throw new IOException("Unable to return an undo-held item to its filesystem.");
                            item.Phase = "Completed";
                        }
                        OperationPaths.RemoveEmptyArea(item.UndoArea, item.UndoToken);
                    }
                    catch (Exception rollbackError)
                    {
                        RecoveryError(operation, item, rollbackError.Message);
                        errors.Add($"Undo recovery retained at {item.UndoHold}: {rollbackError.Message}");
                    }
                }
                operation.Status = operation.Mutations.Any(m => m.Undone) ? "UndoPartial" : "UndoBlocked";
                operation.Errors.AddRange(errors);
                TryPersist(operation);
            }
        }
        finally { PublishHistory(); _gate.Release(); }
        return new(id, !canceled && errors.Count == 0, canceled, changes, errors);
    }

    private async Task MoveUndoToSourceAsync(JournalOperation operation, JournalMutation item, CancellationToken token, List<OperationChange> changes)
    {
        OperationPaths.VerifyArea(item.UndoArea!, item.UndoToken!);
        if (await _platform.TryMoveEntryAsync(item.UndoHold!, item.Source, token).ConfigureAwait(false)) return;
        item.SourceHoldArea = OperationPaths.CreateArea(Path.GetDirectoryName(item.Source)!, operation.Id, out var owner);
        item.SourceHoldToken = owner;
        item.SourceHold = Path.Combine(item.SourceHoldArea, "payload");
        item.Phase = "StagingUndoCopy";
        Persist();
        await CopyTreeAsync(item.UndoHold!, item.SourceHold, token).ConfigureAwait(false);
        await VerifyFingerprintAsync(item.UndoHold!, item.Fingerprint, token, item.FingerprintIncludesContent).ConfigureAwait(false);
        var heldContents = await OperationPaths.FingerprintAsync(item.UndoHold!, true, token).ConfigureAwait(false);
        var copiedContents = await OperationPaths.FingerprintAsync(item.SourceHold, true, token).ConfigureAwait(false);
        if (!string.Equals(heldContents, copiedContents, StringComparison.Ordinal))
            throw new IOException("The copy made while undoing does not match its source; undo was not committed.");
        token.ThrowIfCancellationRequested();
        if (!await _platform.TryMoveEntryAsync(item.SourceHold, item.Source, CancellationToken.None).ConfigureAwait(false))
            throw new IOException("The original source filesystem changed before undo commit.");
        changes.Add(new(item.Destination!, item.Source, "Undo copy committed; verification pending"));
        OperationPaths.UnchangedLocation(item.Source);
        await VerifyFingerprintAsync(item.Source, copiedContents, CancellationToken.None).ConfigureAwait(false);
        OperationPaths.RemoveOwned(item.UndoArea!, item.UndoToken!, item.UndoHold!);
        OperationPaths.RemoveEmptyArea(item.SourceHoldArea, item.SourceHoldToken);
    }

    private async Task ReconcileAsync(JournalOperation operation, CancellationToken token, List<OperationChange> changes, List<string> errors)
    {
        foreach (var item in operation.Mutations.Where(m => !m.Undone))
        {
            if (item.Phase == "RestoringTrash" && item.Destination is not null &&
                !OperationPaths.Exists(item.Destination) && OperationPaths.Exists(item.Source))
            {
                OperationPaths.UnchangedLocation(item.Source);
                await VerifyFingerprintAsync(item.Source, item.Fingerprint, token, item.FingerprintIncludesContent).ConfigureAwait(false);
                item.Undone = true;
                item.RecoveryRequired = false;
                item.Phase = "Undone";
                changes.Add(new(item.Destination, item.Source, "Recovered committed Trash restore"));
                const string warning = "An interrupted Trash restore already committed. Its payload was preserved; inspect the system Trash for any stale metadata record.";
                errors.Add(warning);
                operation.Errors.Add(warning);
                continue;
            }
            if (item.Phase == "UndoTrashing" && item.UndoHold is not null && !OperationPaths.Exists(item.UndoHold))
            {
                // Trash may have committed before its native result path was
                // durably returned to the journal. Never invoke Trash again.
                item.Undone = true;
                item.RecoveryRequired = false;
                item.Phase = "Undone";
                item.UndoTrashPath = null;
                OperationPaths.RemoveEmptyArea(item.UndoArea, item.UndoToken);
                const string warning = "The interrupted Undo payload was no longer in its recorded hold. It was not sent to Trash again; its exact system Trash location could not be confirmed.";
                operation.Errors.Add(warning);
                changes.Add(new(item.Destination ?? item.Source, null, "Undo removal already committed or payload missing; no replay"));
                continue;
            }
            if (item.Phase is "HoldingUndo" or "UndoHeld" or "StagingUndoCopy" or "UndoTrashing")
            {
                if (item.SourceRemoved && OperationPaths.Exists(item.Source) && !OperationPaths.Exists(item.Destination!))
                {
                    OperationPaths.UnchangedLocation(item.Source);
                    await VerifyFingerprintAsync(item.Source, item.Fingerprint, token, item.FingerprintIncludesContent).ConfigureAwait(false);
                    if (item.UndoHold is not null && OperationPaths.Exists(item.UndoHold))
                    {
                        OperationPaths.VerifyArea(item.UndoArea!, item.UndoToken!);
                        var held = await OperationPaths.FingerprintAsync(item.UndoHold, true, token).ConfigureAwait(false);
                        var restored = await OperationPaths.FingerprintAsync(item.Source, true, token).ConfigureAwait(false);
                        if (!string.Equals(held, restored, StringComparison.Ordinal))
                            throw new IOException("Interrupted undo left two different versions; both were retained.");
                        OperationPaths.RemoveOwned(item.UndoArea!, item.UndoToken!, item.UndoHold);
                    }
                    item.Undone = true;
                    item.RecoveryRequired = false;
                    item.Phase = "Undone";
                    OperationPaths.RemoveEmptyArea(item.UndoArea, item.UndoToken);
                    changes.Add(new(item.Destination!, item.Source, "Recovered committed undo"));
                    continue;
                }
                if (item.UndoHold is not null && OperationPaths.Exists(item.UndoHold))
                {
                    OperationPaths.VerifyArea(item.UndoArea!, item.UndoToken!);
                    await VerifyFingerprintAsync(item.UndoHold, item.Fingerprint, token, item.FingerprintIncludesContent).ConfigureAwait(false);
                    if (item.Phase == "StagingUndoCopy" && item.SourceHold is not null && OperationPaths.Exists(item.SourceHold))
                    {
                        OperationPaths.RemoveOwned(item.SourceHoldArea!, item.SourceHoldToken!, item.SourceHold);
                        OperationPaths.RemoveEmptyArea(item.SourceHoldArea, item.SourceHoldToken);
                    }
                    OperationPaths.UnchangedLocation(item.Destination!);
                    if (!await _platform.TryMoveEntryAsync(item.UndoHold, item.Destination!, token).ConfigureAwait(false))
                        throw new IOException("Interrupted undo could not restore its held destination on the same filesystem.");
                    item.Phase = "Completed";
                    item.RecoveryRequired = false;
                    OperationPaths.RemoveEmptyArea(item.UndoArea, item.UndoToken);
                }
            }
            if (item.Phase is "StagingRename" or "StagedRename" && item.Stage is not null && OperationPaths.Exists(item.Stage))
            {
                OperationPaths.VerifyArea(item.Area!, item.Token!);
                await VerifyFingerprintAsync(item.Stage, item.SourceFingerprint, token, item.FingerprintIncludesContent).ConfigureAwait(false);
                item.SourceRemoved = true;
                item.RecoveryRequired = false;
            }
            if (!item.Applied && item.Destination is not null && item.Fingerprint is not null &&
                item.Phase is "MovingDirect" or "Publishing" or "PublishingRename" &&
                OperationPaths.Exists(item.Destination) && (item.Stage is null || !OperationPaths.Exists(item.Stage)))
            {
                OperationPaths.UnchangedLocation(item.Destination);
                VerifyHardLinkIdentity(item, item.Destination);
                await VerifyFingerprintAsync(item.Destination, item.Fingerprint, token, item.FingerprintIncludesContent).ConfigureAwait(false);
                item.Applied = true;
                item.SourceRemoved = item.Action == "Rename" || item.Phase == "MovingDirect" ||
                    (item.Action == "Move" && !OperationPaths.Exists(item.Source));
                item.RecoveryRequired = false;
            }
            if (!item.Applied && item.Action != "Rename" &&
                (item.Backup is not null || item.Phase == "RecoveringReplacement"))
            {
                await RecoverUncommittedReplacementAsync(item, changes, token).ConfigureAwait(false);
                continue;
            }
            if (item.SourceHold is not null && OperationPaths.Exists(item.SourceHold) && !OperationPaths.Exists(item.Source))
            {
                OperationPaths.VerifyArea(item.SourceHoldArea!, item.SourceHoldToken!);
                await VerifyFingerprintAsync(item.SourceHold, item.SourceFingerprint, token).ConfigureAwait(false);
                if (!await _platform.TryMoveEntryAsync(item.SourceHold, item.Source, token).ConfigureAwait(false))
                    throw new IOException("The held original source could not be recovered on its filesystem.");
                item.SourceRemoved = false;
                OperationPaths.RemoveEmptyArea(item.SourceHoldArea, item.SourceHoldToken);
                changes.Add(new(item.SourceHold, item.Source, "Recovered original move source"));
            }
            if (item.Phase == "Trashing" && item.Destination is null && !OperationPaths.Exists(item.Source))
                throw new IOException("The interrupted native Trash operation has no recorded result path. Locate the item in the system Trash before retrying.");
        }
        Persist();
    }

    private static void VerifyReviewedParent(string path, string? identity)
    {
        if (identity is not null && ListaryClient.ReadNativeItemIdentity(path) != identity)
            throw new IOException($"The reviewed destination folder changed before the update: {path}");
    }

    private static void VerifyReviewedFile(string path, ReviewedFile? review)
    {
        if (review is null) return;
        var current = new FileInfo(path);
        if (!current.Exists || (current.Attributes & FileAttributes.ReparsePoint) != 0 ||
            ListaryClient.ReadNativeItemIdentity(path) != review.NativeIdentity || current.Length != review.Length ||
            current.LastWriteTimeUtc != review.ModifiedUtc.UtcDateTime)
            throw new IOException($"The reviewed file changed before the update: {path}");
    }

    private static void VerifyHardLinkIdentity(JournalMutation item, string path)
    {
        if (item.Action == nameof(FileOperationKind.HardLink) &&
            (item.HardLinkIdentity is null || ListaryClient.ReadNativeItemIdentity(path) != item.HardLinkIdentity))
            throw new IOException($"The hard-link item changed at {path}.");
    }

    private static async Task VerifyFingerprintAsync(string path, string? fingerprint, CancellationToken token, bool content = true)
    {
        if (fingerprint is null || !OperationPaths.Exists(path))
            throw new IOException($"The recorded item is unavailable; undo did not change anything: {path}");
        var actual = await OperationPaths.FingerprintAsync(path, content, token).ConfigureAwait(false);
        if (!string.Equals(actual, fingerprint, StringComparison.Ordinal))
            throw new IOException($"The item changed after the operation; undo will not alter it: {path}");
    }

    private void ResumeConfirmedDiscards()
    {
        if (!_operations.Any(operation => operation.DiscardRequested)) return;
        var inspection = BuildStorageInspection();
        var cleaned = new HashSet<JournalOperation>(ReferenceEqualityComparer.Instance);
        var changed = false;
        long ignoredBytes = 0;
        var ignoredFiles = 0;
        foreach (var record in inspection.Records.Where(record => record.Operation.DiscardRequested))
        {
            string? error = null;
            if (!IsStableForDiscard(record.Operation) || HasPendingRecovery(record.Operation))
                error = "The persisted Undo discard is no longer in a stable terminal state; its data was retained.";
            else if (record.ValidationErrors.Count > 0)
                error = string.Join(" | ", record.ValidationErrors);
            else if (TryCleanupConfirmedDiscard(record, ref ignoredBytes, ref ignoredFiles, out var cleanupError))
                cleaned.Add(record.Operation);
            else
                error = cleanupError;

            if (error is not null) changed |= AddDiscardError(record.Operation, error);
        }

        if (cleaned.Count == 0 && !changed) return;
        var retained = _operations.Where(operation => !cleaned.Contains(operation)).ToList();
        try
        {
            _journal.Save(retained);
            _operations.Clear();
            _operations.AddRange(retained);
        }
        catch (Exception error)
        {
            if (error is not OperationHistoryCapacityException)
                _journalFault = "The operation journal could not be saved. Further changes are blocked until the state directory is writable: " + error.Message;
        }
    }

    private StorageInspection BuildStorageInspection()
    {
        var areaReferenceCounts = new Dictionary<string, int>(OperationPaths.Comparer);
        foreach (var operation in _operations)
        {
            var seen = new HashSet<string>(OperationPaths.Comparer);
            foreach (var mutation in operation.Mutations)
            {
                foreach (var area in new[] { mutation.Area, mutation.SourceHoldArea, mutation.UndoArea })
                {
                    if (string.IsNullOrWhiteSpace(area)) continue;
                    try
                    {
                        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(area));
                        if (!seen.Add(full)) continue;
                        areaReferenceCounts[full] = areaReferenceCounts.TryGetValue(full, out var count) ? count + 1 : 1;
                    }
                    catch { /* The record-level inspection reports malformed paths. */ }
                }
            }
        }

        var idCounts = _operations.GroupBy(operation => operation.Id, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
        var records = new List<StorageRecordInspection>(_operations.Count);
        foreach (var operation in _operations.AsEnumerable().Reverse())
            records.Add(InspectStorageRecord(operation, areaReferenceCounts, idCounts));

        var publicRecords = records.Select(record => record.Review).ToArray();
        var validationErrors = records.SelectMany(record => record.ValidationErrors.Select(error => $"{record.Operation.Id}: {error}"))
            .Distinct(StringComparer.Ordinal).ToArray();
        var review = new OperationStorageReview(
            publicRecords,
            publicRecords.Count(record => record.CanUndo),
            publicRecords.Count(record => record.RecoveryProtected),
            publicRecords.Count(record => record.CanDiscard),
            publicRecords.Sum(record => record.ReclaimableBytes),
            publicRecords.Sum(record => record.ReclaimableFiles),
            publicRecords.SelectMany(record => record.Errors.Select(error => $"{record.OperationId}: {error}"))
                .Distinct(StringComparer.Ordinal).ToArray());
        return new(review, records, validationErrors);
    }

    private StorageRecordInspection InspectStorageRecord(JournalOperation operation,
        IReadOnlyDictionary<string, int> areaReferenceCounts, IReadOnlyDictionary<string, int> idCounts)
    {
        var validationErrors = new List<string>();
        if (!idCounts.TryGetValue(operation.Id, out var idCount) || idCount != 1)
            validationErrors.Add("The journal contains a duplicate operation id; discard is blocked for this record.");

        var candidates = new Dictionary<string, MutableStorageArea>(OperationPaths.Comparer);
        foreach (var item in operation.Mutations)
        {
            AddStorageArea(candidates, validationErrors, item.Area, item.Token,
                item.Backup is not null ? "Replacement backup" : "Operation staging", item.Stage, item.Backup);
            AddStorageArea(candidates, validationErrors, item.SourceHoldArea, item.SourceHoldToken,
                "Move source hold", item.SourceHold);
            AddStorageArea(candidates, validationErrors, item.UndoArea, item.UndoToken,
                "Undo hold", item.UndoHold);
        }

        var areas = new List<StorageAreaInspection>();
        foreach (var candidate in candidates.Values)
        {
            if (candidate.Token is null) continue;
            if (areaReferenceCounts.TryGetValue(candidate.Path, out var references) && references > 1)
            {
                validationErrors.Add($"Private area is referenced by multiple operation records and was retained: {candidate.Path}");
                continue;
            }
            try
            {
                if (!OperationPaths.Exists(candidate.Path))
                {
                    foreach (var payload in candidate.Payloads)
                        if (OperationPaths.Exists(payload))
                            validationErrors.Add($"Recorded private payload exists without its owning area: {payload}");
                    continue;
                }
                OperationPaths.VerifyArea(candidate.Path, candidate.Token);
                foreach (var payload in candidate.Payloads)
                    if (OperationPaths.Exists(payload)) OperationPaths.VerifyOwnedEntry(candidate.Path, payload);
                var usage = OperationPaths.MeasureOwnedArea(candidate.Path, candidate.Token);
                areas.Add(new(candidate.Path, candidate.Token,
                    string.Join(", ", candidate.Purposes.OrderBy(value => value, StringComparer.Ordinal)), usage.Bytes, usage.Files));
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                validationErrors.Add(error.Message);
            }
        }

        var canUndo = CanUndoOperation(operation);
        var recoveryProtected = HasPendingRecovery(operation);
        var canDiscard = !operation.DiscardRequested && HasUndoState(operation) &&
            IsStableForDiscard(operation) && !recoveryProtected && validationErrors.Count == 0;
        var ownedBytes = areas.Sum(area => area.Bytes);
        var ownedFiles = areas.Sum(area => area.Files);
        var areaReview = areas.Select(area => new OperationStorageAreaReview(
            area.Path, area.Purpose, area.Bytes, area.Files, canDiscard)).ToArray();
        var publicErrors = operation.Errors.Concat(validationErrors).Distinct(StringComparer.Ordinal).ToArray();
        var review = new OperationStorageRecordReview(
            operation.Id, operation.StartedAt, operation.Kind, operation.Status,
            canUndo, recoveryProtected, OperationJournal.IsRetired(operation), operation.DiscardRequested, canDiscard,
            ownedBytes, ownedFiles, canDiscard ? ownedBytes : 0, canDiscard ? ownedFiles : 0, areaReview, publicErrors);
        return new(operation, review, areas, validationErrors.ToArray());
    }

    private static void AddStorageArea(Dictionary<string, MutableStorageArea> areas, List<string> errors,
        string? area, string? token, string purpose, params string?[] payloads)
    {
        var recordedPayloads = payloads.Where(path => !string.IsNullOrWhiteSpace(path)).Cast<string>().ToArray();
        if (string.IsNullOrWhiteSpace(area))
        {
            if (!string.IsNullOrWhiteSpace(token) || recordedPayloads.Length > 0)
                errors.Add($"Recorded {purpose.ToLowerInvariant()} data is missing its private area path.");
            return;
        }

        string full;
        try { full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(area)); }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException)
        {
            errors.Add($"Recorded private area path is invalid: {error.Message}");
            return;
        }
        if (string.IsNullOrWhiteSpace(token))
        {
            errors.Add($"Recorded private area has no ownership token and was retained: {full}");
            return;
        }

        if (!areas.TryGetValue(full, out var candidate))
        {
            candidate = new(full, token);
            areas.Add(full, candidate);
        }
        else if (!string.Equals(candidate.Token, token, StringComparison.Ordinal))
        {
            errors.Add($"Recorded private area has conflicting ownership tokens and was retained: {full}");
            return;
        }
        candidate.Purposes.Add(purpose);
        foreach (var payload in recordedPayloads) candidate.Payloads.Add(payload);
    }

    private static bool TryCleanupConfirmedDiscard(StorageRecordInspection record,
        ref long reclaimedBytes, ref int reclaimedFiles, out string? error)
    {
        error = null;
        var measured = new List<(StorageAreaInspection Area, long Bytes, int Files)>();
        try
        {
            // Preflight every area for this operation before deleting the first one.
            foreach (var area in record.Areas)
            {
                if (!OperationPaths.Exists(area.Path)) continue;
                var usage = OperationPaths.MeasureOwnedArea(area.Path, area.Token);
                measured.Add((area, usage.Bytes, usage.Files));
            }
            foreach (var item in measured)
            {
                OperationPaths.RemoveOwnedArea(item.Area.Path, item.Area.Token);
                reclaimedBytes = checked(reclaimedBytes + item.Bytes);
                reclaimedFiles = checked(reclaimedFiles + item.Files);
            }
            return true;
        }
        catch (Exception cleanupError) when (cleanupError is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or OverflowException)
        {
            error = cleanupError.Message;
            return false;
        }
    }

    private static bool AddDiscardError(JournalOperation operation, string error)
    {
        var message = "Undo discard cleanup pending: " + error;
        if (operation.Errors.Contains(message, StringComparer.Ordinal)) return false;
        operation.Errors.Add(message);
        return true;
    }

    private static bool HasPendingRecovery(JournalOperation operation) =>
        operation.Status is "Running" or "UndoRunning" or "Interrupted" or "UndoPartial" or "UndoBlocked" ||
        operation.Mutations.Any(item => item.RecoveryRequired);

    private static bool IsStableForDiscard(JournalOperation operation) =>
        operation.Status is "Succeeded" or "Partial" or "Failed" or "Canceled" &&
        operation.Mutations.All(item => !item.RecoveryRequired && item.Phase is "Completed" or "Skipped" or "RolledBack" or "Undone");

    private static bool HasUndoState(JournalOperation operation) =>
        operation.Status != "Undone" && operation.Mutations.Any(m =>
            (m.Undone && m.Backup is not null) || (!m.Undone &&
            ((m.Applied && m.Destination is not null && m.Fingerprint is not null) ||
             (m.Backup is not null && m.BackupFingerprint is not null) || m.Phase == "RecoveringReplacement" ||
             (m.SourceRemoved && m.Stage is not null && m.SourceFingerprint is not null) ||
             (m.Phase is "MovingDirect" or "Publishing" or "PublishingRename" or "StagingRename" or "StagedRename" && m.Fingerprint is not null))));

    private static bool CanUndoOperation(JournalOperation operation) =>
        !operation.DiscardRequested && HasUndoState(operation);

    private static bool IsCaseOnlyRename(string source, string destination) =>
        OperationPaths.Comparer.Equals(Path.GetDirectoryName(source), Path.GetDirectoryName(destination)) &&
        string.Equals(Path.GetFileName(source), Path.GetFileName(destination), StringComparison.OrdinalIgnoreCase) &&
        !string.Equals(Path.GetFileName(source), Path.GetFileName(destination), StringComparison.Ordinal);

    private static bool TargetsSelectedSource(string destination, HashSet<string> selected)
    {
        if (selected.Contains(destination)) return true;
        var parent = Path.GetDirectoryName(destination)!;
        var desired = Path.GetFileName(destination);
        // On case-sensitive volumes an exact separate entry is a real collision.
        // On case-insensitive volumes the directory enumeration reveals the original
        // spelling of the selected entry that this destination currently aliases.
        var entries = Directory.EnumerateFileSystemEntries(parent)
            .Where(p => string.Equals(Path.GetFileName(p), desired, StringComparison.OrdinalIgnoreCase)).ToArray();
        var exact = entries.FirstOrDefault(p => string.Equals(Path.GetFileName(p), desired, StringComparison.Ordinal));
        if (exact is not null) return selected.Contains(exact);
        return entries.Length == 1 && selected.Contains(entries[0]);
    }

    private void Persist()
    {
        try { _journal.Save(_operations); }
        catch (OperationHistoryCapacityException) { throw; }
        catch (Exception error)
        {
            _journalFault = $"The operation journal could not be saved. Further changes are blocked until the state directory is writable: {error.Message}";
            throw new IOException(_journalFault, error);
        }
    }

    private void TryPersist(JournalOperation operation)
    {
        try { Persist(); }
        catch (Exception error) { if (!operation.Errors.Contains(error.Message)) operation.Errors.Add(error.Message); }
    }

    private void PublishHistory()
    {
        var snapshot = _operations.Select(operation => new OperationHistoryItem(operation.Id, operation.StartedAt, operation.Kind,
            $"{operation.Kind}: {operation.Changes.Count(c => !c.Action.StartsWith("Skipped", StringComparison.Ordinal))} changed" +
            (operation.Errors.Count > 0 ? " — " + operation.Errors[^1] : ""),
            CanUndoOperation(operation), operation.Status)).Reverse().ToArray();
        lock (_historyGate) { _history = Array.AsReadOnly(snapshot); _canUndo = snapshot.Any(i => i.CanUndo); }
        if (HistoryChanged is { } changed)
            foreach (var subscriber in changed.GetInvocationList().Cast<Action>()) { try { subscriber(); } catch { /* A view cannot abort a file operation. */ } }
    }

    private static void Report(IProgress<FileOperationProgress>? progress, FileOperationProgress value)
    {
        try { progress?.Report(value); } catch { /* Observer failures do not change filesystem results. */ }
    }

    private static string Display(JournalMutation item) => item.Source.Length > 0 ? item.Source : item.Destination ?? item.Action;

    private static void RecoveryError(JournalOperation operation, JournalMutation item, string message)
    {
        item.RecoveryRequired = true;
        var locations = new[] { item.Stage, item.Backup, item.SourceHold, item.UndoHold }.Where(p => p is not null && OperationPaths.Exists(p)).ToArray();
        operation.Errors.Add($"{message} Recovery data retained: {string.Join("; ", locations)}");
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _shutdown.Cancel();
        _gate.Wait();
        try { _journal.Dispose(); }
        finally { _gate.Release(); }
    }

    private sealed record StorageInspection(
        OperationStorageReview Review,
        IReadOnlyList<StorageRecordInspection> Records,
        IReadOnlyList<string> ValidationErrors);

    private sealed record StorageRecordInspection(
        JournalOperation Operation,
        OperationStorageRecordReview Review,
        IReadOnlyList<StorageAreaInspection> Areas,
        IReadOnlyList<string> ValidationErrors);

    private sealed record StorageAreaInspection(string Path, string Token, string Purpose, long Bytes, int Files);

    private sealed class MutableStorageArea(string path, string token)
    {
        internal string Path { get; } = path;
        internal string Token { get; } = token;
        internal HashSet<string> Purposes { get; } = new(StringComparer.Ordinal);
        internal HashSet<string> Payloads { get; } = new(OperationPaths.Comparer);
    }

    private sealed class Plan(string source, string? destination)
    {
        internal string Source { get; } = source;
        internal string? Destination { get; set; } = destination;
    }
}
