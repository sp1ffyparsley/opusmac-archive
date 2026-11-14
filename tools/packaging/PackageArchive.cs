using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace OpusMac.Packaging;

internal static class PackageArchive
{
    private const UnixFileMode FileMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead;
    private const UnixFileMode ExecutableMode = FileMode | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute;

    internal static void Create(string root, string archive)
    {
        root = Path.GetFullPath(root);
        archive = Path.GetFullPath(archive);
        if (archive.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Archive output must be outside its input directory.");
        var inventory = BundleBuilder.Inventory(root);
        var prefix = Path.GetFileName(root);
        BundleBuilder.ValidateRelative(prefix);
        using (var output = new FileStream(archive, System.IO.FileMode.CreateNew, FileAccess.Write))
        using (var gzip = new GZipStream(output, CompressionLevel.Fastest))
        using (var tar = new TarWriter(gzip, TarEntryFormat.Pax))
        {
            var directories = new HashSet<string>(StringComparer.Ordinal) { prefix };
            foreach (var file in inventory)
            {
                var parts = (prefix + "/" + file.Path).Split('/');
                for (var count = 1; count < parts.Length; count++) directories.Add(string.Join('/', parts.Take(count)));
            }
            foreach (var directory in directories.OrderBy(d => d.Count(c => c == '/')).ThenBy(d => d, StringComparer.Ordinal))
                tar.WriteEntry(new PaxTarEntry(TarEntryType.Directory, directory + "/") { Mode = ExecutableMode });
            foreach (var file in inventory)
            {
                using var input = File.OpenRead(Path.Combine(root, file.Path.Replace('/', Path.DirectorySeparatorChar)));
                tar.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, prefix + "/" + file.Path)
                {
                    Mode = file.Executable ? ExecutableMode : FileMode,
                    ModificationTime = File.GetLastWriteTimeUtc(input.Name), DataStream = input
                });
            }
        }
        Verify(root, archive);
        File.WriteAllText(archive + ".sha256", BundleBuilder.Hash(archive) + "  " + Path.GetFileName(archive) + "\n", new UTF8Encoding(false));
        Console.WriteLine("POSIX archive verified: " + archive);
    }

    internal static void Verify(string root, string archive)
    {
        var inventory = BundleBuilder.Inventory(root).ToDictionary(f => f.Path, StringComparer.OrdinalIgnoreCase);
        var prefix = Path.GetFileName(root) + "/";
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var input = File.OpenRead(archive);
        using var gzip = new GZipStream(input, CompressionMode.Decompress);
        using var tar = new TarReader(gzip);
        TarEntry? entry;
        while ((entry = tar.GetNextEntry()) != null)
        {
            BundleBuilder.ValidateRelative(entry.Name.TrimEnd('/'));
            if (!entry.Name.StartsWith(prefix, StringComparison.Ordinal)) throw new InvalidDataException("Archive root changed.");
            if (entry.EntryType == TarEntryType.Directory)
            {
                if (!entry.Mode.HasFlag(UnixFileMode.UserExecute)) throw new InvalidDataException("Archive directory is not traversable.");
                continue;
            }
            if (entry.EntryType != TarEntryType.RegularFile || entry.DataStream == null) throw new InvalidDataException("Unexpected archive entry type.");
            var relative = entry.Name[prefix.Length..];
            if (!seen.Add(relative) || !inventory.TryGetValue(relative, out var expected)) throw new InvalidDataException("Unexpected or duplicate archive path.");
            var hash = Convert.ToHexString(SHA256.HashData(entry.DataStream)).ToLowerInvariant();
            if (entry.Length != expected.Length || hash != expected.Sha256 || entry.Mode.HasFlag(UnixFileMode.UserExecute) != expected.Executable)
                throw new InvalidDataException("Archive content or executable-mode mismatch: " + relative);
        }
        if (!seen.SetEquals(inventory.Keys)) throw new InvalidDataException("Archive is incomplete.");
    }
}
