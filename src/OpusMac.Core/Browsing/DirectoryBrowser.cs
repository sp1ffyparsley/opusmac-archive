using System.IO.Enumeration;
using System.Text;
using OpusMac.Core.Search;

namespace OpusMac.Core.Browsing;

public sealed class DirectoryBrowser
{
    private static readonly HashSet<string> PackageExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".app", ".bundle", ".framework", ".photoslibrary", ".photolibrary", ".plugin", ".vst3" };

    public Task<DirectoryListing> ReadAsync(string directory, BrowseOptions options,
        IProgress<DirectoryBatch>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.MaxEntries is < 1 or > 1000000) throw new ArgumentOutOfRangeException(nameof(options), "The listing limit must be between 1 and 1,000,000.");
        var path = ResolvePath(directory);
        return Task.Run(() => Read(path, options, progress, cancellationToken), cancellationToken);
    }

    private static DirectoryListing Read(string directory, BrowseOptions options,
        IProgress<DirectoryBatch>? progress, CancellationToken token)
    {
        var filter = FilenameFilter.Compile(options.Filter);
        var entries = new List<FileEntry>();
        var errors = new List<string>();
        var batch = new List<FileEntry>(128);
        var pending = new Stack<string>();
        pending.Push(directory);
        var truncated = false;
        string? rootError = null;
        var inspected = 0;
        while (pending.Count > 0 && !truncated)
        {
            token.ThrowIfCancellationRequested();
            var current = pending.Pop();
            try
            {
                foreach (var info in new DirectoryInfo(current).EnumerateFileSystemInfos())
                {
                    token.ThrowIfCancellationRequested();
                    if (++inspected > options.MaxEntries) { truncated = true; break; }
                    var path = info.FullName;
                    try
                    {
                        // Directory enumeration already supplies these fields from its native
                        // find record. Keep that metadata with the entry instead of issuing
                        // another attribute/size/time query for every matching row.
                        var attributes = info.Attributes;
                        var name = info.Name;
                        var isDirectory = (attributes & FileAttributes.Directory) != 0;
                        var isLink = (attributes & FileAttributes.ReparsePoint) != 0;
                        if (!options.ShowHidden && ((attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0 || name.StartsWith('.'))) continue;
                        if (options.Recursive && isDirectory && !isLink && !IsPackagePath(path, isDirectory)) pending.Push(path);
                        if (!filter.Matches(name)) continue;
                        var entry = ReadEntry(info, attributes);
                        entries.Add(entry);
                        batch.Add(entry);
                        if (batch.Count >= 128)
                        {
                            progress?.Report(new DirectoryBatch(batch.ToArray(), entries.Count));
                            batch.Clear();
                        }
                    }
                    catch (Exception e) when (IsFileError(e)) { AddError(errors, path, e); }
                }
            }
            catch (Exception e) when (IsFileError(e))
            {
                AddError(errors, current, e);
                if (PathEquals(current, directory)) rootError = e.Message;
            }
        }
        if (batch.Count > 0) progress?.Report(new DirectoryBatch(batch.ToArray(), entries.Count));
        token.ThrowIfCancellationRequested();
        var sorted = SortEntries(entries);
        token.ThrowIfCancellationRequested();
        return new DirectoryListing(directory, sorted, errors, truncated, rootError);
    }

    public static (Dictionary<string, long> Sizes, List<string> Errors) CalculateFolderSizes(
        IReadOnlyList<FileEntry> folders, CancellationToken token, Action<int>? reportCompleted = null,
        bool followNestedLinks = false)
    {
        var sizes = new Dictionary<string, long>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        var errors = new List<string>();
        for (var index = 0; index < folders.Count; index++)
        {
            token.ThrowIfCancellationRequested();
            var folder = folders[index];
            try { sizes[folder.FullPath] = CalculateFolderSize(folder.FullPath, token, followNestedLinks, followRootLink: true); }
            catch (Exception error) when (IsFileError(error) || error is OverflowException)
            { errors.Add($"{folder.Name}: {error.Message}"); }
            reportCompleted?.Invoke(index + 1);
        }
        return (sizes, errors);
    }

    public static long CalculateFolderSize(string directory, CancellationToken cancellationToken,
        bool followNestedLinks = false, bool followRootLink = false)
    {
        var root = ReadEntry(directory);
        if (!root.IsDirectory || (root.IsSymbolicLink && !followRootLink))
            throw new IOException("Folder size requires an accessible directory.");
        var pending = new Stack<string>();
        pending.Push(root.IsSymbolicLink ? ResolveSizeLink(root.FullPath, isDirectory: true) : root.FullPath);
        var visited = followNestedLinks ? new HashSet<string>(StringComparer.Ordinal) : null;
        var options = new EnumerationOptions { IgnoreInaccessible = false, AttributesToSkip = 0 };
        long bytes = 0;
        var inspected = 0;
        while (pending.TryPop(out var current))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (visited is not null)
            {
                var identity = ListaryClient.ReadNativeItemIdentity(current)
                    ?? throw new IOException("A linked folder could not be identified safely.");
                if (!visited.Add(identity)) continue;
            }
            foreach (var path in Directory.EnumerateFileSystemEntries(current, "*", options))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (++inspected > 1_000_000) throw new IOException("Folder size exceeded the one-million-entry scan limit.");
                var attributes = File.GetAttributes(path);
                var isDirectory = (attributes & FileAttributes.Directory) != 0;
                var isLink = (attributes & FileAttributes.ReparsePoint) != 0;
                if (isLink && !followNestedLinks) continue;
                var target = isLink ? ResolveSizeLink(path, isDirectory) : path;
                if (isDirectory) pending.Push(target);
                else bytes = checked(bytes + new FileInfo(target).Length);
            }
        }
        return bytes;
    }

    private static string ResolveSizeLink(string path, bool isDirectory)
    {
        FileSystemInfo link = isDirectory ? new DirectoryInfo(path) : new FileInfo(path);
        return link.ResolveLinkTarget(returnFinalTarget: true)?.FullName
            ?? throw new IOException("The symbolic link target could not be resolved.");
    }

    public static FileEntry ReadEntry(string path)
    {
        path = Path.GetFullPath(path);
        var attributes = File.GetAttributes(path);
        var isDirectory = (attributes & FileAttributes.Directory) != 0;
        FileSystemInfo info = isDirectory ? new DirectoryInfo(path) : new FileInfo(path);
        return ReadEntry(info, attributes);
    }

    private static FileEntry ReadEntry(FileSystemInfo info, FileAttributes attributes)
    {
        var path = info.FullName;
        var isDirectory = (attributes & FileAttributes.Directory) != 0;
        var isLink = (attributes & FileAttributes.ReparsePoint) != 0;
        // Reading a link's target length would hydrate/cloud-fetch or fail on a dangling link.
        var length = !isDirectory && !isLink ? ((FileInfo)info).Length : 0;
        var modified = new DateTimeOffset(DateTime.SpecifyKind(info.LastWriteTimeUtc, DateTimeKind.Utc));
        var created = new DateTimeOffset(DateTime.SpecifyKind(info.CreationTimeUtc, DateTimeKind.Utc));
        return new FileEntry(path, info.Name, isDirectory, isLink, length, modified, created, attributes,
            IsPackagePath(path, isDirectory));
    }

    public static bool IsPackagePath(string path, bool isDirectory = true) =>
        OperatingSystem.IsMacOS() && isDirectory && PackageExtensions.Contains(Path.GetExtension(Path.TrimEndingDirectorySeparator(path)));

    public static string ResolvePath(string path, string? baseDirectory = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        path = path.Trim();
        if (path.Length >= 2 && path[0] == '"' && path[^1] == '"') path = path[1..^1];
        if (path.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
        {
            if (!Uri.TryCreate(path, UriKind.Absolute, out var uri) || !uri.IsFile) throw new ArgumentException("The file URL is invalid.", nameof(path));
            path = uri.LocalPath;
        }
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (path == "~") path = home;
        else if (path.StartsWith("~/", StringComparison.Ordinal) || path.StartsWith(@"~\", StringComparison.Ordinal)) path = Path.Combine(home, path[2..]);
        return Path.GetFullPath(path, baseDirectory is null ? home : Path.GetFullPath(baseDirectory));
    }

    public static IReadOnlyList<FileEntry> FilterEntries(IEnumerable<FileEntry> entries, string filter)
    {
        var matcher = FilenameFilter.Compile(filter);
        return entries.Where(entry => matcher.Matches(entry.Name)).ToArray();
    }

    public static bool MatchesFilter(string name, string? filter) => FilenameFilter.Compile(filter).Matches(name);

    internal static Func<string, bool> CompileFilenameFilter(string? filter) => FilenameFilter.Compile(filter).Matches;

    private sealed class FilenameFilter(FilterTerm[] includes, FilterTerm[] excludes, bool normalizeNames)
    {
        private static readonly FilenameFilter MatchAll = new([], [], false);

        public static FilenameFilter Compile(string? filter)
        {
            if (string.IsNullOrWhiteSpace(filter)) return MatchAll;
            var includes = new List<FilterTerm>();
            var excludes = new List<FilterTerm>();
            // These terms belong to this query. Reusing them avoids reparsing and normalizing
            // the same expression for every row without retaining a global query cache.
            foreach (var part in filter.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            {
                var exclude = part.StartsWith('!');
                if (exclude && part.Length == 1) continue;
                var term = (exclude ? part[1..] : part).Normalize(NormalizationForm.FormC);
                (exclude ? excludes : includes).Add(new FilterTerm(term, term.IndexOfAny(['*', '?']) >= 0));
            }
            return new FilenameFilter(includes.ToArray(), excludes.ToArray(), true);
        }

        public bool Matches(string name)
        {
            if (!normalizeNames) return true;
            name = name.Normalize(NormalizationForm.FormC);
            foreach (var term in excludes)
                if (term.Matches(name)) return false;
            if (includes.Length == 0) return true;
            foreach (var term in includes)
                if (term.Matches(name)) return true;
            return false;
        }
    }

    private readonly record struct FilterTerm(string Text, bool UsesWildcards)
    {
        public bool Matches(string name) => UsesWildcards
            ? FileSystemName.MatchesSimpleExpression(Text, name, ignoreCase: true)
            : name.Contains(Text, StringComparison.OrdinalIgnoreCase);
    }

    public static IReadOnlyList<FileEntry> SortEntries(IEnumerable<FileEntry> entries, string sortColumn = "Name",
        bool descending = false, bool foldersFirst = true)
    {
        var items = entries.ToArray();
        var key = sortColumn.ToLowerInvariant();
        Array.Sort(items, (a, b) =>
        {
            if (foldersFirst && a.IsBrowsable != b.IsBrowsable) return a.IsBrowsable ? -1 : 1;
            var compared = key switch
            {
                "size" or "length" => (a.CalculatedFolderLength ?? a.Length).CompareTo(b.CalculatedFolderLength ?? b.Length),
                "modified" or "date modified" => a.ModifiedUtc.CompareTo(b.ModifiedUtc),
                "created" or "date created" => a.CreatedUtc.CompareTo(b.CreatedUtc),
                "type" or "kind" => StringComparer.OrdinalIgnoreCase.Compare(a.Kind, b.Kind),
                "attr" or "attribute" or "attributes" => ((int)a.Attributes).CompareTo((int)b.Attributes),
                "path" => NaturalNameComparer.Instance.Compare(a.FullPath, b.FullPath),
                _ => NaturalNameComparer.Instance.Compare(a.Name, b.Name)
            };
            if (compared == 0) compared = NaturalNameComparer.Instance.Compare(a.Name, b.Name);
            if (compared == 0) compared = StringComparer.Ordinal.Compare(a.FullPath, b.FullPath);
            return descending ? -Math.Sign(compared) : Math.Sign(compared);
        });
        return items;
    }

    private static bool IsFileError(Exception e) => e is IOException or UnauthorizedAccessException or System.Security.SecurityException;
    private static bool PathEquals(string left, string right) => string.Equals(left, right,
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    private static void AddError(List<string> errors, string path, Exception error)
    {
        if (errors.Count < 100) errors.Add($"{path}: {error.Message}");
        else if (errors.Count == 100) errors.Add("Further inaccessible entries were omitted from this error list.");
    }
}

public sealed class NaturalNameComparer : IComparer<string>
{
    public static NaturalNameComparer Instance { get; } = new();
    public int Compare(string? left, string? right)
    {
        if (ReferenceEquals(left, right)) return 0;
        if (left is null) return -1;
        if (right is null) return 1;
        var i = 0; var j = 0;
        while (i < left.Length && j < right.Length)
        {
            if (char.IsAsciiDigit(left[i]) && char.IsAsciiDigit(right[j]))
            {
                var a = i; var b = j;
                while (i < left.Length && char.IsAsciiDigit(left[i])) i++;
                while (j < right.Length && char.IsAsciiDigit(right[j])) j++;
                var significantA = a; var significantB = b;
                while (significantA < i && left[significantA] == '0') significantA++;
                while (significantB < j && right[significantB] == '0') significantB++;
                var length = (i - significantA).CompareTo(j - significantB);
                if (length != 0) return length;
                var number = left.AsSpan(significantA, i - significantA).CompareTo(right.AsSpan(significantB, j - significantB), StringComparison.Ordinal);
                if (number != 0) return number;
                var padding = (i - a).CompareTo(j - b);
                if (padding != 0) return padding;
            }
            else
            {
                var compare = char.ToUpperInvariant(left[i]).CompareTo(char.ToUpperInvariant(right[j]));
                if (compare != 0) return compare;
                i++; j++;
            }
        }
        return (left.Length - i).CompareTo(right.Length - j);
    }
}
