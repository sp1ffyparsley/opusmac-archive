using System.Buffers;
using System.Buffers.Binary;
using System.IO.Compression;

namespace OpusMac.Core.Tools;

/// <summary>Bounded ZIP tools. Extraction always commits into a new directory; existing files are never merged or replaced.</summary>
public sealed class ArchiveService
{
    public Task<ArchiveListing> ListAsync(string archivePath, ArchiveLimits? limits = null, CancellationToken cancellationToken = default) => Task.Run(() =>
    {
        cancellationToken.ThrowIfCancellationRequested();
        limits ??= new ArchiveLimits();
        var full = ToolPath.Full(archivePath);
        ToolPath.RequireNoLinks(full);
        using var stream = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.Read);
        ValidateEnvelope(stream, limits, cancellationToken);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
        var entries = ValidateEntries(archive, limits, cancellationToken);
        return new ArchiveListing(full, entries.Select(e => new ArchiveEntryInfo(e.Name, e.Directory, e.Entry.Length, e.Entry.CompressedLength, e.Entry.LastWriteTime.ToUniversalTime())).ToArray(), entries.Sum(e => e.Entry.Length));
    }, cancellationToken);

    public Task<ArchiveResult> CreateZipAsync(IReadOnlyList<string> sources, string archivePath, IProgress<ToolProgress>? progress = null, CancellationToken cancellationToken = default, string? expectedDestinationParentIdentity = null) => Task.Run(async () =>
    {
        ArgumentNullException.ThrowIfNull(sources);
        cancellationToken.ThrowIfCancellationRequested();
        if (sources.Count == 0) throw new ArgumentException("Select at least one file or directory.", nameof(sources));
        var target = PrepareTarget(archivePath);
        var targetParent = Path.GetDirectoryName(target)!;
        var targetParentIdentity = CaptureDestinationParentIdentity(targetParent, expectedDestinationParentIdentity);
        var limits = new ArchiveLimits();
        var manifest = new List<(ToolFileSnapshot Item, string Name)>();
        foreach (var source in sources)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var root = ToolPath.Full(source);
            var rootName = Path.GetFileName(root);
            if (ToolPath.NameError(rootName) is { } error) throw new InvalidDataException($"Cannot archive '{source}': {error}");
            var remaining = limits.MaxEntries - manifest.Count;
            if (remaining <= 0) throw new InvalidDataException("Archive entry limit exceeded.");
            var tree = ToolWalker.Scan([root], true, true, remaining, limits.MaxDepth, cancellationToken);
            if (tree.Errors.Count > 0) throw new IOException(string.Join(Environment.NewLine, tree.Errors));
            if (tree.Truncated) throw new InvalidDataException("The selected files exceed the archive entry or depth limit.");
            foreach (var item in tree.Entries)
            {
                if (item.IsLink) throw new InvalidDataException($"ZIP creation does not follow or silently discard symbolic links: {item.Path}");
                var relative = ToolPath.PathComparer.Equals(item.Path, root) ? rootName : rootName + "/" + Path.GetRelativePath(root, item.Path).Replace(Path.DirectorySeparatorChar, '/');
                manifest.Add((item, ToolPath.ArchiveName(relative, limits.MaxDepth)));
            }
        }
        ValidateNames(manifest.Select(item => (item.Name, item.Item.IsDirectory)), cancellationToken);
        long totalBytes = 0;
        foreach (var (item, _) in manifest)
        {
            if (item.Length > limits.MaxEntryBytes || item.Length > limits.MaxTotalBytes - totalBytes) throw new InvalidDataException("The selected files exceed the archive size limit.");
            totalBytes += item.Length;
        }
        var temporary = Path.Combine(targetParent, ".opusmac-zip-" + Guid.NewGuid().ToString("N") + ".tmp");
        var ownsTemporary = false;
        long completedBytes = 0;
        var completed = 0;
        try
        {
            RequireDestinationParentIdentity(targetParent, targetParentIdentity);
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 131072, FileOptions.Asynchronous))
            {
                ownsTemporary = true;
                using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
                {
                    foreach (var (item, name) in manifest)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        ToolPath.RequireNoLinks(item.Path);
                        if (!item.StillMatches()) throw new IOException($"Source changed after archive preview: {item.Path}");
                        var entry = archive.CreateEntry(name + (item.IsDirectory ? "/" : string.Empty), CompressionLevel.Fastest);
                        entry.LastWriteTime = ZipTime(item.ModifiedUtc);
                        if (!OperatingSystem.IsWindows())
                        {
                            var permissions = (int)File.GetUnixFileMode(item.Path) & 0x1ff;
                            entry.ExternalAttributes = ((item.IsDirectory ? 0x4000 : 0x8000) | permissions) << 16;
                        }
                        else if (item.IsDirectory) entry.ExternalAttributes = (int)FileAttributes.Directory;
                        if (!item.IsDirectory)
                        {
                            await using var input = new FileStream(item.Path, FileMode.Open, FileAccess.Read, FileShare.Read, 131072, FileOptions.Asynchronous | FileOptions.SequentialScan);
                            await using var destination = entry.Open();
                            var bytes = await CopyBoundedAsync(input, destination, item.Length, cancellationToken, count =>
                            {
                                completedBytes += count;
                                progress?.Report(new ToolProgress("Creating " + name, completed, manifest.Count, completedBytes, totalBytes));
                            }, computeCrc: false).ConfigureAwait(false);
                            if (bytes.Length != item.Length || !item.StillMatches()) throw new IOException($"Source changed while archiving: {item.Path}");
                        }
                        completed++;
                        progress?.Report(new ToolProgress("Archived " + name, completed, manifest.Count, completedBytes, totalBytes));
                    }
                }
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                output.Flush(flushToDisk: true);
            }
            progress?.Report(new ToolProgress("Archive ready to commit", completed, manifest.Count, completedBytes, totalBytes));
            cancellationToken.ThrowIfCancellationRequested();
            RequireDestinationParentIdentity(targetParent, targetParentIdentity);
            File.Move(temporary, target, overwrite: false);
            ownsTemporary = false;
            return new ArchiveResult(target, completed, completedBytes);
        }
        finally
        {
            if (ownsTemporary && ToolPath.Exists(temporary)) File.Delete(temporary);
        }
    }, cancellationToken);

    public Task<ArchiveResult> ExtractZipAsync(string archivePath, string destinationDirectory, ArchiveLimits? limits = null, IProgress<ToolProgress>? progress = null, CancellationToken cancellationToken = default, string? expectedArchiveIdentity = null, string? expectedDestinationParentIdentity = null) => Task.Run(async () =>
    {
        cancellationToken.ThrowIfCancellationRequested();
        limits ??= new ArchiveLimits();
        var source = ToolPath.Full(archivePath);
        var target = PrepareTarget(destinationDirectory);
        var targetParent = Path.GetDirectoryName(target)!;
        var targetParentIdentity = CaptureDestinationParentIdentity(targetParent, expectedDestinationParentIdentity);
        ToolPath.RequireNoLinks(source);
        var sourceSnapshot = ToolFileSnapshot.Read(source);
        if (sourceSnapshot.IsDirectory || sourceSnapshot.IsLink || sourceSnapshot.NativeIdentity is null)
            throw new IOException("The selected archive's native filesystem identity cannot be verified. Reopen the archive before extracting.");
        if (expectedArchiveIdentity is not null &&
            !string.Equals(sourceSnapshot.NativeIdentity, expectedArchiveIdentity, StringComparison.Ordinal))
            throw new IOException("The selected archive changed since its contents were reviewed. Reopen the archive before extracting.");
        await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 131072, FileOptions.Asynchronous | FileOptions.RandomAccess);
        if (!sourceSnapshot.StillMatches())
            throw new IOException("The selected archive changed before extraction started. Reopen it before extracting.");
        ValidateEnvelope(input, limits, cancellationToken);
        using var archive = new ZipArchive(input, ZipArchiveMode.Read, leaveOpen: true);
        var entries = ValidateEntries(archive, limits, cancellationToken);
        if (!sourceSnapshot.StillMatches())
            throw new IOException("The selected archive changed while its contents were being reviewed. Reopen it before extracting.");
        var totalBytes = entries.Sum(item => item.Entry.Length);
        var stage = Path.Combine(targetParent, ".opusmac-extract-" + Guid.NewGuid().ToString("N"));
        var ownsStage = false;
        var completed = 0;
        long completedBytes = 0;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            RequireDestinationParentIdentity(targetParent, targetParentIdentity);
            if (ToolPath.Exists(stage)) throw new IOException("The private extraction stage already exists.");
            Directory.CreateDirectory(stage);
            ownsStage = true;
            foreach (var item in entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var destination = Path.Combine(stage, item.Name.Replace('/', Path.DirectorySeparatorChar));
                if (!ToolPath.IsWithin(destination, stage)) throw new InvalidDataException("ZIP entry escapes the extraction stage.");
                ToolPath.RequireNoLinks(destination);
                if (item.Directory) Directory.CreateDirectory(destination);
                else
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                    ToolPath.RequireNoLinks(destination);
                    await using (var compressed = item.Entry.Open())
                    await using (var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 131072, FileOptions.Asynchronous | FileOptions.SequentialScan))
                    {
                        var copied = await CopyBoundedAsync(compressed, output, item.Entry.Length, cancellationToken, count =>
                        {
                            completedBytes += count;
                            if (completedBytes > limits.MaxTotalBytes) throw new InvalidDataException("Extracted content exceeds the total size limit.");
                            progress?.Report(new ToolProgress("Extracting " + item.Name, completed, entries.Count, completedBytes, totalBytes));
                        }).ConfigureAwait(false);
                        if (copied.Length != item.Entry.Length || copied.Crc32 != item.Entry.Crc32) throw new InvalidDataException($"ZIP entry failed its length or CRC integrity check: {item.Name}");
                        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                    }
                    ApplyMetadata(destination, item);
                }
                completed++;
                progress?.Report(new ToolProgress("Extracted " + item.Name, completed, entries.Count, completedBytes, totalBytes));
            }
            foreach (var directory in entries.Where(item => item.Directory).OrderByDescending(item => item.Name.Count(character => character == '/')))
                ApplyMetadata(Path.Combine(stage, directory.Name.Replace('/', Path.DirectorySeparatorChar)), directory);
            progress?.Report(new ToolProgress("Extraction ready to commit", completed, entries.Count, completedBytes, totalBytes));
            cancellationToken.ThrowIfCancellationRequested();
            if (!sourceSnapshot.StillMatches())
                throw new IOException("The selected archive changed during extraction; no output was committed.");
            RequireDestinationParentIdentity(targetParent, targetParentIdentity);
            if (ToolPath.Exists(target)) throw new IOException($"The extraction destination already exists: {target}");
            Directory.Move(stage, target);
            ownsStage = false;
            return new ArchiveResult(target, completed, completedBytes);
        }
        finally
        {
            if (ownsStage && ToolPath.Exists(stage)) Directory.Delete(stage, recursive: true);
        }
    }, cancellationToken);

    internal static string PrepareTarget(string path)
    {
        var full = ToolPath.Full(path);
        if (ToolPath.NameError(Path.GetFileName(full)) is { } error) throw new IOException(error);
        var parent = Path.GetDirectoryName(full) ?? throw new IOException("An output directory is required.");
        ToolPath.RequireNoLinks(parent);
        if (!Directory.Exists(parent)) throw new DirectoryNotFoundException($"The output parent directory does not exist: {parent}");
        if (ToolPath.Exists(full)) throw new IOException($"The output already exists and will not be overwritten: {full}");
        return full;
    }

    internal static string CaptureDestinationParentIdentity(string parent, string? expectedIdentity)
    {
        ToolPath.RequireNoLinks(parent);
        var snapshot = ToolFileSnapshot.Read(parent);
        if (!snapshot.IsDirectory || snapshot.IsLink || string.IsNullOrWhiteSpace(snapshot.NativeIdentity))
            throw new IOException("The destination folder's native filesystem identity cannot be verified.");
        if (expectedIdentity is not null &&
            (string.IsNullOrWhiteSpace(expectedIdentity) || !string.Equals(snapshot.NativeIdentity, expectedIdentity, StringComparison.Ordinal)))
            throw new IOException("The destination folder changed after it was selected. Choose the destination again before continuing.");
        return snapshot.NativeIdentity;
    }

    internal static void RequireDestinationParentIdentity(string parent, string expectedIdentity)
    {
        ToolPath.RequireNoLinks(parent);
        var snapshot = ToolFileSnapshot.Read(parent);
        // Do not compare directory mtime: creating the private stage changes it.
        // The native object ID binds the reviewed destination across staging.
        if (!snapshot.IsDirectory || snapshot.IsLink || snapshot.NativeIdentity is null ||
            !string.Equals(snapshot.NativeIdentity, expectedIdentity, StringComparison.Ordinal))
            throw new IOException("The destination folder changed during the archive operation; no output was committed.");
    }

    private sealed record ValidatedEntry(ZipArchiveEntry Entry, string Name, bool Directory);

    private static List<ValidatedEntry> ValidateEntries(ZipArchive archive, ArchiveLimits limits, CancellationToken cancellationToken)
    {
        ValidateLimits(limits);
        if (archive.Entries.Count > limits.MaxEntries) throw new InvalidDataException("ZIP entry limit exceeded.");
        var entries = new List<ValidatedEntry>(archive.Entries.Count);
        long total = 0;
        foreach (var entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var name = ToolPath.ArchiveName(entry.FullName, limits.MaxDepth);
            var unixType = (entry.ExternalAttributes >> 16) & 0xf000;
            if (unixType is not (0 or 0x4000 or 0x8000) || (entry.ExternalAttributes & (int)FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException($"ZIP links and special files are unsupported: {entry.FullName}");
            var directory = entry.FullName.EndsWith('/') || unixType == 0x4000 || (entry.ExternalAttributes & (int)FileAttributes.Directory) != 0;
            if (directory && entry.Length != 0) throw new InvalidDataException($"ZIP directory contains unexpected file data: {entry.FullName}");
            if (entry.Length < 0 || entry.Length > limits.MaxEntryBytes || entry.Length > limits.MaxTotalBytes - total) throw new InvalidDataException("ZIP uncompressed size limit exceeded.");
            if (entry.Length / (double)Math.Max(1, entry.CompressedLength) > limits.MaxExpansionRatio) throw new InvalidDataException($"ZIP expansion ratio limit exceeded: {entry.FullName}");
            total += entry.Length;
            entries.Add(new ValidatedEntry(entry, name, directory));
        }
        ValidateNames(entries.Select(item => (item.Name, item.Directory)), cancellationToken);
        return entries;
    }

    private static void ValidateNames(IEnumerable<(string Name, bool Directory)> entries, CancellationToken cancellationToken)
    {
        var names = new Dictionary<string, (string ExactName, bool Directory)>(ToolPath.PortableComparer);
        var explicitNames = new HashSet<string>(ToolPath.PortableComparer);
        long storedPathCharacters = 0;
        foreach (var (name, directory) in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!explicitNames.Add(ToolPath.Key(name))) throw new InvalidDataException($"ZIP contains duplicate or case/Unicode-colliding paths: {name}");
            var parts = name.Split('/');
            for (var index = 0; index < parts.Length; index++)
            {
                var prefix = string.Join('/', parts.Take(index + 1));
                var isDirectory = index < parts.Length - 1 || directory;
                var key = ToolPath.Key(prefix);
                if (names.TryGetValue(key, out var existing))
                {
                    if (existing.Directory != isDirectory) throw new InvalidDataException($"ZIP path is both a file and a directory: {prefix}");
                    if (!string.Equals(existing.ExactName, prefix, StringComparison.Ordinal)) throw new InvalidDataException($"ZIP contains case/Unicode-colliding parent paths: {prefix}");
                }
                else
                {
                    storedPathCharacters += prefix.Length;
                    if (names.Count >= 200000 || storedPathCharacters > 33554432) throw new InvalidDataException("ZIP path metadata exceeds the safe memory budget.");
                    names.Add(key, (prefix, isDirectory));
                }
            }
        }
    }

    private static void ValidateLimits(ArchiveLimits limits)
    {
        if (limits.MaxEntries <= 0 || limits.MaxEntryBytes < 0 || limits.MaxTotalBytes < 0 || limits.MaxDepth <= 0 || !double.IsFinite(limits.MaxExpansionRatio) || limits.MaxExpansionRatio <= 0)
            throw new ArgumentOutOfRangeException(nameof(limits), "Archive entry/depth/ratio limits must be positive and byte limits cannot be negative.");
    }

    // Bound central-directory allocation before ZipArchive materializes entries, including ZIP64 archives.
    private static void ValidateEnvelope(Stream input, ArchiveLimits limits, CancellationToken cancellationToken)
    {
        ValidateLimits(limits);
        if (input.Length < 22) throw new InvalidDataException("File is too short to be a ZIP archive.");
        var tail = new byte[(int)Math.Min(input.Length, 65557)];
        input.Position = input.Length - tail.Length;
        input.ReadExactly(tail);
        var found = -1;
        for (var i = tail.Length - 22; i >= 0; i--)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(i)) == 0x06054b50 && i + 22 + BinaryPrimitives.ReadUInt16LittleEndian(tail.AsSpan(i + 20)) == tail.Length) { found = i; break; }
        }
        if (found < 0) throw new InvalidDataException("ZIP end-of-directory record is missing or invalid.");
        var endOffset = input.Length - tail.Length + found;
        var end = tail.AsSpan(found);
        if (BinaryPrimitives.ReadUInt16LittleEndian(end[4..]) != 0 || BinaryPrimitives.ReadUInt16LittleEndian(end[6..]) != 0) throw new InvalidDataException("Split ZIP archives are unsupported.");
        ulong count = BinaryPrimitives.ReadUInt16LittleEndian(end[10..]);
        ulong size = BinaryPrimitives.ReadUInt32LittleEndian(end[12..]);
        ulong offset = BinaryPrimitives.ReadUInt32LittleEndian(end[16..]);
        if (count == ushort.MaxValue || size == uint.MaxValue || offset == uint.MaxValue)
        {
            if (endOffset < 20) throw new InvalidDataException("ZIP64 locator is missing.");
            Span<byte> locator = stackalloc byte[20];
            input.Position = endOffset - 20;
            input.ReadExactly(locator);
            if (BinaryPrimitives.ReadUInt32LittleEndian(locator) != 0x07064b50 || BinaryPrimitives.ReadUInt32LittleEndian(locator[4..]) != 0 || BinaryPrimitives.ReadUInt32LittleEndian(locator[16..]) != 1) throw new InvalidDataException("Invalid or split ZIP64 archive.");
            var recordOffset = BinaryPrimitives.ReadUInt64LittleEndian(locator[8..]);
            if (recordOffset > (ulong)Math.Max(0, endOffset - 76)) throw new InvalidDataException("ZIP64 directory offset is invalid.");
            Span<byte> record = stackalloc byte[56];
            input.Position = checked((long)recordOffset);
            input.ReadExactly(record);
            if (BinaryPrimitives.ReadUInt32LittleEndian(record) != 0x06064b50 || BinaryPrimitives.ReadUInt32LittleEndian(record[16..]) != 0 || BinaryPrimitives.ReadUInt32LittleEndian(record[20..]) != 0) throw new InvalidDataException("Invalid ZIP64 directory record.");
            count = BinaryPrimitives.ReadUInt64LittleEndian(record[32..]);
            if (count != BinaryPrimitives.ReadUInt64LittleEndian(record[24..])) throw new InvalidDataException("Split ZIP64 archives are unsupported.");
            size = BinaryPrimitives.ReadUInt64LittleEndian(record[40..]);
            offset = BinaryPrimitives.ReadUInt64LittleEndian(record[48..]);
        }
        else if (count != BinaryPrimitives.ReadUInt16LittleEndian(end[8..])) throw new InvalidDataException("Split ZIP archives are unsupported.");
        if (count > (ulong)limits.MaxEntries) throw new InvalidDataException("ZIP entry limit exceeded.");
        var maxDirectoryBytes = (ulong)Math.Min(134217728L, (long)limits.MaxEntries * 8192 + 65536);
        if (size > maxDirectoryBytes || offset > (ulong)endOffset || size > (ulong)endOffset - offset) throw new InvalidDataException("ZIP central directory exceeds its size or offset bounds.");
        input.Position = checked((long)offset);
        var directoryEnd = checked((long)(offset + size));
        ulong observedEntries = 0;
        Span<byte> header = stackalloc byte[46];
        while (input.Position < directoryEnd)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (directoryEnd - input.Position < header.Length) throw new InvalidDataException("ZIP central directory contains an incomplete entry.");
            input.ReadExactly(header);
            if (BinaryPrimitives.ReadUInt32LittleEndian(header) != 0x02014b50) throw new InvalidDataException("ZIP central directory entry is invalid.");
            observedEntries++;
            if (observedEntries > count || observedEntries > (ulong)limits.MaxEntries) throw new InvalidDataException("ZIP central directory contains more entries than its declared or permitted count.");
            var nameLength = BinaryPrimitives.ReadUInt16LittleEndian(header[28..]);
            var extraLength = BinaryPrimitives.ReadUInt16LittleEndian(header[30..]);
            var commentLength = BinaryPrimitives.ReadUInt16LittleEndian(header[32..]);
            if (nameLength is 0 or > 4096 || BinaryPrimitives.ReadUInt16LittleEndian(header[34..]) != 0) throw new InvalidDataException("ZIP entry name length or disk number is unsupported.");
            var variableLength = (long)nameLength + extraLength + commentLength;
            if (variableLength > directoryEnd - input.Position) throw new InvalidDataException("ZIP central directory entry exceeds its bounds.");
            input.Position += variableLength;
        }
        if (observedEntries != count) throw new InvalidDataException("ZIP central directory entry count is inconsistent.");
        input.Position = 0;
    }

    private static DateTimeOffset ZipTime(DateTimeOffset value) => value.Year < 1980 ? new DateTimeOffset(1980, 1, 1, 0, 0, 0, TimeSpan.Zero) : value.Year > 2107 ? new DateTimeOffset(2107, 12, 31, 23, 59, 58, TimeSpan.Zero) : value;

    private static void ApplyMetadata(string path, ValidatedEntry item)
    {
        if (item.Directory) Directory.SetLastWriteTimeUtc(path, item.Entry.LastWriteTime.UtcDateTime);
        else File.SetLastWriteTimeUtc(path, item.Entry.LastWriteTime.UtcDateTime);
        if (!OperatingSystem.IsWindows())
        {
            var mode = (item.Entry.ExternalAttributes >> 16) & 0x1ff;
            if (mode != 0)
            {
                mode |= (int)(UnixFileMode.UserRead | UnixFileMode.UserWrite);
                if (item.Directory) mode |= (int)UnixFileMode.UserExecute;
                File.SetUnixFileMode(path, (UnixFileMode)mode);
            }
        }
    }

    private static readonly uint[] CrcTable = Enumerable.Range(0, 256).Select(index =>
    {
        var value = (uint)index;
        for (var bit = 0; bit < 8; bit++) value = (value & 1) != 0 ? 0xedb88320U ^ (value >> 1) : value >> 1;
        return value;
    }).ToArray();

    internal static async Task<(long Length, uint Crc32)> CopyBoundedAsync(Stream input, Stream output, long maximumBytes, CancellationToken cancellationToken, Action<int> bytesCopied, bool computeCrc = true)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(131072);
        long total = 0;
        uint crc = uint.MaxValue;
        try
        {
            while (true)
            {
                var count = await input.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken).ConfigureAwait(false);
                if (count == 0) break;
                if (count > maximumBytes - total) throw new InvalidDataException("Content exceeds its declared or permitted length.");
                total += count;
                if (computeCrc)
                    for (var i = 0; i < count; i++) crc = CrcTable[(byte)(crc ^ buffer[i])] ^ (crc >> 8);
                await output.WriteAsync(buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
                bytesCopied(count);
            }
            cancellationToken.ThrowIfCancellationRequested();
            return (total, crc ^ uint.MaxValue);
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
    }
}
