using System.Buffers;
using System.Security.Cryptography;
using System.Text;
using OpusMac.Core.Search;

namespace OpusMac.Core.Tools;

internal static class ToolPath
{
    internal static readonly StringComparer PathComparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
    internal static readonly StringComparer PortableComparer = StringComparer.OrdinalIgnoreCase;
    internal static string Key(string value) => value.Normalize(NormalizationForm.FormC);

    internal static string Full(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    }

    internal static bool Exists(string path)
    {
        try { _ = File.GetAttributes(path); return true; }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
    }

    internal static void RequireNoLinks(string path)
    {
        var full = Full(path);
        var root = Path.GetPathRoot(full)!;
        var current = root;
        foreach (var segment in full[root.Length..].Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Where(part => part.Length > 0))
        {
            current = Path.Combine(current, segment);
            if (!Exists(current)) break;
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException($"Symbolic links and reparse points are not followed by this tool: {current}");
            FileSystemInfo info = Directory.Exists(current) ? new DirectoryInfo(current) : new FileInfo(current);
            if (info.LinkTarget is not null) throw new IOException($"Symbolic links are not followed by this tool: {current}");
        }
    }

    internal static bool IsWithin(string path, string root)
    {
        var relative = Path.GetRelativePath(Full(root), Full(path));
        return relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) && !Path.IsPathRooted(relative);
    }

    internal static string? NameError(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "A filename cannot be empty or whitespace.";
        if (name is "." or "..") return "Dot path components cannot be filenames.";
        if (name.Any(c => c < 32 || "<>:\"/\\|?*".Contains(c))) return "The name contains a path separator or a character unsafe across Mac and Windows filesystems.";
        if (name.EndsWith(' ') || name.EndsWith('.')) return "A portable filename cannot end in a space or period.";
        if (Encoding.UTF8.GetByteCount(name) > 255) return "The filename exceeds the portable 255-byte limit.";
        var stem = name.Split('.')[0];
        if (stem.Equals("CON", StringComparison.OrdinalIgnoreCase) || stem.Equals("PRN", StringComparison.OrdinalIgnoreCase) || stem.Equals("AUX", StringComparison.OrdinalIgnoreCase) || stem.Equals("NUL", StringComparison.OrdinalIgnoreCase) ||
            (stem.Length == 4 && (stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase) || stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)) && stem[3] is >= '1' and <= '9'))
            return "The filename is reserved on Windows and cannot be used by this portable operation.";
        return null;
    }

    internal static string ArchiveName(string name, int maxDepth)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 4096 || name.Contains('\\') || name.StartsWith('/') || Path.IsPathRooted(name))
            throw new InvalidDataException($"Unsafe ZIP entry path: {name}");
        var trimmed = name.EndsWith('/') ? name[..^1] : name;
        var components = trimmed.Split('/');
        if (components.Length > maxDepth) throw new InvalidDataException($"ZIP entry exceeds the depth limit: {name}");
        foreach (var component in components)
            if (NameError(component) is { } error) throw new InvalidDataException($"Unsafe ZIP entry '{name}': {error}");
        return string.Join('/', components);
    }
}

internal sealed record ToolFileSnapshot(string Path, bool IsDirectory, bool IsLink, long Length, DateTimeOffset ModifiedUtc, FileAttributes Attributes, string? NativeIdentity)
{
    internal static ToolFileSnapshot Read(string path)
    {
        var full = ToolPath.Full(path);
        var attributes = File.GetAttributes(full);
        var directory = (attributes & FileAttributes.Directory) != 0;
        FileSystemInfo info = directory ? new DirectoryInfo(full) : new FileInfo(full);
        info.Refresh();
        var link = (attributes & FileAttributes.ReparsePoint) != 0 || info.LinkTarget is not null;
        var length = directory || link ? 0 : ((FileInfo)info).Length;
        var nativeIdentity = ListaryClient.ReadNativeItemIdentity(full, out var isOffline);
        if (isOffline) attributes |= FileAttributes.Offline;
        return new ToolFileSnapshot(full, directory, link, length, new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero), attributes, nativeIdentity);
    }

    internal bool StillMatches(bool requireOnline = false)
    {
        var current = Read(Path);
        if (requireOnline && current.Attributes.HasFlag(FileAttributes.Offline))
            throw new IOException($"File is offline: {Path}");
        return !IsLink && !current.IsLink && NativeIdentity is not null && current.NativeIdentity is not null &&
            string.Equals(current.NativeIdentity, NativeIdentity, StringComparison.Ordinal) &&
            current.IsDirectory == IsDirectory && current.Length == Length && current.ModifiedUtc == ModifiedUtc;
    }
}

internal sealed record ToolWalkResult(IReadOnlyList<ToolFileSnapshot> Entries, IReadOnlyList<string> Errors, bool Truncated);

internal static class ToolWalker
{
    internal static ToolWalkResult Scan(IReadOnlyList<string> roots, bool recursive, bool includeHidden, int maxEntries, int maxDepth,
        CancellationToken cancellationToken, Func<string, bool>? exclude = null)
    {
        if (maxEntries <= 0 || maxDepth < 0) throw new ArgumentOutOfRangeException(nameof(maxEntries), "Entry limits must be positive and depth cannot be negative.");
        var entries = new List<ToolFileSnapshot>();
        var errors = new List<string>();
        var visited = new HashSet<string>(ToolPath.PathComparer);
        var pending = new Stack<(string Path, int Depth, bool IsRoot)>();
        foreach (var root in roots.Reverse()) pending.Push((root, 0, true));
        var truncated = false;
        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (entries.Count >= maxEntries) { truncated = true; break; }
            var (path, depth, isRoot) = pending.Pop();
            try
            {
                var full = ToolPath.Full(path);
                if (isRoot && exclude?.Invoke(full) == true) continue;
                if (!visited.Add(full)) continue;
                if (isRoot)
                {
                    var parent = Path.GetDirectoryName(full);
                    if (parent is not null) ToolPath.RequireNoLinks(parent);
                }
                var item = ToolFileSnapshot.Read(full);
                if (!isRoot && !includeHidden && ((item.Attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0 || Path.GetFileName(full).StartsWith('.'))) continue;
                entries.Add(item);
                if (!item.IsDirectory || item.IsLink || (!recursive && !isRoot)) continue;
                if (depth >= maxDepth) { truncated = true; continue; }
                // Enumerate only the current directory. Links are recorded as leaves and never recursed.
                ToolPath.RequireNoLinks(full);
                var children = new List<string>();
                using var enumerator = Directory.EnumerateFileSystemEntries(full, "*", new EnumerationOptions { RecurseSubdirectories = false, IgnoreInaccessible = false, AttributesToSkip = 0, ReturnSpecialDirectories = false }).GetEnumerator();
                while (enumerator.MoveNext())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (exclude?.Invoke(enumerator.Current) == true) continue;
                    if (children.Count + entries.Count + pending.Count >= maxEntries) { truncated = true; break; }
                    children.Add(enumerator.Current);
                }
                children.Sort(ToolPath.PathComparer);
                for (var i = children.Count - 1; i >= 0; i--) pending.Push((children[i], depth + 1, false));
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                errors.Add($"{path}: {error.Message}");
            }
        }
        return new ToolWalkResult(entries, errors, truncated);
    }

    internal static async Task<string> HashAsync(ToolFileSnapshot item, Action<int>? bytesRead, CancellationToken cancellationToken,
        bool requireOnline = false, HashAlgorithmName? algorithm = null, long? byteLimit = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ToolPath.RequireNoLinks(item.Path);
        if (item.IsDirectory || item.IsLink || !item.StillMatches(requireOnline)) throw new IOException($"File changed before reading: {item.Path}");
        var buffer = ArrayPool<byte>.Shared.Rent(131072);
        try
        {
            using var hash = IncrementalHash.CreateHash(algorithm ?? HashAlgorithmName.SHA256);
            long total = 0;
            await using (var input = new FileStream(item.Path, FileMode.Open, FileAccess.Read, FileShare.Read, 131072, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                while (byteLimit is null || total < byteLimit.Value)
                {
                    var request = byteLimit is { } limit ? (int)Math.Min(buffer.Length, limit - total) : buffer.Length;
                    var count = await input.ReadAsync(buffer.AsMemory(0, request), cancellationToken).ConfigureAwait(false);
                    if (count == 0) break;
                    total += count;
                    if (total > item.Length) throw new IOException($"File grew while reading: {item.Path}");
                    hash.AppendData(buffer, 0, count);
                    bytesRead?.Invoke(count);
                }
            }
            ToolPath.RequireNoLinks(item.Path);
            if (total != (byteLimit ?? item.Length) || !item.StillMatches(requireOnline))
                throw new IOException($"File changed while reading: {item.Path}");
            return Convert.ToHexString(hash.GetHashAndReset());
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
    }
}
