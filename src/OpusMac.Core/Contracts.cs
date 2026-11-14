namespace OpusMac.Core;

public sealed record FileEntry(
    string FullPath, string Name, bool IsDirectory, bool IsSymbolicLink,
    long Length, DateTimeOffset ModifiedUtc, DateTimeOffset CreatedUtc,
    FileAttributes Attributes, bool IsPackage = false)
{
    public bool IsBrowsable => IsDirectory && !IsPackage;
    public string Extension => IsDirectory ? string.Empty : Path.GetExtension(Name);
    public string Kind => IsSymbolicLink ? "Link" : IsPackage ? "Application package" : IsDirectory ? "Folder" : Extension.Length > 1 ? Extension[1..].ToUpperInvariant() + " file" : "File";
    public long? CalculatedFolderLength { get; init; }
    public string SizeText => IsDirectory && CalculatedFolderLength is null
        ? string.Empty : FormatSize(CalculatedFolderLength ?? Length);
    public string ModifiedText => ModifiedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
    public static string FormatSize(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var value = (double)Math.Max(0, bytes);
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1) { value /= 1024; unit++; }
        return unit == 0 ? $"{bytes:N0} B" : $"{value:0.##} {units[unit]}";
    }
}

public sealed record BrowseOptions(bool ShowHidden = false, bool Recursive = false, string Filter = "", int MaxEntries = 200000);
public sealed record DirectoryBatch(IReadOnlyList<FileEntry> Entries, int Discovered);
public sealed record DirectoryListing(
    string Directory,
    IReadOnlyList<FileEntry> Entries,
    IReadOnlyList<string> Errors,
    bool Truncated,
    string? RootError = null);

public enum FileOperationKind { Copy, Move, Trash, Rename, BatchRename, CreateDirectory, CreateFile, HardLink }
public enum CollisionPolicy { Fail, Skip, KeepBoth, Replace }
public sealed record RenameMapping(string Source, string NewName);
public sealed record ReviewedFile(string NativeIdentity, long Length, DateTimeOffset ModifiedUtc);
public sealed record FileOperationRequest(
    FileOperationKind Kind, IReadOnlyList<string> Sources,
    string? DestinationDirectory = null, string? NewName = null,
    CollisionPolicy Collision = CollisionPolicy.Fail,
    IReadOnlyList<RenameMapping>? Renames = null,
    ReviewedFile? ReviewedSource = null, ReviewedFile? ReviewedDestination = null,
    string? ReviewedDestinationParentIdentity = null);
public sealed record FileOperationProgress(string Id, string Description, int CompletedItems, int TotalItems, long CompletedBytes = 0, long TotalBytes = 0);
public sealed record OperationChange(string Source, string? Destination, string Action, string? NativeIdentity = null);
public sealed record FileOperationResult(string Id, bool Succeeded, bool Canceled, IReadOnlyList<OperationChange> Changes, IReadOnlyList<string> Errors);
public sealed record OperationHistoryItem(string Id, DateTimeOffset StartedAt, FileOperationKind Kind, string Summary, bool CanUndo, string Status);
public sealed record TrashResult(string? TrashedPath);

public interface IFilePlatform
{
    Task CreateHardLinkAsync(string source, string destination, CancellationToken cancellationToken) =>
        throw new PlatformNotSupportedException("This file platform cannot create hard links.");
    // Copy one regular file or symbolic link into a non-existing path, retaining native metadata.
    Task CopyLeafAsync(string source, string destination, CancellationToken cancellationToken);
    Task CopyDirectoryMetadataAsync(string source, string destination, CancellationToken cancellationToken);
    // Exclusive atomic rename only; false means a cross-volume move with no mutation.
    Task<bool> TryMoveEntryAsync(string source, string destination, CancellationToken cancellationToken);
    Task<TrashResult> TrashAsync(string path, CancellationToken cancellationToken);
    Task RestoreTrashAsync(string trashedPath, string destination, CancellationToken cancellationToken);
    Task OpenAsync(string path, CancellationToken cancellationToken = default);
    Task RevealAsync(string path, CancellationToken cancellationToken = default);
    Task QuickLookAsync(IReadOnlyList<string> paths, CancellationToken cancellationToken = default);
    Task OpenTerminalAsync(string directory, CancellationToken cancellationToken = default);
}
