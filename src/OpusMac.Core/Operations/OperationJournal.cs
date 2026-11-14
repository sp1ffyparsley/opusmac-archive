using System.Runtime.CompilerServices;
using System.Text.Json;

[assembly: InternalsVisibleTo("OpusMac.Checks")]

namespace OpusMac.Core.Operations;

internal sealed class OperationJournal : IDisposable
{
    private readonly string _directory;
    private readonly string _path;
    private readonly FileStream _ownerLock;
    private readonly OperationJournalLimits _limits;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    internal OperationJournal(string directory, OperationJournalLimits? limits = null)
    {
        _limits = limits ?? OperationJournalLimits.Default;
        if (_limits.MaximumBytes < 1024 || _limits.NewWorkBytes < 1024 ||
            _limits.NewWorkBytes >= _limits.MaximumBytes || _limits.RetiredHistoryItems < 0)
            throw new ArgumentOutOfRangeException(nameof(limits));
        Directory.CreateDirectory(Path.GetFullPath(directory));
        _directory = OperationPaths.DirectoryPath(directory);
        _path = Path.Combine(_directory, "operations.json");
        _ownerLock = new FileStream(Path.Combine(_directory, "operations.lock"), FileMode.OpenOrCreate,
            FileAccess.ReadWrite, FileShare.None);
    }

    internal string DirectoryPath => _directory;

    internal List<JournalOperation> Load()
    {
        if (!File.Exists(_path)) return [];
        if ((File.GetAttributes(_path) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("The operation journal cannot be a symbolic link.");
        var size = new FileInfo(_path).Length;
        if (size > _limits.MaximumBytes) throw new IOException("The operation journal is too large to open safely. Keep this state directory for recovery; it has not been reset.");
        var document = JsonSerializer.Deserialize<JournalDocument>(File.ReadAllText(_path), JsonOptions)
            ?? throw new IOException("The operation journal is empty or invalid.");
        if (document.Version != 1) throw new IOException("This operation journal version is not supported.");
        return document.Operations;
    }

    internal void EnsureCanBegin(List<JournalOperation> operations, long reservedBytes)
    {
        if (reservedBytes >= _limits.NewWorkBytes)
            throw new OperationHistoryCapacityException("This selection is too large for one safely journaled operation. Process a smaller selection.");
        // Leave room beyond the admission budget for completion, diagnostics and undo.
        _ = SerializeFitting(operations.ToList(), checked(_limits.NewWorkBytes - (int)reservedBytes), materialize: false);
    }

    internal void Save(List<JournalOperation> operations)
    {
        if (!OperationPaths.Comparer.Equals(OperationPaths.DirectoryPath(_directory), _directory))
            throw new IOException("The operation journal directory changed.");
        // The same hard limit applies to both readers and writers. Check before
        // opening a temporary file so an oversized state never replaces the journal.
        var retained = operations.ToList();
        var bytes = SerializeFitting(retained, _limits.MaximumBytes, materialize: true);
        var temporary = Path.Combine(_directory, $".operations-{Guid.NewGuid():N}.tmp");
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                file.Write(bytes);
                file.Flush(true);
            }
            File.Move(temporary, _path, true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
        operations.Clear();
        operations.AddRange(retained);
    }

    private byte[] SerializeFitting(List<JournalOperation> operations, int maximumBytes, bool materialize)
    {
        var retired = operations.Where(IsPrunableRetired).ToArray();
        var excess = Math.Max(0, retired.Length - _limits.RetiredHistoryItems);
        if (excess > 0)
        {
            var expired = retired.Take(excess).ToHashSet();
            operations.RemoveAll(expired.Contains);
        }
        try { return SerializeWithin(operations, maximumBytes, materialize); }
        catch (JournalWriteLimitException)
        {
            // Under byte pressure, only retired metadata with no remaining private
            // storage may be dropped. Live undo, recovery data and reclaimable
            // private areas retain their journal record.
            if (operations.RemoveAll(IsPrunableRetired) == 0) throw CapacityError();
            try { return SerializeWithin(operations, maximumBytes, materialize); }
            catch (JournalWriteLimitException) { throw CapacityError(); }
        }
    }

    private static byte[] SerializeWithin(List<JournalOperation> operations, int maximumBytes, bool materialize)
    {
        using var output = new BoundedJournalStream(maximumBytes, materialize);
        JsonSerializer.Serialize(output, new JournalDocument { Operations = operations }, JsonOptions);
        return output.ToArray();
    }

    internal static bool IsRetired(JournalOperation operation) =>
        (operation.Status is "Succeeded" or "Failed" or "Canceled" or "Undone") &&
        operation.Mutations.All(item => !item.RecoveryRequired && item.Backup is null &&
            (item.Undone || (!item.Applied && !item.SourceRemoved &&
            (item.Phase is "Skipped" or "RolledBack" ||
             (item.Phase == "Planned" && item.Stage is null && item.SourceHold is null && item.UndoHold is null)))));

    private static bool IsPrunableRetired(JournalOperation operation) =>
        IsRetired(operation) && !operation.Mutations.Any(HasRetainedPrivateStorage);

    private static bool HasRetainedPrivateStorage(JournalMutation item) =>
        new[] { item.Area, item.Stage, item.Backup, item.SourceHoldArea, item.SourceHold, item.UndoArea, item.UndoHold }
            .Any(path => path is not null && OperationPaths.Exists(path));

    private static OperationHistoryCapacityException CapacityError() => new(
        "Operation history has reached its safety limit. Use Undo for operations you are ready to reverse, then retry. Retained undo backups and recovery records have not been deleted.");

    private sealed class JournalWriteLimitException : IOException { }

    private sealed class BoundedJournalStream(int maximumBytes, bool materialize) : Stream
    {
        private readonly MemoryStream? _buffer = materialize ? new(Math.Min(maximumBytes, 64 * 1024)) : null;
        private long _written;
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => _written;
        public override long Position { get => _written; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));
        public override void Write(ReadOnlySpan<byte> buffer)
        {
            if (_written + buffer.Length > maximumBytes) throw new JournalWriteLimitException();
            _buffer?.Write(buffer);
            _written += buffer.Length;
        }
        internal byte[] ToArray() => _buffer?.ToArray() ?? [];
        protected override void Dispose(bool disposing) { if (disposing) _buffer?.Dispose(); base.Dispose(disposing); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }

    public void Dispose() => _ownerLock.Dispose();
}

internal sealed record OperationJournalLimits(int MaximumBytes, int NewWorkBytes, int RetiredHistoryItems)
{
    internal static OperationJournalLimits Default { get; } = new(64 * 1024 * 1024, 48 * 1024 * 1024, 200);
}

internal sealed class OperationHistoryCapacityException(string message) : IOException(message);

internal sealed class JournalDocument
{
    public int Version { get; set; } = 1;
    public List<JournalOperation> Operations { get; set; } = [];
}

internal sealed class JournalOperation
{
    public string Id { get; set; } = "";
    public DateTimeOffset StartedAt { get; set; }
    public FileOperationKind Kind { get; set; }
    public string Status { get; set; } = "Running";
    public List<JournalMutation> Mutations { get; set; } = [];
    public List<OperationChange> Changes { get; set; } = [];
    public List<string> Errors { get; set; } = [];
    public bool DiscardRequested { get; set; }
}

internal sealed class JournalMutation
{
    public string Source { get; set; } = "";
    public string? Destination { get; set; }
    public string Action { get; set; } = "";
    public string Phase { get; set; } = "Planned";
    public string? Area { get; set; }
    public string? Token { get; set; }
    public string? Stage { get; set; }
    public string? Backup { get; set; }
    public string? BackupFingerprint { get; set; }
    public string? Fingerprint { get; set; }
    public bool FingerprintIncludesContent { get; set; } = true;
    public string? SourceFingerprint { get; set; }
    public string? HardLinkIdentity { get; set; }
    public string? SourceHoldArea { get; set; }
    public string? SourceHoldToken { get; set; }
    public string? SourceHold { get; set; }
    public string? UndoHold { get; set; }
    public string? UndoArea { get; set; }
    public string? UndoToken { get; set; }
    public string? UndoTrashPath { get; set; }
    public bool Applied { get; set; }
    public bool SourceRemoved { get; set; }
    public bool Undone { get; set; }
    public bool RecoveryRequired { get; set; }
}
