using OpusMac.Core.Browsing;
using System.Security.Cryptography;
using System.IO.Enumeration;
using System.Text.RegularExpressions;

namespace OpusMac.Core.Tools;

/// <summary>Groups regular files by the selected comparison. Symbolic links are reported and never traversed.</summary>
public sealed class DuplicateFinder
{
    public static bool ComparesContents(DuplicateComparison comparison) =>
        comparison is DuplicateComparison.Content or DuplicateComparison.Md5Checksum;

    private static HashAlgorithmName ChecksumAlgorithm(DuplicateComparison comparison) =>
        comparison == DuplicateComparison.Md5Checksum ? HashAlgorithmName.MD5 : HashAlgorithmName.SHA256;

    private static long? ChecksumLimit(long length, int accuracy) =>
        accuracy == 100 ? null : (long)(((Int128)length * accuracy + 99) / 100);

    public Task<DuplicateResult> FindAsync(IReadOnlyList<string> roots, DuplicateOptions? options = null, IProgress<ToolProgress>? progress = null, CancellationToken cancellationToken = default) => Task.Run(async () =>
    {
        ArgumentNullException.ThrowIfNull(roots);
        options ??= new DuplicateOptions();
        if (!Enum.IsDefined(options.Comparison)) throw new ArgumentOutOfRangeException(nameof(options), "Unknown duplicate comparison.");
        if (options.MinimumSize < 0) throw new ArgumentOutOfRangeException(nameof(options), "Minimum file size cannot be negative.");
        if (options.Md5Accuracy is < 1 or > 100)
            throw new ArgumentOutOfRangeException(nameof(options), "MD5 accuracy must be between 1 and 100 percent.");
        cancellationToken.ThrowIfCancellationRequested();
        var exclude = CompileExclusions(options.ExcludedLocations);
        var scan = ToolWalker.Scan(roots, options.Recursive, options.IncludeHidden, options.MaxEntries, options.MaxDepth,
            cancellationToken, exclude);
        var errors = scan.Errors.ToList();
        bool IsOffline(ToolFileSnapshot item) => options.IgnoreOffline && !item.IsDirectory && item.Attributes.HasFlag(FileAttributes.Offline);
        var skipped = scan.Entries.Where(item => item.IsLink || IsOffline(item)).Select(item => item.Path)
            .ToHashSet(ToolPath.PathComparer);
        var filenameMatches = DirectoryBrowser.CompileFilenameFilter(options.FilenameFilter);
        var files = scan.Entries.Where(item => !item.IsDirectory && !skipped.Contains(item.Path) &&
            item.Length >= options.MinimumSize && filenameMatches(Path.GetFileName(item.Path))).ToArray();
        var references = (options.ReferenceFiles ?? []).Select(ToolPath.Full).ToHashSet(ToolPath.PathComparer);
        if (references.Count > 1000) throw new ArgumentException("Choose up to 1,000 reference files.");
        var scannedPaths = files.Select(file => file.Path).ToHashSet(ToolPath.PathComparer);
        var outsideReferences = new List<ToolFileSnapshot>();
        foreach (var path in references)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ToolPath.RequireNoLinks(path);
            var reference = ToolFileSnapshot.Read(path);
            if (reference.IsDirectory || reference.IsLink) throw new IOException($"Choose a regular reference file: {path}");
            if (IsOffline(reference))
            {
                skipped.Add(path);
                continue;
            }
            if (!scannedPaths.Contains(path) && reference.Length >= options.MinimumSize) outsideReferences.Add(reference);
        }
        (long Length, string Key) Classification(ToolFileSnapshot item) => options.Comparison switch
        {
            DuplicateComparison.Filename => (0, ToolPath.Key(Path.GetFileName(item.Path)).ToUpperInvariant()),
            DuplicateComparison.FilenameStem => (0, ToolPath.Key(Path.GetFileNameWithoutExtension(item.Path)).ToUpperInvariant()),
            DuplicateComparison.FilenameAndSize => (item.Length, ToolPath.Key(Path.GetFileName(item.Path)).ToUpperInvariant()),
            _ => (item.Length, "")
        };
        var contentComparison = ComparesContents(options.Comparison);
        var checksumAlgorithm = ChecksumAlgorithm(options.Comparison);
        var accuracy = options.Comparison == DuplicateComparison.Md5Checksum ? options.Md5Accuracy : 100;
        var candidates = files.Concat(outsideReferences).GroupBy(Classification).Where(group => group.Count() > 1 &&
                (references.Count == 0 || group.Any(file => references.Contains(file.Path))))
            .SelectMany(group => group.Select(item => (Item: item, group.Key))).ToArray();
        var groups = new Dictionary<(long Length, string Key), List<ToolFileSnapshot>>();
        long hashedBytes = 0;
        long totalBytes = 0;
        if (contentComparison)
            foreach (var candidate in candidates)
            {
                var bytes = ChecksumLimit(candidate.Item.Length, accuracy) ?? candidate.Item.Length;
                totalBytes = bytes > long.MaxValue - totalBytes ? long.MaxValue : totalBytes + bytes;
            }
        var completed = 0;
        var lastProgress = Environment.TickCount64;
        foreach (var (item, classification) in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var key = classification;
                if (contentComparison)
                {
                    var hash = await ToolWalker.HashAsync(item, count =>
                    {
                        hashedBytes += count;
                        if (Environment.TickCount64 - lastProgress >= 100)
                        {
                            progress?.Report(new ToolProgress("Hashing " + Path.GetFileName(item.Path), completed, candidates.Length, hashedBytes, totalBytes));
                            lastProgress = Environment.TickCount64;
                        }
                    }, cancellationToken, options.IgnoreOffline, checksumAlgorithm, ChecksumLimit(item.Length, accuracy))
                        .ConfigureAwait(false);
                    key = (item.Length, hash);
                }
                if (!groups.TryGetValue(key, out var matches)) groups[key] = matches = [];
                matches.Add(item);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                errors.Add($"{item.Path}: {error.Message}");
            }
            completed++;
            progress?.Report(new ToolProgress("Compared " + completed + " candidate files", completed, candidates.Length, hashedBytes, totalBytes));
        }
        cancellationToken.ThrowIfCancellationRequested();
        foreach (var matches in groups.Values)
        {
            for (var index = matches.Count - 1; index >= 0; index--)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var member = matches[index];
                try
                {
                    ToolPath.RequireNoLinks(member.Path);
                    if (!member.StillMatches(options.IgnoreOffline)) throw new IOException("File changed during duplicate comparison.");
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    errors.Add($"{member.Path}: {error.Message}");
                    matches.RemoveAt(index);
                }
            }
        }
        if (options.IgnoreHardLinks)
        {
            foreach (var key in groups.Keys.ToArray())
            {
                cancellationToken.ThrowIfCancellationRequested();
                var seen = new HashSet<string>(StringComparer.Ordinal);
                var distinct = new List<ToolFileSnapshot>();
                foreach (var member in groups[key].OrderByDescending(file => references.Contains(file.Path))
                             .ThenBy(file => file.Path, ToolPath.PathComparer))
                {
                    if (member.NativeIdentity is { } identity && !seen.Add(identity)) skipped.Add(member.Path);
                    else distinct.Add(member);
                }
                groups[key] = distinct;
            }
        }
        var duplicates = groups.Where(group => group.Value.Count > 1 && (references.Count == 0 ||
            (group.Value.Any(file => references.Contains(file.Path)) && group.Value.Any(file => scannedPaths.Contains(file.Path))))).Select(group =>
        {
            var matches = group.Value.OrderBy(file => file.Path, ToolPath.PathComparer).ToArray();
            return new DuplicateGroup(group.Key.Key, group.Key.Length, matches.Select(file => file.Path).ToArray())
            { ReviewedFiles = matches };
        }).OrderByDescending(group => group.Length).ThenBy(group => group.Hash, StringComparer.Ordinal).ToArray();
        return new DuplicateResult(duplicates, errors, skipped.ToArray(), scan.Truncated, files.Length, hashedBytes, options.Comparison)
        {
            OutsideReferences = outsideReferences.Select(file => file.Path).ToArray(),
            IgnoreOffline = options.IgnoreOffline,
            Md5Accuracy = accuracy
        };
    }, cancellationToken);

    private static Func<string, bool>? CompileExclusions(IReadOnlyList<string>? exclusions)
    {
        if (exclusions is null || exclusions.Count == 0) return null;
        var rules = new List<Func<string, bool>>();
        foreach (var entry in exclusions)
        {
            var text = entry.Trim();
            if (text.Length == 0) continue;
            if (text.StartsWith("regex:", StringComparison.OrdinalIgnoreCase))
            {
                if (text.Length == 6) throw new ArgumentException("Enter an expression after regex:.");
                var regex = new Regex(text[6..], RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
                rules.Add(path => regex.IsMatch(path));
                continue;
            }

            var forceWildcard = text.StartsWith("wild:", StringComparison.OrdinalIgnoreCase);
            if (forceWildcard) text = text[5..];
            if (text.Length == 0) throw new ArgumentException("Enter a pattern after wild:.");
            text = ToolPath.Key(Path.TrimEndingDirectorySeparator(text.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar)));
            var matchPath = text.Contains(Path.DirectorySeparatorChar);
            var relativePath = matchPath && !Path.IsPathFullyQualified(text);
            var wildcard = forceWildcard || relativePath || text.IndexOfAny(['*', '?']) >= 0;
            var pattern = relativePath ? "*" + Path.DirectorySeparatorChar + text : text;
            rules.Add(path =>
            {
                var value = ToolPath.Key(matchPath ? path : Path.GetFileName(path));
                return wildcard
                    ? FileSystemName.MatchesSimpleExpression(pattern, value, ignoreCase: true)
                    : string.Equals(pattern, value, StringComparison.OrdinalIgnoreCase);
            });
        }
        return rules.Count == 0 ? null : path => rules.Any(rule => rule(path));
    }

    public Task<IReadOnlyList<IReadOnlyList<FileEntry>>> ReadReviewedGroupsAsync(
        DuplicateResult result, bool verifyContents, CancellationToken token) => Task.Run<IReadOnlyList<IReadOnlyList<FileEntry>>>(async () =>
    {
        if (result.Truncated || result.Errors.Count > 0)
            throw new IOException("Complete the duplicate scan without read errors before selecting results.");
        var verified = new List<IReadOnlyList<FileEntry>>();
        foreach (var group in result.Groups)
        {
            if (group.ReviewedFiles.Count != group.Paths.Count)
                throw new IOException("The duplicate group has no reviewed filesystem identities.");
            var entries = new List<FileEntry>();
            foreach (var file in group.ReviewedFiles)
            {
                token.ThrowIfCancellationRequested();
                ToolPath.RequireNoLinks(file.Path);
                if (!file.StillMatches(result.IgnoreOffline) || (verifyContents && ComparesContents(result.Comparison) &&
                    await ToolWalker.HashAsync(file, null, token, result.IgnoreOffline, ChecksumAlgorithm(result.Comparison),
                            ChecksumLimit(file.Length, result.Md5Accuracy))
                        .ConfigureAwait(false) != group.Hash))
                    throw new IOException($"A duplicate result changed. Scan again: {file.Path}");
                entries.Add(DirectoryBrowser.ReadEntry(file.Path));
            }
            verified.Add(entries);
        }
        return verified;
    }, token);
}
