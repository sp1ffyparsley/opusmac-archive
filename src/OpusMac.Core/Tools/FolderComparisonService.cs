using OpusMac.Core.Search;

namespace OpusMac.Core.Tools;

/// <summary>Recursively compares folders and builds explicit, non-deleting operation requests from the preview.</summary>
public sealed class FolderComparisonService
{
    public Task<FolderComparisonResult> CompareAsync(string leftDirectory, string rightDirectory, FolderComparisonOptions? options = null, IProgress<ToolProgress>? progress = null, CancellationToken cancellationToken = default,
        IReadOnlyCollection<string>? selectedNames = null) => Task.Run(async () =>
    {
        options ??= new FolderComparisonOptions();
        if (options.TimestampTolerance is { } tolerance && tolerance < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(options));
        cancellationToken.ThrowIfCancellationRequested();
        var leftRoot = ValidateRoot(leftDirectory);
        var rightRoot = ValidateRoot(rightDirectory);
        var leftRootIdentity = ListaryClient.ReadNativeItemIdentity(leftRoot);
        var rightRootIdentity = ListaryClient.ReadNativeItemIdentity(rightRoot);
        if (ToolPath.IsWithin(leftRoot, rightRoot) || ToolPath.IsWithin(rightRoot, leftRoot)) throw new IOException("Choose separate folders; a folder cannot be compared against itself or a descendant.");
        progress?.Report(new ToolProgress("Scanning folders", 0, 0));
        var leftPaths = selectedNames?.Select(name => SelectedChild(leftRoot, name)).ToArray() ?? [leftRoot];
        var rightPaths = selectedNames?.Select(name => SelectedChild(rightRoot, name)).Where(ToolPath.Exists).ToArray() ?? [rightRoot];
        var leftScan = ToolWalker.Scan(leftPaths, true, options.IncludeHidden, options.MaxEntries, options.MaxDepth, cancellationToken);
        var rightScan = ToolWalker.Scan(rightPaths, true, options.IncludeHidden, Math.Max(1, options.MaxEntries - leftScan.Entries.Count), options.MaxDepth, cancellationToken);
        var errors = leftScan.Errors.Concat(rightScan.Errors).ToList();
        var left = Index(leftScan.Entries, leftRoot, errors);
        var right = Index(rightScan.Entries, rightRoot, errors);
        var paths = left.Keys.Union(right.Keys, ToolPath.PortableComparer).Order(ToolPath.PortableComparer).ToArray();
        var result = new List<ComparisonItem>(paths.Length);
        var completed = 0;
        long hashedBytes = 0;
        var lastProgress = Environment.TickCount64;
        foreach (var key in paths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            left.TryGetValue(key, out var leftItem);
            right.TryGetValue(key, out var rightItem);
            var relative = leftItem is not null ? Relative(leftRoot, leftItem.Path) : Relative(rightRoot, rightItem!.Path);
            ComparisonStatus status;
            string? detail = null;
            if (leftItem?.IsLink == true || rightItem?.IsLink == true)
            {
                status = ComparisonStatus.SkippedLink;
                detail = "Symbolic links and reparse points are not followed or included in copy plans.";
            }
            else if (leftItem is null) status = ComparisonStatus.RightOnly;
            else if (rightItem is null) status = ComparisonStatus.LeftOnly;
            else if (leftItem.IsDirectory != rightItem.IsDirectory)
            {
                status = ComparisonStatus.TypeConflict;
                detail = "One side is a directory and the other is a file. Resolve this explicitly before copying.";
            }
            else if (leftItem.IsDirectory) status = ComparisonStatus.Equal;
            else if (leftItem.Length != rightItem.Length) status = ComparisonStatus.Different;
            else if (!options.CompareContents)
            {
                status = (leftItem.ModifiedUtc - rightItem.ModifiedUtc).Duration() <= (options.TimestampTolerance ?? TimeSpan.FromSeconds(2)) ? ComparisonStatus.Equal : ComparisonStatus.Different;
                detail = "Metadata comparison: file content was not read.";
            }
            else
            {
                try
                {
                    void OnBytes(int count)
                    {
                        hashedBytes += count;
                        if (Environment.TickCount64 - lastProgress >= 100)
                        {
                            progress?.Report(new ToolProgress("Comparing " + relative, completed, paths.Length, hashedBytes));
                            lastProgress = Environment.TickCount64;
                        }
                    }
                    var leftHash = await ToolWalker.HashAsync(leftItem, OnBytes, cancellationToken).ConfigureAwait(false);
                    var rightHash = await ToolWalker.HashAsync(rightItem, OnBytes, cancellationToken).ConfigureAwait(false);
                    status = string.Equals(leftHash, rightHash, StringComparison.Ordinal) ? ComparisonStatus.Equal : ComparisonStatus.Different;
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    status = ComparisonStatus.Unreadable;
                    detail = error.Message;
                    errors.Add($"{relative}: {error.Message}");
                }
            }
            result.Add(new ComparisonItem(relative, status, leftItem?.Path, rightItem?.Path, (leftItem ?? rightItem)!.IsDirectory, leftItem?.Length, rightItem?.Length, leftItem?.ModifiedUtc, rightItem?.ModifiedUtc, detail)
            {
                LeftIdentity = leftItem?.NativeIdentity,
                RightIdentity = rightItem?.NativeIdentity
            });
            completed++;
            progress?.Report(new ToolProgress("Compared " + completed + " paths", completed, paths.Length, hashedBytes));
        }
        foreach (var item in leftScan.Entries.Concat(rightScan.Entries).Where(item => !item.IsLink))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (!item.StillMatches()) errors.Add($"Folder contents changed during comparison; compare again: {item.Path}");
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { errors.Add($"{item.Path}: {error.Message}"); }
        }
        return new FolderComparisonResult(leftRoot, rightRoot, result, errors, leftScan.Truncated || rightScan.Truncated || leftScan.Entries.Count + rightScan.Entries.Count > options.MaxEntries, options.CompareContents)
        { LeftRootIdentity = leftRootIdentity, RightRootIdentity = rightRootIdentity };
    }, cancellationToken);

    /// <summary>Missing items are copied by default. Existing changed files require an explicit includeChanged choice. No requests delete destination-only content.</summary>
    public FolderCopyPlan BuildCopyPlan(FolderComparisonResult comparison, ComparisonDirection direction, bool includeChanged = false,
        CopyUpdateMode? update = null)
    {
        ArgumentNullException.ThrowIfNull(comparison);
        if (!Enum.IsDefined(direction)) throw new ArgumentOutOfRangeException(nameof(direction));
        if (comparison.Truncated || comparison.Errors.Count > 0 || comparison.Items.Any(item => item.Status == ComparisonStatus.Unreadable)) throw new InvalidOperationException("A complete comparison without read errors is required before creating a copy plan.");
        var leftToRight = direction == ComparisonDirection.LeftToRight;
        var sourceRoot = ValidateRoot(leftToRight ? comparison.LeftDirectory : comparison.RightDirectory);
        var destinationRoot = ValidateRoot(leftToRight ? comparison.RightDirectory : comparison.LeftDirectory);
        if (ToolPath.IsWithin(sourceRoot, destinationRoot) || ToolPath.IsWithin(destinationRoot, sourceRoot)) throw new InvalidOperationException("The source and destination must be separate folders.");
        var requests = new List<FileOperationRequest>();
        var warnings = new List<string>();
        var blocked = comparison.Items.Where(item => item.Status is ComparisonStatus.TypeConflict or ComparisonStatus.SkippedLink).Select(item => item.RelativePath).ToArray();
        foreach (var item in comparison.Items.Where(item => item.Status is ComparisonStatus.TypeConflict or ComparisonStatus.SkippedLink)) warnings.Add($"Skipped {item.RelativePath}: {item.Detail ?? item.Status.ToString()}");
        if (!comparison.ComparedContents) warnings.Add("This preview used size and timestamp metadata. Equal metadata does not guarantee equal file content.");
        var destinationDirectories = new Dictionary<string, string>(ToolPath.PortableComparer) { [string.Empty] = destinationRoot };
        var destinationIdentities = new Dictionary<string, string>(ToolPath.PathComparer);
        void RememberDestination(string path, string? identity)
        {
            if (update is null) return;
            if (identity is null || ListaryClient.ReadNativeItemIdentity(path) != identity)
                throw new IOException($"The destination folder changed after comparison. Compare again: {path}");
            destinationIdentities.Add(path, identity);
        }
        RememberDestination(destinationRoot, leftToRight ? comparison.RightRootIdentity : comparison.LeftRootIdentity);
        foreach (var item in comparison.Items.Where(item => item.IsDirectory && item.Status is not (ComparisonStatus.TypeConflict or ComparisonStatus.SkippedLink)))
        {
            var target = leftToRight ? item.RightPath : item.LeftPath;
            if (target is null) continue;
            ValidatePreviewPath(target, destinationRoot, item.RelativePath);
            ToolPath.RequireNoLinks(target);
            if (!Directory.Exists(target)) throw new InvalidOperationException($"Destination changed; compare again: {target}");
            RememberDestination(target, leftToRight ? item.RightIdentity : item.LeftIdentity);
            destinationDirectories[ToolPath.Key(item.RelativePath)] = target;
        }
        var sourceOnly = leftToRight ? ComparisonStatus.LeftOnly : ComparisonStatus.RightOnly;
        var selected = comparison.Items.Where(item =>
            (item.Status == sourceOnly && update != CopyUpdateMode.Existing) ||
            (item.Status == ComparisonStatus.Different && (update is null ? includeChanged :
                leftToRight ? item.LeftModifiedUtc > item.RightModifiedUtc : item.RightModifiedUtc > item.LeftModifiedUtc)))
            .Where(item => !blocked.Any(prefix => SameOrChild(item.RelativePath, prefix))).ToArray();
        foreach (var item in selected.Where(item => item.IsDirectory).OrderBy(item => item.RelativePath.Count(character => character == '/')).ThenBy(item => item.RelativePath, ToolPath.PortableComparer))
        {
            var source = leftToRight ? item.LeftPath : item.RightPath;
            if (source is null) throw new InvalidOperationException("The comparison contains a missing source path.");
            ValidateSource(item, source, sourceRoot, leftToRight);
            var (parentRelative, name) = SplitRelative(item.RelativePath);
            if (!destinationDirectories.TryGetValue(ToolPath.Key(parentRelative), out var parent)) throw new InvalidOperationException($"No safe destination parent exists for {item.RelativePath}.");
            var destination = Path.Combine(parent, name);
            if (ToolPath.Exists(destination)) throw new InvalidOperationException($"Destination changed; compare again: {destination}");
            requests.Add(new FileOperationRequest(FileOperationKind.CreateDirectory, [], parent, name,
                ReviewedDestinationParentIdentity: destinationIdentities.GetValueOrDefault(parent)));
            destinationDirectories[ToolPath.Key(item.RelativePath)] = destination;
        }
        foreach (var item in selected.Where(item => !item.IsDirectory).OrderBy(item => item.RelativePath, ToolPath.PortableComparer))
        {
            var source = leftToRight ? item.LeftPath : item.RightPath;
            if (source is null) throw new InvalidOperationException("The comparison contains a missing source path.");
            var currentSource = ValidateSource(item, source, sourceRoot, leftToRight);
            ToolFileSnapshot? currentTarget = null;
            var (parentRelative, _) = SplitRelative(item.RelativePath);
            if (!destinationDirectories.TryGetValue(ToolPath.Key(parentRelative), out var parent)) throw new InvalidOperationException($"No safe destination parent exists for {item.RelativePath}.");
            if (item.Status == ComparisonStatus.Different)
            {
                var previousTarget = leftToRight ? item.RightPath : item.LeftPath;
                if (previousTarget is null) throw new InvalidOperationException("The changed file has no destination in the preview.");
                ValidatePreviewPath(previousTarget, destinationRoot, item.RelativePath);
                ToolPath.RequireNoLinks(previousTarget);
                // Copy uses the source basename. A case/normalization-only destination name mismatch requires a separate rename decision.
                if (!string.Equals(Path.GetFileName(previousTarget), Path.GetFileName(source), StringComparison.Ordinal))
                {
                    warnings.Add($"Skipped {item.RelativePath}: source and destination use different filename casing or Unicode spelling. Resolve the name before replacing it.");
                    continue;
                }
                var current = ToolFileSnapshot.Read(previousTarget);
                var length = leftToRight ? item.RightLength : item.LeftLength;
                var modified = leftToRight ? item.RightModifiedUtc : item.LeftModifiedUtc;
                if (current.IsDirectory || current.IsLink || current.Length != length || current.ModifiedUtc != modified) throw new InvalidOperationException($"Destination changed; compare again: {previousTarget}");
                currentTarget = current;
            }
            requests.Add(new FileOperationRequest(FileOperationKind.Copy, [source], parent,
                Collision: item.Status == ComparisonStatus.Different ? CollisionPolicy.Replace : CollisionPolicy.Fail,
                ReviewedSource: update is null ? null : Review(currentSource, leftToRight ? item.LeftIdentity : item.RightIdentity),
                ReviewedDestination: update is not null && currentTarget is not null
                    ? Review(currentTarget, leftToRight ? item.RightIdentity : item.LeftIdentity) : null,
                ReviewedDestinationParentIdentity: destinationIdentities.GetValueOrDefault(parent)));
        }
        if (update is null && !includeChanged && comparison.Items.Any(item => item.Status == ComparisonStatus.Different)) warnings.Add("Changed existing files are excluded. Enable replacement explicitly to include them.");
        return new FolderCopyPlan(requests, warnings, requests.Any(request => request.Collision == CollisionPolicy.Replace));
    }

    private static ReviewedFile Review(ToolFileSnapshot file, string? identity)
    {
        if (identity is null || file.NativeIdentity != identity)
            throw new IOException($"The file changed after comparison. Compare again: {file.Path}");
        return new ReviewedFile(identity, file.Length, file.ModifiedUtc);
    }

    private static string SelectedChild(string root, string name)
    {
        if (ToolPath.NameError(name) is { } error) throw new IOException(error);
        return Path.Combine(root, name);
    }

    private static string ValidateRoot(string directory)
    {
        var root = ToolPath.Full(directory);
        ToolPath.RequireNoLinks(root);
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException($"Folder does not exist: {root}");
        return root;
    }

    private static Dictionary<string, ToolFileSnapshot> Index(IReadOnlyList<ToolFileSnapshot> entries, string root, List<string> errors)
    {
        var index = new Dictionary<string, ToolFileSnapshot>(ToolPath.PortableComparer);
        foreach (var item in entries)
        {
            if (ToolPath.PathComparer.Equals(item.Path, root)) continue;
            var relative = Relative(root, item.Path);
            if (!index.TryAdd(ToolPath.Key(relative), item)) errors.Add($"Case or Unicode-colliding paths prevent a portable comparison: {item.Path}");
        }
        return index;
    }

    private static string Relative(string root, string path) => Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/');
    private static bool SameOrChild(string path, string prefix) => ToolPath.PortableComparer.Equals(ToolPath.Key(path), ToolPath.Key(prefix)) || ToolPath.Key(path).StartsWith(ToolPath.Key(prefix) + "/", StringComparison.OrdinalIgnoreCase);
    private static (string Parent, string Name) SplitRelative(string relative)
    {
        var slash = relative.LastIndexOf('/');
        return slash < 0 ? (string.Empty, relative) : (relative[..slash], relative[(slash + 1)..]);
    }

    private static void ValidatePreviewPath(string path, string root, string relative)
    {
        var full = ToolPath.Full(path);
        if (!ToolPath.IsWithin(full, root) || ToolPath.PathComparer.Equals(full, root) || !ToolPath.PortableComparer.Equals(ToolPath.Key(Relative(root, full)), ToolPath.Key(relative))) throw new InvalidOperationException("The comparison contains a path outside its selected folder.");
    }

    private static ToolFileSnapshot ValidateSource(ComparisonItem item, string source, string root, bool leftToRight)
    {
        ValidatePreviewPath(source, root, item.RelativePath);
        ToolPath.RequireNoLinks(source);
        var current = ToolFileSnapshot.Read(source);
        var length = leftToRight ? item.LeftLength : item.RightLength;
        var modified = leftToRight ? item.LeftModifiedUtc : item.RightModifiedUtc;
        if (current.IsLink || current.IsDirectory != item.IsDirectory || (!current.IsDirectory && (current.Length != length || current.ModifiedUtc != modified))) throw new InvalidOperationException($"Source changed; compare again: {source}");
        return current;
    }
}
