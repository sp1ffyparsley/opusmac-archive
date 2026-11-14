using System.Security.Cryptography;
using System.Text;
using System.Buffers;

namespace OpusMac.Core.Operations;

internal static class OperationPaths
{
    internal static StringComparer Comparer => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
    internal static StringComparison Comparison => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
    private static StringComparison GuardComparison => OperatingSystem.IsMacOS() || OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    internal static bool Exists(string path)
    {
        try { _ = File.GetAttributes(path); return true; }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
    }

    internal static string Entry(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("A file or folder path is required.");
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        var parent = Path.GetDirectoryName(full);
        if (string.IsNullOrEmpty(parent) || Comparer.Equals(full, Path.GetPathRoot(full)))
            throw new IOException("A filesystem root cannot be changed or used as an operation source.");
        return Path.Combine(DirectoryPath(parent), Path.GetFileName(full));
    }

    internal static string DirectoryPath(string path)
    {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        var root = Path.GetPathRoot(full) ?? throw new IOException("The directory has no filesystem root.");
        var current = root;
        var parts = full[root.Length..].Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
        foreach (var part in parts)
        {
            current = Path.Combine(current, part);
            var info = new DirectoryInfo(current);
            info.Refresh();
            if (!info.Exists) throw new DirectoryNotFoundException($"Directory is unavailable: {current}");
            if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                var target = info.ResolveLinkTarget(true) ?? throw new IOException($"Cannot resolve directory link: {current}");
                if (target is not DirectoryInfo directory || !directory.Exists)
                    throw new IOException($"Directory link target is unavailable: {current}");
                current = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory.FullName));
            }
        }
        return Path.TrimEndingDirectorySeparator(current);
    }

    internal static bool Contains(string ancestor, string candidate)
    {
        ancestor = Path.TrimEndingDirectorySeparator(ancestor);
        candidate = Path.TrimEndingDirectorySeparator(candidate);
        return string.Equals(ancestor, candidate, GuardComparison) ||
               candidate.StartsWith(ancestor + Path.DirectorySeparatorChar, GuardComparison);
    }

    internal static void StableParent(string path, string expectedParent)
    {
        var actual = DirectoryPath(Path.GetDirectoryName(path) ?? throw new IOException("Missing parent directory."));
        if (!Comparer.Equals(actual, expectedParent)) throw new IOException($"The parent directory changed during the operation: {path}");
    }

    internal static void UnchangedLocation(string path)
    {
        if (!Comparer.Equals(Entry(path), path))
            throw new IOException($"The item's parent was redirected during the operation: {path}");
    }

    internal static bool IsRealDirectory(string path) =>
        (File.GetAttributes(path) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) == FileAttributes.Directory;

    internal static void ValidName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name) || name is "." or ".." || Path.IsPathRooted(name) ||
            name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name.Contains('/') || name.Contains('\\'))
            throw new ArgumentException("Enter one valid file or folder name, without a path.");
        if (OperatingSystem.IsWindows())
        {
            if (name.EndsWith(' ') || name.EndsWith('.')) throw new ArgumentException("A Windows name cannot end in a dot or space.");
            var stem = name.Split('.')[0];
            if (new[] { "CON", "PRN", "AUX", "NUL" }.Contains(stem, StringComparer.OrdinalIgnoreCase) ||
                (stem.Length == 4 && (stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase) ||
                stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)) && stem[3] is >= '1' and <= '9'))
                throw new ArgumentException("That name is reserved by Windows.");
        }
    }

    internal static string AvailableName(string destination, ISet<string>? reserved = null)
    {
        if (!Exists(destination) && !(reserved?.Contains(destination) ?? false)) return destination;
        var parent = Path.GetDirectoryName(destination)!;
        var filename = Path.GetFileName(destination);
        var extension = Path.GetExtension(filename);
        var stem = Path.GetFileNameWithoutExtension(filename);
        if (Exists(destination) && IsRealDirectory(destination)) { stem = filename; extension = ""; }
        for (var number = 2; number < 100000; number++)
        {
            var candidate = Path.Combine(parent, $"{stem} ({number}){extension}");
            if (!Exists(candidate) && !(reserved?.Contains(candidate) ?? false)) return candidate;
        }
        throw new IOException("Unable to find a free destination name.");
    }

    internal static string CreateArea(string parent, string operationId, out string token)
    {
        parent = DirectoryPath(parent);
        token = Guid.NewGuid().ToString("N");
        var area = Path.Combine(parent, $".opusmac-{operationId}-{token}");
        if (Exists(area)) throw new IOException("The private staging location unexpectedly exists.");
        Directory.CreateDirectory(area);
        using var marker = new FileStream(Path.Combine(area, ".owner"), FileMode.CreateNew, FileAccess.Write, FileShare.None);
        var bytes = Encoding.UTF8.GetBytes(token);
        marker.Write(bytes);
        marker.Flush(true);
        return area;
    }

    internal static string CreateUndoArea(string destination, string operationId, out string token)
    {
        var parent = Path.GetDirectoryName(destination) ?? throw new IOException("The undo destination has no parent directory.");
        if (!OperatingSystem.IsWindows()) return CreateArea(parent, operationId, out token);

        // Windows Shell Recycle Bin operations do not reliably accept extended-length
        // paths. Keep the temporary payload on the same volume, but place its owned
        // staging directory at the nearest writable ancestor that keeps the Shell
        // path below the traditional MAX_PATH boundary.
        var volume = Path.GetPathRoot(Path.GetFullPath(destination));
        for (var current = parent; !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(current)))
        {
            var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(current));
            if (!string.Equals(Path.GetPathRoot(full), volume, StringComparison.OrdinalIgnoreCase)) break;
            token = Guid.NewGuid().ToString("N");
            var candidate = Path.Combine(full, $".opusmac-{operationId}-{token}");
            if (Path.Combine(candidate, "payload").Length >= 240) continue;
            try { return CreateArea(full, operationId, out token); }
            catch (UnauthorizedAccessException) { /* Try the next ancestor without changing the destination. */ }
        }
        throw new IOException("No writable same-volume staging ancestor can keep the reversible Undo payload within the Windows Shell path limit.");
    }

    internal static void VerifyArea(string path, string token)
    {
        if (string.IsNullOrEmpty(token) || !Path.GetFileName(path).StartsWith(".opusmac-", StringComparison.Ordinal) ||
            !IsRealDirectory(path) || !Comparer.Equals(DirectoryPath(path), Path.TrimEndingDirectorySeparator(path)))
            throw new IOException($"Recovery area identity changed; retained for inspection: {path}");
        var marker = Path.Combine(path, ".owner");
        if (!Exists(marker) || (File.GetAttributes(marker) & FileAttributes.ReparsePoint) != 0 ||
            new FileInfo(marker).Length != token.Length ||
            !string.Equals(File.ReadAllText(marker), token, StringComparison.Ordinal))
            throw new IOException($"Recovery area ownership cannot be verified: {path}");
    }

    internal static void RemoveOwned(string area, string token, string entry)
    {
        VerifyArea(area, token);
        VerifyOwnedEntry(area, entry);
        DeleteTree(Path.GetFullPath(entry));
    }

    internal static void VerifyOwnedEntry(string area, string entry)
    {
        var fullArea = Path.TrimEndingDirectorySeparator(Path.GetFullPath(area));
        var full = Path.GetFullPath(entry);
        if (!full.StartsWith(fullArea + Path.DirectorySeparatorChar, Comparison) ||
            Comparer.Equals(full, Path.Combine(fullArea, ".owner")))
            throw new IOException("Refusing to use an item outside its private staging area.");
    }

    internal static (long Bytes, int Files) MeasureOwnedArea(string area, string token)
    {
        VerifyArea(area, token);
        long bytes = 0;
        var files = 0;
        var marker = Path.Combine(area, ".owner");
        foreach (var child in Directory.EnumerateFileSystemEntries(area))
        {
            if (Comparer.Equals(child, marker)) continue;
            MeasureTree(child, ref bytes, ref files);
        }
        return (bytes, files);
    }

    internal static void RemoveOwnedArea(string area, string token)
    {
        if (!Exists(area)) return;
        VerifyArea(area, token);
        var marker = Path.Combine(area, ".owner");
        foreach (var child in Directory.EnumerateFileSystemEntries(area).Where(path => !Comparer.Equals(path, marker)).ToArray())
            DeleteTree(child);
        RemoveEmptyArea(area, token);
    }

    internal static void RemoveEmptyArea(string? area, string? token)
    {
        if (area is null || token is null || !Exists(area)) return;
        VerifyArea(area, token);
        if (Directory.EnumerateFileSystemEntries(area).Any(p => !Comparer.Equals(p, Path.Combine(area, ".owner")))) return;
        File.Delete(Path.Combine(area, ".owner"));
        Directory.Delete(area, false);
    }

    private static void DeleteTree(string path)
    {
        if (!Exists(path)) return;
        var attributes = File.GetAttributes(path);
        if ((attributes & FileAttributes.ReparsePoint) != 0)
        {
            if ((attributes & FileAttributes.Directory) != 0) Directory.Delete(path, false);
            else File.Delete(path);
            return;
        }
        if ((attributes & FileAttributes.Directory) != 0)
        {
            foreach (var child in Directory.EnumerateFileSystemEntries(path)) DeleteTree(child);
            if ((attributes & FileAttributes.ReadOnly) != 0) File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);
            Directory.Delete(path, false);
        }
        else
        {
            if ((attributes & FileAttributes.ReadOnly) != 0) File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);
            File.Delete(path);
        }
    }

    private static void MeasureTree(string path, ref long bytes, ref int files)
    {
        var attributes = File.GetAttributes(path);
        if ((attributes & FileAttributes.ReparsePoint) != 0)
        {
            files = checked(files + 1);
            return;
        }
        if ((attributes & FileAttributes.Directory) != 0)
        {
            foreach (var child in Directory.EnumerateFileSystemEntries(path)) MeasureTree(child, ref bytes, ref files);
            return;
        }
        bytes = checked(bytes + new FileInfo(path).Length);
        files = checked(files + 1);
    }

    internal static async Task<string> FingerprintAsync(string path, bool content, CancellationToken cancellationToken)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await AddAsync(path, ".", hash, content, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static async Task AddAsync(string path, string relative, IncrementalHash hash, bool content, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var attributes = File.GetAttributes(path);
        AddText(hash, relative);
        // Hidden is derived from the basename on Unix; Archive can change during native copying.
        AddText(hash, ((int)(attributes & ~(FileAttributes.Hidden | FileAttributes.Archive))).ToString(System.Globalization.CultureInfo.InvariantCulture));
        if ((attributes & FileAttributes.ReparsePoint) != 0)
        {
            FileSystemInfo link = (attributes & FileAttributes.Directory) != 0 ? new DirectoryInfo(path) : new FileInfo(path);
            AddText(hash, "L");
            AddText(hash, link.LinkTarget ?? throw new IOException($"Unsupported link or reparse point: {path}"));
            return;
        }
        AddText(hash, File.GetLastWriteTimeUtc(path).Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture));
        if ((attributes & FileAttributes.Directory) != 0)
        {
            AddText(hash, "D");
            foreach (var child in Directory.EnumerateFileSystemEntries(path).OrderBy(Path.GetFileName, StringComparer.Ordinal))
                await AddAsync(child, relative + "/" + Path.GetFileName(child), hash, content, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            AddText(hash, "F");
            var info = new FileInfo(path);
            var length = info.Length;
            var modified = info.LastWriteTimeUtc;
            AddText(hash, length.ToString(System.Globalization.CultureInfo.InvariantCulture));
            if (content)
            {
                await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                var buffer = ArrayPool<byte>.Shared.Rent(128 * 1024);
                try
                {
                    int read;
                    while ((read = await stream.ReadAsync(buffer.AsMemory(0, 128 * 1024), cancellationToken).ConfigureAwait(false)) > 0)
                        hash.AppendData(buffer, 0, read);
                }
                finally { ArrayPool<byte>.Shared.Return(buffer); }
            }
            info.Refresh();
            if (!info.Exists || length != info.Length || modified != info.LastWriteTimeUtc)
                throw new IOException($"The file changed while it was being checked: {path}");
        }
    }

    private static void AddText(IncrementalHash hash, string text)
    {
        hash.AppendData(Encoding.UTF8.GetBytes(text));
        hash.AppendData([0]);
    }
}
