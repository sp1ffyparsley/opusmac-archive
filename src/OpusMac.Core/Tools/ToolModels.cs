namespace OpusMac.Core.Tools;

public sealed record ToolProgress(string Description, int CompletedItems, int TotalItems, long CompletedBytes = 0, long TotalBytes = 0);

public sealed record ArchiveLimits(int MaxEntries = 100000, long MaxEntryBytes = 34359738368, long MaxTotalBytes = 137438953472, double MaxExpansionRatio = 1000, int MaxDepth = 256);
public sealed record ArchiveEntryInfo(string Name, bool IsDirectory, long Length, long CompressedLength, DateTimeOffset ModifiedUtc);
public sealed record ArchiveListing(string ArchivePath, IReadOnlyList<ArchiveEntryInfo> Entries, long TotalUncompressedBytes);
public sealed record ArchiveResult(string Destination, int EntryCount, long Bytes);

public enum RenameCase { Unchanged, Lower, Upper, Title }
public sealed record RenameOptions(string Find = "", string Replace = "", string Prefix = "", string Suffix = "", bool UseRegex = false, bool IgnoreCase = false, bool IncludeExtension = false, RenameCase Case = RenameCase.Unchanged, int? NumberStart = null, int NumberStep = 1, int NumberDigits = 0);
public sealed record RenamePlanItem(string Source, string OriginalName, string NewName, string Destination, bool Changed, IReadOnlyList<string> Errors);
public sealed record RenamePlan(IReadOnlyList<RenamePlanItem> Items, IReadOnlyList<string> Errors)
{
    public bool IsValid => Errors.Count == 0 && Items.All(item => item.Errors.Count == 0);
    public bool CanExecute => IsValid && Items.Any(item => item.Changed);
    public FileOperationRequest ToRequest()
    {
        if (!CanExecute) throw new InvalidOperationException(IsValid ? "The rename preview contains no changes." : "Correct the rename preview errors before applying it.");
        var mappings = Items.Where(item => item.Changed).Select(item => new RenameMapping(item.Source, item.NewName)).ToArray();
        return new FileOperationRequest(FileOperationKind.BatchRename, mappings.Select(item => item.Source).ToArray(), Renames: mappings);
    }
}

public enum DuplicateComparison { Content, Filename, FilenameStem, FilenameAndSize, Size, Md5Checksum }
public sealed record DuplicateOptions(bool Recursive = true, bool IncludeHidden = false, long MinimumSize = 1, int MaxEntries = 200000, int MaxDepth = 256,
    DuplicateComparison Comparison = DuplicateComparison.Content, IReadOnlyList<string>? ReferenceFiles = null,
    bool IgnoreOffline = true, bool IgnoreHardLinks = false, int Md5Accuracy = 100, string? FilenameFilter = null,
    IReadOnlyList<string>? ExcludedLocations = null);
public sealed record DuplicateGroup(string Hash, long Length, IReadOnlyList<string> Paths)
{
    internal IReadOnlyList<ToolFileSnapshot> ReviewedFiles { get; init; } = [];
    public long SizeAt(int index) => ReviewedFiles.Count == Paths.Count ? ReviewedFiles[index].Length : Length;
    public long ReclaimableBytes => checked(Length * Math.Max(0, Paths.Count - 1));
}
public sealed record DuplicateResult(IReadOnlyList<DuplicateGroup> Groups, IReadOnlyList<string> Errors, IReadOnlyList<string> SkippedPaths, bool Truncated, int ScannedFiles, long HashedBytes,
    DuplicateComparison Comparison = DuplicateComparison.Content)
{
    public IReadOnlyList<string> OutsideReferences { get; init; } = [];
    internal bool IgnoreOffline { get; init; } = true;
    public int Md5Accuracy { get; init; } = 100;
}

public enum ComparisonStatus { Equal, LeftOnly, RightOnly, Different, TypeConflict, SkippedLink, Unreadable }
public enum ComparisonDirection { LeftToRight, RightToLeft }
public enum CopyUpdateMode { All, Existing }
public sealed record FolderComparisonOptions(bool CompareContents = true, bool IncludeHidden = false, int MaxEntries = 200000, int MaxDepth = 256, TimeSpan? TimestampTolerance = null);
public sealed record ComparisonItem(string RelativePath, ComparisonStatus Status, string? LeftPath, string? RightPath, bool IsDirectory, long? LeftLength, long? RightLength, DateTimeOffset? LeftModifiedUtc, DateTimeOffset? RightModifiedUtc, string? Detail = null)
{
    internal string? LeftIdentity { get; init; }
    internal string? RightIdentity { get; init; }
}
public sealed record FolderComparisonResult(string LeftDirectory, string RightDirectory, IReadOnlyList<ComparisonItem> Items, IReadOnlyList<string> Errors, bool Truncated, bool ComparedContents)
{
    internal string? LeftRootIdentity { get; init; }
    internal string? RightRootIdentity { get; init; }
}
public sealed record FolderCopyPlan(IReadOnlyList<FileOperationRequest> Requests, IReadOnlyList<string> Warnings, bool IncludesReplacement);
