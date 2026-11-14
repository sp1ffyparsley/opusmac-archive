using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using OpusMac.Core;
using OpusMac.Core.Operations;
using OpusMac.Core.Platform;
using OpusMac.Core.Search;
using OpusMac.Core.Tools;

namespace OpusMac.Checks.Suites;

public sealed class AdvancedChecks : ICheckSuite
{
    public async Task RunAsync(CheckRunner checks, string sandbox)
    {
        await CheckArchivesAsync(checks, sandbox);
        await CheckSplitRollbackAsync(checks, sandbox);
        await CheckDuplicatesAsync(checks, sandbox);
        await CheckComparisonAsync(checks, sandbox);
        await CheckRenamePlansAsync(checks, sandbox);
        await CheckOperationIntegrationAsync(checks, sandbox);
    }

    private static async Task CheckSplitRollbackAsync(CheckRunner checks, string sandbox)
    {
        await checks.CheckAsync("Split rolls back its published piece when a later target arrives during staging", async () =>
        {
            var root = CaseDirectory(sandbox, "split-late-collision");
            var source = Path.Combine(root, "source.bin");
            await File.WriteAllBytesAsync(source, Payload(163, 254));
            var originalHash = Hash(source);
            var collision = Path.Combine(root, "source.bin.002");
            var injected = false;
            var progress = new InlineProgress(value =>
            {
                if (value.Description != "Split pieces ready to commit") return;
                File.WriteAllText(collision, "independent piece");
                injected = true;
            });
            await CheckRunner.ThrowsAsync<IOException>(() => FileParts.SplitAsync(source,
                ListaryClient.ReadNativeItemIdentity(source)!, root, ListaryClient.ReadNativeItemIdentity(root)!,
                60, false, progress, CancellationToken.None));
            CheckRunner.True(injected, "The late target was not created after staging.");
            CheckRunner.Equal("independent piece", File.ReadAllText(collision), "Rollback changed an independently created target.");
            CheckRunner.Equal(originalHash, Hash(source), "Rollback changed the split input.");
            OnlyChildren(root, "source.bin", "source.bin.002");
        });
        await checks.CheckAsync("Canceling a split while bytes are staging removes its temporary pieces", async () =>
        {
            var root = CaseDirectory(sandbox, "split-cancel");
            var source = Path.Combine(root, "source.bin");
            await File.WriteAllBytesAsync(source, Payload(163, 857));
            var originalHash = Hash(source);
            using var cancel = new CancellationTokenSource();
            var progress = new InlineProgress(value => { if (value.CompletedBytes > 0) cancel.Cancel(); });
            await CheckRunner.ThrowsAsync<OperationCanceledException>(() => FileParts.SplitAsync(source,
                ListaryClient.ReadNativeItemIdentity(source)!, root, ListaryClient.ReadNativeItemIdentity(root)!,
                60, true, progress, cancel.Token));
            CheckRunner.True(cancel.IsCancellationRequested, "The test did not cancel during a staged write.");
            CheckRunner.Equal(originalHash, Hash(source), "Canceling changed the split input.");
            OnlyChildren(root, "source.bin");
        });
    }

    private static async Task CheckArchivesAsync(CheckRunner checks, string sandbox)
    {
        (string Name, Func<string, string> EntryName)[] unsafePaths =
        [
            ("parent traversal", _ => "../escape.txt"),
            ("backslash traversal", _ => @"..\escape.txt"),
            ("absolute path", root => Path.Combine(root, "escape.txt").Replace('\\', '/')),
            ("rooted path", root =>
            {
                var path = Path.Combine(root, "escape.txt").Replace('\\', '/');
                return path.Length > 1 && path[1] == ':' ? path[2..] : path;
            }),
            ("drive relative path", root => OperatingSystem.IsWindows()
                ? Path.GetPathRoot(root)![..2] + Path.GetRelativePath(Environment.CurrentDirectory, Path.Combine(root, "escape.txt")).Replace('\\', '/')
                : "C:escape.txt")
        ];
        foreach (var unsafePath in unsafePaths)
        {
            await checks.CheckAsync($"Archive rejects {unsafePath.Name} before writing", async () =>
            {
                var root = CaseDirectory(sandbox, "zip-path");
                await RejectArchiveAsync(root, [new ZipFixtureEntry(unsafePath.EntryName(root), Bytes("escape"))]);
            });
        }

        await checks.CheckAsync("Archive rejects Unix symlink entries", async () =>
        {
            var root = CaseDirectory(sandbox, "zip-link");
            await RejectArchiveAsync(root, [new ZipFixtureEntry("link", Bytes("../outside.txt"), unchecked((int)0xA1FF0000))]);
        });
        await checks.CheckAsync("Archive rejects case insensitive output collisions", async () =>
        {
            var root = CaseDirectory(sandbox, "zip-case-collision");
            await RejectArchiveAsync(root, [new("Report.txt", Bytes("one")), new("report.txt", Bytes("two"))]);
        });
        await checks.CheckAsync("Archive rejects case collisions in implicit parent directories", async () =>
        {
            var root = CaseDirectory(sandbox, "zip-parent-case-collision");
            await RejectArchiveAsync(root, [new("a/file.txt", Bytes("one")), new("A/other.txt", Bytes("two"))]);
        });
        await checks.CheckAsync("Archive rejects Unicode normalized output collisions", async () =>
        {
            var root = CaseDirectory(sandbox, "zip-unicode-collision");
            await RejectArchiveAsync(root, [new("café.txt", Bytes("one")), new("cafe\u0301.txt", Bytes("two"))]);
        });
        await checks.CheckAsync("Archive rejects repeated output names", async () =>
        {
            var root = CaseDirectory(sandbox, "zip-duplicate");
            await RejectArchiveAsync(root, [new("same.txt", Bytes("one")), new("same.txt", Bytes("two"))]);
        });
        await checks.CheckAsync("Archive rejects file and directory path conflicts", async () =>
        {
            var root = CaseDirectory(sandbox, "zip-type-conflict");
            await RejectArchiveAsync(root, [new("folder", Bytes("file")), new("folder/child.txt", Bytes("child"))]);
        });
        await checks.CheckAsync("Archive enforces entry count before extraction", async () =>
        {
            var root = CaseDirectory(sandbox, "zip-entry-limit");
            await RejectArchiveAsync(root, [new("one.txt", Bytes("one")), new("two.txt", Bytes("two"))], new ArchiveLimits(MaxEntries: 1));
        });
        await checks.CheckAsync("Archive rejects forged directory counts before materializing entries", async () =>
        {
            var root = CaseDirectory(sandbox, "zip-forged-count");
            var archive = Path.Combine(root, "input.zip");
            var destination = Path.Combine(root, "output");
            WriteZip(archive, [new("one.txt", Bytes("one")), new("two.txt", Bytes("two"))]);
            var bytes = await File.ReadAllBytesAsync(archive);
            var endOffset = bytes.Length - 22;
            CheckRunner.Equal(0x06054b50u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(endOffset, 4)), "Fixture has no ZIP end record");
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(endOffset + 8, 2), 1);
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(endOffset + 10, 2), 1);
            await File.WriteAllBytesAsync(archive, bytes);
            var before = Hash(archive);
            var service = new ArchiveService();
            await CheckRunner.ThrowsAsync<InvalidDataException>(() => service.ListAsync(archive));
            await CheckRunner.ThrowsAsync<InvalidDataException>(() => service.ExtractZipAsync(archive, destination));
            CheckRunner.Equal(before, Hash(archive), "Rejected forged archive was modified");
            CheckRunner.True(!Directory.Exists(destination) && !File.Exists(destination), "Forged entry count produced an output");
            OnlyChildren(root, "input.zip");
        });
        await checks.CheckAsync("Archive enforces per entry expansion bounds", async () =>
        {
            var root = CaseDirectory(sandbox, "zip-entry-byte-limit");
            await RejectArchiveAsync(root, [new("large.bin", new byte[64])], new ArchiveLimits(MaxEntryBytes: 32));
        });
        await checks.CheckAsync("Archive enforces aggregate expansion bounds", async () =>
        {
            var root = CaseDirectory(sandbox, "zip-total-byte-limit");
            await RejectArchiveAsync(root, [new("one.bin", new byte[32]), new("two.bin", new byte[32])], new ArchiveLimits(MaxTotalBytes: 63));
        });
        await checks.CheckAsync("Archive enforces compressed expansion ratio", async () =>
        {
            var root = CaseDirectory(sandbox, "zip-ratio-limit");
            await RejectArchiveAsync(root, [new("zeros.bin", new byte[1024 * 1024])], new ArchiveLimits(MaxExpansionRatio: 2), CompressionLevel.Optimal);
        });
        await checks.CheckAsync("Archive enforces directory depth bounds", async () =>
        {
            var root = CaseDirectory(sandbox, "zip-depth-limit");
            await RejectArchiveAsync(root, [new("a/b/c/d/e/f/file.txt", Bytes("deep"))], new ArchiveLimits(MaxDepth: 2));
        });
        await checks.CheckAsync("Archive rejects invalid safety limits", async () =>
        {
            var root = CaseDirectory(sandbox, "zip-invalid-limit");
            var archive = Path.Combine(root, "input.zip");
            WriteZip(archive, [new("item.txt", Bytes("item"))]);
            var service = new ArchiveService();
            await CheckRunner.ThrowsAsync<ArgumentOutOfRangeException>(() => service.ListAsync(archive, new ArchiveLimits(MaxEntries: 0)));
            await CheckRunner.ThrowsAsync<ArgumentOutOfRangeException>(() => service.ExtractZipAsync(archive, Path.Combine(root, "output"), new ArchiveLimits(MaxEntries: 0)));
            OnlyChildren(root, "input.zip");
        });
        foreach (var understateLength in new[] { false, true })
        {
            await checks.CheckAsync(understateLength ? "Archive bounds actual bytes when ZIP metadata understates size" : "Archive rejects corrupt payload CRC without committing", async () =>
            {
                var root = CaseDirectory(sandbox, "zip-content-integrity");
                var archive = Path.Combine(root, "input.zip");
                var destination = Path.Combine(root, "output");
                WriteZip(archive, [new("payload.bin", Payload(128, 4861))]);
                var bytes = await File.ReadAllBytesAsync(archive);
                CheckRunner.Equal(0x04034b50u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(0, 4)), "Fixture has no local ZIP header");
                if (understateLength)
                {
                    var centralOffset = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(bytes.Length - 6, 4)));
                    CheckRunner.Equal(0x02014b50u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(centralOffset, 4)), "Fixture has no central ZIP header");
                    BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(22, 4), 1);
                    BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(centralOffset + 24, 4), 1);
                }
                else
                {
                    var payloadOffset = 30 + BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(26, 2)) + BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(28, 2));
                    bytes[payloadOffset] ^= 0x55;
                }
                await File.WriteAllBytesAsync(archive, bytes);
                var before = Hash(archive);
                await CheckRunner.ThrowsAsync<InvalidDataException>(() => new ArchiveService().ExtractZipAsync(archive, destination));
                CheckRunner.Equal(before, Hash(archive), "Rejected extraction changed its source archive");
                CheckRunner.True(!Directory.Exists(destination) && !File.Exists(destination), "Unverified content was committed");
                OnlyChildren(root, "input.zip");
            });
        }
        await checks.CheckAsync("Archive roundtrip preserves binary Unicode and empty entries", async () =>
        {
            var root = CaseDirectory(sandbox, "zip-roundtrip");
            var source = Path.Combine(root, "source");
            var tree = Path.Combine(source, "tree");
            Directory.CreateDirectory(Path.Combine(tree, "nested"));
            Directory.CreateDirectory(Path.Combine(tree, "empty-dir"));
            await File.WriteAllBytesAsync(Path.Combine(source, "alpha.bin"), Payload(1024 * 1024 + 37, 1127));
            await File.WriteAllTextAsync(Path.Combine(tree, "nested", "unicode-μ.txt"), "Directory Opus — résumé — 日本語");
            await File.WriteAllBytesAsync(Path.Combine(tree, "empty.txt"), []);
            var sourceBefore = Snapshot(source);
            var archive = Path.Combine(root, "roundtrip.zip");
            var destination = Path.Combine(root, "output");
            var service = new ArchiveService();
            var created = await service.CreateZipAsync([Path.Combine(source, "alpha.bin"), tree], archive);
            CheckRunner.True(File.Exists(archive), "Successful creation did not commit an archive");
            CheckRunner.Equal(Path.GetFullPath(archive), Path.GetFullPath(created.Destination));
            var listing = await service.ListAsync(archive);
            CheckRunner.True(listing.Entries.Any(entry => entry.Name == "alpha.bin"), "Top level file missing from archive");
            CheckRunner.True(listing.Entries.Any(entry => entry.Name == "tree/nested/unicode-μ.txt"), "Nested Unicode file missing from archive");
            var extracted = await service.ExtractZipAsync(archive, destination);
            CheckRunner.Equal(Path.GetFullPath(destination), Path.GetFullPath(extracted.Destination));
            AssertSnapshot(sourceBefore, Snapshot(destination), "ZIP roundtrip changed file names or bytes");
            AssertSnapshot(sourceBefore, Snapshot(source), "ZIP creation changed source files");
            CheckRunner.True(Directory.Exists(Path.Combine(destination, "tree", "empty-dir")), "Empty directory was lost");
            CheckRunner.Equal(0L, new FileInfo(Path.Combine(destination, "tree", "empty.txt")).Length);
            CheckRunner.True(extracted.EntryCount > 0 && extracted.Bytes > 0, "Committed extraction result has no work");
        });
        await checks.CheckAsync("Archive lists and extracts a valid small ZIP64 envelope", async () =>
        {
            var root = CaseDirectory(sandbox, "zip64-roundtrip");
            var archive = Path.Combine(root, "input.zip");
            var destination = Path.Combine(root, "output");
            var payload = Payload(257, 4237);
            WriteZip(archive, [new("small.bin", payload)]);
            var original = await File.ReadAllBytesAsync(archive);
            var endOffset = original.Length - 22;
            CheckRunner.Equal(0x06054b50u, BinaryPrimitives.ReadUInt32LittleEndian(original.AsSpan(endOffset, 4)), "Fixture has no ZIP end record");
            var count = BinaryPrimitives.ReadUInt16LittleEndian(original.AsSpan(endOffset + 10, 2));
            var centralSize = BinaryPrimitives.ReadUInt32LittleEndian(original.AsSpan(endOffset + 12, 4));
            var centralOffset = BinaryPrimitives.ReadUInt32LittleEndian(original.AsSpan(endOffset + 16, 4));
            var zip64 = new byte[original.Length + 76];
            Array.Copy(original, 0, zip64, 0, endOffset);
            BinaryPrimitives.WriteUInt32LittleEndian(zip64.AsSpan(endOffset, 4), 0x06064b50);
            BinaryPrimitives.WriteUInt64LittleEndian(zip64.AsSpan(endOffset + 4, 8), 44);
            BinaryPrimitives.WriteUInt16LittleEndian(zip64.AsSpan(endOffset + 12, 2), 45);
            BinaryPrimitives.WriteUInt16LittleEndian(zip64.AsSpan(endOffset + 14, 2), 45);
            BinaryPrimitives.WriteUInt64LittleEndian(zip64.AsSpan(endOffset + 24, 8), count);
            BinaryPrimitives.WriteUInt64LittleEndian(zip64.AsSpan(endOffset + 32, 8), count);
            BinaryPrimitives.WriteUInt64LittleEndian(zip64.AsSpan(endOffset + 40, 8), centralSize);
            BinaryPrimitives.WriteUInt64LittleEndian(zip64.AsSpan(endOffset + 48, 8), centralOffset);
            var locatorOffset = endOffset + 56;
            BinaryPrimitives.WriteUInt32LittleEndian(zip64.AsSpan(locatorOffset, 4), 0x07064b50);
            BinaryPrimitives.WriteUInt64LittleEndian(zip64.AsSpan(locatorOffset + 8, 8), (ulong)endOffset);
            BinaryPrimitives.WriteUInt32LittleEndian(zip64.AsSpan(locatorOffset + 16, 4), 1);
            var finalEndOffset = endOffset + 76;
            Array.Copy(original, endOffset, zip64, finalEndOffset, 22);
            BinaryPrimitives.WriteUInt16LittleEndian(zip64.AsSpan(finalEndOffset + 8, 2), ushort.MaxValue);
            BinaryPrimitives.WriteUInt16LittleEndian(zip64.AsSpan(finalEndOffset + 10, 2), ushort.MaxValue);
            BinaryPrimitives.WriteUInt32LittleEndian(zip64.AsSpan(finalEndOffset + 12, 4), uint.MaxValue);
            BinaryPrimitives.WriteUInt32LittleEndian(zip64.AsSpan(finalEndOffset + 16, 4), uint.MaxValue);
            await File.WriteAllBytesAsync(archive, zip64);
            var before = Hash(archive);
            var service = new ArchiveService();
            var listing = await service.ListAsync(archive);
            CheckRunner.Equal(1, listing.Entries.Count);
            CheckRunner.Equal((long)payload.Length, listing.TotalUncompressedBytes);
            var result = await service.ExtractZipAsync(archive, destination);
            CheckRunner.Equal(1, result.EntryCount);
            CheckRunner.Equal((long)payload.Length, result.Bytes);
            CheckRunner.Equal(Convert.ToHexString(SHA256.HashData(payload)), Hash(Path.Combine(destination, "small.bin")), "ZIP64 roundtrip changed payload bytes");
            CheckRunner.Equal(before, Hash(archive), "ZIP64 extraction changed its source archive");
        });
        await checks.CheckAsync("Archive creation retains existing destination and source", async () =>
        {
            var root = CaseDirectory(sandbox, "zip-existing-file");
            var source = Path.Combine(root, "source.txt");
            var destination = Path.Combine(root, "existing.zip");
            await File.WriteAllTextAsync(source, "Keep the original source.");
            await File.WriteAllTextAsync(destination, "Keep the previous destination.");
            var before = Snapshot(root);
            await CheckRunner.ThrowsAsync<IOException>(() => new ArchiveService().CreateZipAsync([source], destination));
            AssertSnapshot(before, Snapshot(root), "Archive collision modified source or destination");
            OnlyChildren(root, "source.txt", "existing.zip");
        });
        await checks.CheckAsync("Archive extraction retains an existing destination directory", async () =>
        {
            var root = CaseDirectory(sandbox, "zip-existing-directory");
            var archive = Path.Combine(root, "input.zip");
            var destination = Path.Combine(root, "existing");
            Directory.CreateDirectory(destination);
            await File.WriteAllTextAsync(Path.Combine(destination, "sentinel.txt"), "Keep this directory intact.");
            WriteZip(archive, [new("sentinel.txt", Bytes("replacement")), new("new.txt", Bytes("new"))]);
            var before = Snapshot(root);
            await CheckRunner.ThrowsAsync<IOException>(() => new ArchiveService().ExtractZipAsync(archive, destination));
            AssertSnapshot(before, Snapshot(root), "Extraction collision modified archive or destination");
            OnlyChildren(root, "input.zip", "existing");
        });
        await checks.CheckAsync("ZIP extraction rejects an archive identity changed since preview", async () =>
        {
            var root = CaseDirectory(sandbox, "zip-extract-source-identity");
            var archivePath = Path.Combine(root, "reviewed.zip");
            var heldOriginal = Path.Combine(root, "held-original.zip");
            var destination = Path.Combine(root, "must-not-extract");
            var originalPayload = Bytes("Original archive payload stays reviewed.");
            var replacementPayload = Bytes(new string('R', originalPayload.Length));
            static async Task WriteFixtureAsync(string path, byte[] payload)
            {
                using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
                var entry = archive.CreateEntry("reviewed.txt", CompressionLevel.NoCompression);
                await using var stream = entry.Open();
                await stream.WriteAsync(payload);
            }
            await WriteFixtureAsync(archivePath, originalPayload);
            var originalArchiveBytes = await File.ReadAllBytesAsync(archivePath);
            var originalStamp = File.GetLastWriteTimeUtc(archivePath);
            var originalIdentity = OpusMac.Core.Search.ListaryClient.ReadNativeItemIdentity(archivePath);
            CheckRunner.True(originalIdentity is not null, "The reviewed archive has no native identity.");
            File.Move(archivePath, heldOriginal);
            await WriteFixtureAsync(archivePath, replacementPayload);
            File.SetLastWriteTimeUtc(archivePath, originalStamp);
            var replacementArchiveBytes = await File.ReadAllBytesAsync(archivePath);
            var replacementIdentity = OpusMac.Core.Search.ListaryClient.ReadNativeItemIdentity(archivePath);
            CheckRunner.True(replacementArchiveBytes.Length == originalArchiveBytes.Length &&
                File.GetLastWriteTimeUtc(archivePath) == originalStamp &&
                replacementIdentity is not null &&
                !string.Equals(originalIdentity, replacementIdentity, StringComparison.Ordinal),
                "The replacement must have the reviewed size and timestamp but a different native identity.");
            IOException? failure = null;
            try
            {
                await new ArchiveService().ExtractZipAsync(archivePath, destination,
                    expectedArchiveIdentity: originalIdentity);
            }
            catch (IOException error) { failure = error; }
            CheckRunner.True(failure is not null, "The core extraction owner accepted an archive identity that no longer matched the preview.");
            CheckRunner.True(!Directory.Exists(destination), "Stale archive extraction created its destination.");
            CheckRunner.True((await File.ReadAllBytesAsync(heldOriginal)).SequenceEqual(originalArchiveBytes),
                "The held reviewed archive changed.");
            CheckRunner.True((await File.ReadAllBytesAsync(archivePath)).SequenceEqual(replacementArchiveBytes),
                "The replacement archive changed after refusal.");
            OnlyChildren(root, "reviewed.zip", "held-original.zip");
        });
        await checks.CheckAsync("ZIP creation rejects nested same-path replacement with identical size and timestamp", async () =>
        {
            var root = CaseDirectory(sandbox, "zip-nested-source-identity");
            var tree = Path.Combine(root, "source-tree");
            Directory.CreateDirectory(tree);
            var first = Path.Combine(tree, "first.bin");
            var second = Path.Combine(tree, "second.bin");
            var held = Path.Combine(root, "held-original-second.bin");
            var archive = Path.Combine(root, "must-not-commit.zip");
            await File.WriteAllBytesAsync(first, Payload(8192, 123));
            var original = Bytes("nested file original payload with a fixed length");
            var replacement = Bytes(new string('R', original.Length));
            await File.WriteAllBytesAsync(second, original);
            var fileStamp = DateTime.UtcNow.AddMinutes(-10);
            File.SetLastWriteTimeUtc(second, fileStamp);
            var treeStamp = Directory.GetLastWriteTimeUtc(tree);
            var originalIdentity = OpusMac.Core.Search.ListaryClient.ReadNativeItemIdentity(second);
            var swapped = false;
            var progress = new InlineProgress(value =>
            {
                if (swapped || value.CompletedItems < 1 ||
                    !value.Description.StartsWith("Archived ", StringComparison.Ordinal)) return;
                // The root directory is the first manifest item. Swap a later
                // child after the manifest exists, then restore all metadata
                // that the former path/size/mtime-only guard relied on.
                File.Move(second, held);
                File.WriteAllBytes(second, replacement);
                File.SetLastWriteTimeUtc(second, fileStamp);
                Directory.SetLastWriteTimeUtc(tree, treeStamp);
                swapped = true;
            });
            IOException? failure = null;
            try { await new ArchiveService().CreateZipAsync([tree], archive, progress); }
            catch (IOException error) { failure = error; }
            CheckRunner.True(swapped, "The deterministic progress hook did not replace the nested source.");
            var replacementIdentity = OpusMac.Core.Search.ListaryClient.ReadNativeItemIdentity(second);
            CheckRunner.True(originalIdentity is not null && replacementIdentity is not null &&
                !string.Equals(originalIdentity, replacementIdentity, StringComparison.Ordinal),
                "The replacement must have a distinct native filesystem identity.");
            CheckRunner.True(failure is not null,
                "ZIP creation accepted a nested replacement with matching size and timestamp: " + (File.Exists(archive) ? "archive committed" : "no exception"));
            CheckRunner.True(!File.Exists(archive), "A stale-source archive was committed.");
            CheckRunner.True(new FileInfo(second).Length == original.Length &&
                File.GetLastWriteTimeUtc(second) == fileStamp &&
                Directory.GetLastWriteTimeUtc(tree) == treeStamp,
                "The adversarial replacement did not retain the reviewed metadata.");
            CheckRunner.True((await File.ReadAllBytesAsync(held)).SequenceEqual(original),
                "The held original source was not preserved byte-for-byte.");
            CheckRunner.True((await File.ReadAllBytesAsync(second)).SequenceEqual(replacement),
                "The replacement source was unexpectedly changed.");
            OnlyChildren(root, "source-tree", "held-original-second.bin");
        });
        await checks.CheckAsync("ZIP creation refuses a replaced destination parent after staging", async () =>
        {
            var root = CaseDirectory(sandbox, "zip-create-destination-parent-identity");
            var source = Path.Combine(root, "source.bin");
            var destinationParent = Path.Combine(root, "destination");
            var heldParent = Path.Combine(root, "held-destination");
            Directory.CreateDirectory(destinationParent);
            var originalSource = Payload(4096, 917);
            await File.WriteAllBytesAsync(source, originalSource);
            var originalParentIdentity = OpusMac.Core.Search.ListaryClient.ReadNativeItemIdentity(destinationParent);
            CheckRunner.True(originalParentIdentity is not null, "The reviewed ZIP destination parent has no native identity.");
            var swapped = false;
            var progress = new InlineProgress(value =>
            {
                if (swapped || !string.Equals(value.Description, "Archive ready to commit", StringComparison.Ordinal)) return;
                var staged = Directory.GetFiles(destinationParent, ".opusmac-zip-*.tmp", SearchOption.TopDirectoryOnly);
                CheckRunner.True(staged.Length == 1, "Expected exactly one closed ZIP staging file before commit.");
                var stagedName = Path.GetFileName(staged[0]);
                Directory.Move(destinationParent, heldParent);
                Directory.CreateDirectory(destinationParent);
                // Model a competing path replacement that carries the owned stage
                // into the new directory, so path-only commit checks would publish it there.
                File.Move(Path.Combine(heldParent, stagedName), Path.Combine(destinationParent, stagedName));
                swapped = true;
            });
            IOException? failure = null;
            try
            {
                await new ArchiveService().CreateZipAsync([source], Path.Combine(destinationParent, "created.zip"), progress,
                    expectedDestinationParentIdentity: originalParentIdentity);
            }
            catch (IOException error) { failure = error; }
            CheckRunner.True(swapped, "The deterministic destination-parent substitution hook did not run.");
            var replacementParentIdentity = OpusMac.Core.Search.ListaryClient.ReadNativeItemIdentity(destinationParent);
            CheckRunner.True(replacementParentIdentity is not null &&
                !string.Equals(originalParentIdentity, replacementParentIdentity, StringComparison.Ordinal),
                "The replacement destination parent must have a different native identity.");
            CheckRunner.True(failure is not null, "ZIP creation committed into a destination parent replaced after staging.");
            CheckRunner.True(!File.Exists(Path.Combine(destinationParent, "created.zip")), "A ZIP was committed into the replacement destination parent.");
            CheckRunner.True(!Directory.EnumerateFileSystemEntries(destinationParent).Any(), "The replacement destination retained an uncommitted ZIP staging file.");
            CheckRunner.True(!Directory.EnumerateFileSystemEntries(heldParent).Any(), "The original destination parent retained an owned ZIP staging file.");
            CheckRunner.True((await File.ReadAllBytesAsync(source)).SequenceEqual(originalSource), "ZIP destination refusal changed the source bytes.");
            IOException? staleReviewFailure = null;
            try
            {
                await new ArchiveService().CreateZipAsync([source], Path.Combine(destinationParent, "stale-review.zip"),
                    expectedDestinationParentIdentity: originalParentIdentity);
            }
            catch (IOException error) { staleReviewFailure = error; }
            CheckRunner.True(staleReviewFailure is not null, "ZIP creation accepted a destination parent replaced after Save review.");
            OnlyChildren(root, "source.bin", "destination", "held-destination");
        });
        await checks.CheckAsync("ZIP extraction refuses a replaced destination parent after staging", async () =>
        {
            var root = CaseDirectory(sandbox, "zip-extract-destination-parent-identity");
            var archivePath = Path.Combine(root, "source.zip");
            var destinationParent = Path.Combine(root, "destination");
            var heldParent = Path.Combine(root, "held-destination");
            var target = Path.Combine(destinationParent, "unpacked");
            Directory.CreateDirectory(destinationParent);
            WriteZip(archivePath, [new ZipFixtureEntry("payload.txt", Bytes("Destination parent custody fixture."))]);
            var originalArchive = await File.ReadAllBytesAsync(archivePath);
            var originalParentIdentity = OpusMac.Core.Search.ListaryClient.ReadNativeItemIdentity(destinationParent);
            CheckRunner.True(originalParentIdentity is not null, "The reviewed extraction parent has no native identity.");
            var swapped = false;
            var progress = new InlineProgress(value =>
            {
                if (swapped || !string.Equals(value.Description, "Extraction ready to commit", StringComparison.Ordinal)) return;
                var stages = Directory.GetDirectories(destinationParent, ".opusmac-extract-*", SearchOption.TopDirectoryOnly);
                CheckRunner.True(stages.Length == 1, "Expected exactly one completed extraction stage before commit.");
                var stageName = Path.GetFileName(stages[0]);
                Directory.Move(destinationParent, heldParent);
                Directory.CreateDirectory(destinationParent);
                // Relocate the private stage into the replacement path to expose
                // any path-only commit that ignores the reviewed parent identity.
                Directory.Move(Path.Combine(heldParent, stageName), Path.Combine(destinationParent, stageName));
                swapped = true;
            });
            IOException? failure = null;
            try
            {
                await new ArchiveService().ExtractZipAsync(archivePath, target, progress: progress,
                    expectedDestinationParentIdentity: originalParentIdentity);
            }
            catch (IOException error) { failure = error; }
            CheckRunner.True(swapped, "The deterministic extraction-parent substitution hook did not run.");
            var replacementParentIdentity = OpusMac.Core.Search.ListaryClient.ReadNativeItemIdentity(destinationParent);
            CheckRunner.True(replacementParentIdentity is not null &&
                !string.Equals(originalParentIdentity, replacementParentIdentity, StringComparison.Ordinal),
                "The replacement extraction parent must have a different native identity.");
            CheckRunner.True(failure is not null, "ZIP extraction committed into a destination parent replaced after staging.");
            CheckRunner.True(!Directory.Exists(target), "An extraction was committed into the replacement destination parent.");
            CheckRunner.True(!Directory.EnumerateFileSystemEntries(destinationParent).Any(), "The replacement destination retained an uncommitted extraction stage.");
            CheckRunner.True(!Directory.EnumerateFileSystemEntries(heldParent).Any(), "The original destination parent retained an owned extraction stage.");
            CheckRunner.True((await File.ReadAllBytesAsync(archivePath)).SequenceEqual(originalArchive), "Destination-parent refusal changed the source archive.");
            IOException? staleReviewFailure = null;
            try
            {
                await new ArchiveService().ExtractZipAsync(archivePath, target,
                    expectedDestinationParentIdentity: originalParentIdentity);
            }
            catch (IOException error) { staleReviewFailure = error; }
            CheckRunner.True(staleReviewFailure is not null, "ZIP extraction accepted a destination parent replaced after Extract was selected.");
            OnlyChildren(root, "source.zip", "destination", "held-destination");
        });
        await checks.CheckAsync("Archive creation cancellation retains source and removes staging", async () =>
        {
            var root = CaseDirectory(sandbox, "zip-create-cancel");
            var source = Path.Combine(root, "source.bin");
            var destination = Path.Combine(root, "canceled.zip");
            await File.WriteAllBytesAsync(source, Payload(2 * 1024 * 1024 + 13, 5631));
            var before = Hash(source);
            using var cancellation = new CancellationTokenSource();
            var progress = new InlineProgress(value =>
            {
                if (value.CompletedBytes > 0) cancellation.Cancel();
            });
            await CheckRunner.ThrowsAsync<OperationCanceledException>(() => new ArchiveService().CreateZipAsync([source], destination, progress, cancellation.Token));
            CheckRunner.True(cancellation.IsCancellationRequested, "Test did not observe in-flight byte progress");
            CheckRunner.Equal(before, Hash(source), "Canceled creation changed source bytes");
            CheckRunner.True(!File.Exists(destination) && !Directory.Exists(destination), "Canceled creation committed its destination");
            OnlyChildren(root, "source.bin");
        });
        await checks.CheckAsync("Archive extraction cancellation retains archive and removes staging", async () =>
        {
            var root = CaseDirectory(sandbox, "zip-extract-cancel");
            var archive = Path.Combine(root, "input.zip");
            var destination = Path.Combine(root, "canceled");
            WriteZip(archive, [new("large.bin", Payload(2 * 1024 * 1024 + 29, 6584))]);
            var before = Hash(archive);
            using var cancellation = new CancellationTokenSource();
            var progress = new InlineProgress(value =>
            {
                if (value.CompletedBytes > 0) cancellation.Cancel();
            });
            await CheckRunner.ThrowsAsync<OperationCanceledException>(() => new ArchiveService().ExtractZipAsync(archive, destination, progress: progress, cancellationToken: cancellation.Token));
            CheckRunner.True(cancellation.IsCancellationRequested, "Test did not observe in-flight byte progress");
            CheckRunner.Equal(before, Hash(archive), "Canceled extraction changed the source archive");
            CheckRunner.True(!File.Exists(destination) && !Directory.Exists(destination), "Canceled extraction committed its destination");
            OnlyChildren(root, "input.zip");
        });
    }

    private static async Task CheckDuplicatesAsync(CheckRunner checks, string sandbox)
    {
        await checks.CheckAsync("Duplicate scans can collapse hard links and preserve independent copies", async () =>
        {
            var root = CaseDirectory(sandbox, "duplicate-hard-links");
            var scanned = Directory.CreateDirectory(Path.Combine(root, "scanned")).FullName;
            var source = Path.Combine(scanned, "first.txt");
            var alias = Path.Combine(scanned, "second.txt");
            var reference = Path.Combine(root, "reference.txt");
            var copy = Path.Combine(scanned, "third.txt");
            await File.WriteAllTextAsync(source, "fixture content");
            var platform = new DesktopFilePlatform();
            await platform.CreateHardLinkAsync(source, alias, CancellationToken.None);
            await platform.CreateHardLinkAsync(source, reference, CancellationToken.None);
            var finder = new DuplicateFinder();
            var options = new DuplicateOptions(IgnoreHardLinks: true);
            CheckRunner.Equal(0, (await finder.FindAsync([scanned], options)).Groups.Count,
                "Two paths to the same file appeared as independent duplicates.");
            File.Copy(source, copy);
            var ordinary = await finder.FindAsync([scanned]);
            CheckRunner.Equal(3, ordinary.Groups.Single().Paths.Count);
            var distinct = await finder.FindAsync([scanned], options);
            CheckRunner.True(distinct.Groups.Single().Paths.ToHashSet().SetEquals([source, copy]),
                "The independent copy or the stable representative was missing.");
            var targeted = await finder.FindAsync([scanned], options with { ReferenceFiles = [reference] });
            CheckRunner.True(targeted.Groups.Single().Paths.ToHashSet().SetEquals([reference, copy]) &&
                targeted.OutsideReferences.SequenceEqual([reference]),
                "An outside reference did not take precedence over its hard-link aliases.");
        });
        await checks.CheckAsync("Duplicate metadata modes distinguish name, stem and size without hashing", async () =>
        {
            var root = CaseDirectory(sandbox, "duplicate-metadata");
            var child = Directory.CreateDirectory(Path.Combine(root, "child")).FullName;
            var a = Path.Combine(root, "same.txt");
            var b = Path.Combine(child, "SAME.TXT");
            var c = Path.Combine(child, "same.wav");
            var d = Path.Combine(root, "different.txt");
            await File.WriteAllTextAsync(a, "aaa");
            await File.WriteAllTextAsync(b, "bbbb");
            await File.WriteAllTextAsync(c, "ccc");
            await File.WriteAllTextAsync(d, "ddd");
            var finder = new DuplicateFinder();
            async Task<DuplicateResult> Find(DuplicateComparison mode)
            {
                var result = await finder.FindAsync([root], new DuplicateOptions(Comparison: mode));
                CheckRunner.Equal(0L, result.HashedBytes, "A metadata comparison read file contents.");
                CheckRunner.Equal(mode, result.Comparison);
                return result;
            }
            var byName = await Find(DuplicateComparison.Filename);
            CheckRunner.True(byName.Groups.Single().Paths.ToHashSet().SetEquals([a, b]), "Filename comparison did not match case-insensitively.");
            var byStem = await Find(DuplicateComparison.FilenameStem);
            CheckRunner.True(byStem.Groups.Single().Paths.ToHashSet().SetEquals([a, b, c]), "Stem comparison did not ignore extensions.");
            CheckRunner.Equal(0, (await Find(DuplicateComparison.FilenameAndSize)).Groups.Count);
            var bySize = await Find(DuplicateComparison.Size);
            CheckRunner.True(bySize.Groups.Single().Paths.ToHashSet().SetEquals([a, c, d]), "Size comparison did not group equal-length files.");
        });
        await checks.CheckAsync("Duplicate scan groups actual SHA256 content and deduplicates roots", async () =>
        {
            var root = CaseDirectory(sandbox, "duplicates-content");
            Directory.CreateDirectory(Path.Combine(root, "nested"));
            var payload = Payload(1024 * 1024 + 83, 9754);
            var first = Path.Combine(root, "first.bin");
            var second = Path.Combine(root, "nested", "second.bin");
            await File.WriteAllBytesAsync(first, payload);
            await File.WriteAllBytesAsync(second, payload);
            var distinct = (byte[])payload.Clone();
            distinct[^1] ^= 0x55;
            await File.WriteAllBytesAsync(Path.Combine(root, "same-size-distinct.bin"), distinct);
            await File.WriteAllBytesAsync(Path.Combine(root, "empty-one"), []);
            await File.WriteAllBytesAsync(Path.Combine(root, "nested", "empty-two"), []);
            var before = Snapshot(root);
            var result = await new DuplicateFinder().FindAsync([root, first], new DuplicateOptions(MinimumSize: 0));
            CheckRunner.True(!result.Truncated && result.Errors.Count == 0, "Complete duplicate fixture was not fully scanned");
            CheckRunner.Equal(2, result.Groups.Count, "Expected one binary group and one empty-file group");
            var binary = result.Groups.Single(group => group.Length == payload.Length);
            CheckRunner.True(string.Equals(Hash(first), binary.Hash, StringComparison.OrdinalIgnoreCase), "Duplicate group has the wrong SHA256");
            CheckRunner.Equal(2, binary.Paths.Count);
            CheckRunner.True(binary.Paths.ToHashSet(StringComparer.Ordinal).SetEquals([first, second]), "Same-length different content or an overlapping root entered the group");
            CheckRunner.Equal((long)payload.Length, binary.ReclaimableBytes);
            var empty = result.Groups.Single(group => group.Length == 0);
            CheckRunner.Equal(2, empty.Paths.Count);
            CheckRunner.True(string.Equals(Convert.ToHexString(SHA256.HashData(Array.Empty<byte>())), empty.Hash, StringComparison.OrdinalIgnoreCase), "Empty-file hash is incorrect");
            CheckRunner.Equal(5, result.ScannedFiles, "Overlapping roots counted the same file twice");
            AssertSnapshot(before, Snapshot(root), "Duplicate scan modified files");
        });
        await checks.CheckAsync("Duplicate minimum size excludes empty files by default", async () =>
        {
            var root = CaseDirectory(sandbox, "duplicates-empty");
            await File.WriteAllBytesAsync(Path.Combine(root, "one"), []);
            await File.WriteAllBytesAsync(Path.Combine(root, "two"), []);
            var result = await new DuplicateFinder().FindAsync([root]);
            CheckRunner.Equal(0, result.Groups.Count);
            CheckRunner.True(result.Errors.Count == 0 && !result.Truncated, "Empty file filtering reported a scan failure");
        });
        await checks.CheckAsync("Duplicate recursion and entry bounds are enforced", async () =>
        {
            var root = CaseDirectory(sandbox, "duplicates-bounds");
            Directory.CreateDirectory(Path.Combine(root, "nested"));
            await File.WriteAllTextAsync(Path.Combine(root, "one.txt"), "identical content");
            await File.WriteAllTextAsync(Path.Combine(root, "nested", "two.txt"), "identical content");
            var finder = new DuplicateFinder();
            var shallow = await finder.FindAsync([root], new DuplicateOptions(Recursive: false));
            CheckRunner.Equal(0, shallow.Groups.Count, "Nonrecursive scan entered a child directory");
            var recursive = await finder.FindAsync([root]);
            CheckRunner.Equal(1, recursive.Groups.Count);
            var bounded = await finder.FindAsync([root], new DuplicateOptions(MaxEntries: 1));
            CheckRunner.True(bounded.Truncated, "Exceeding the entry bound did not report truncation");
            CheckRunner.True(bounded.ScannedFiles <= 1, "Entry bound was exceeded");
        });
        await checks.CheckAsync("Duplicate scan skips file and directory symlinks", async () =>
        {
            var root = CaseDirectory(sandbox, "duplicates-links");
            var scan = Path.Combine(root, "scan");
            var outside = Path.Combine(root, "outside");
            Directory.CreateDirectory(scan);
            Directory.CreateDirectory(outside);
            await File.WriteAllTextAsync(Path.Combine(scan, "one.txt"), "regular duplicate");
            await File.WriteAllTextAsync(Path.Combine(scan, "two.txt"), "regular duplicate");
            await File.WriteAllTextAsync(Path.Combine(outside, "target.txt"), "regular duplicate");
            var fileLink = Path.Combine(scan, "linked.txt");
            var directoryLink = Path.Combine(scan, "loop");
            try
            {
                File.CreateSymbolicLink(fileLink, Path.Combine(outside, "target.txt"));
                Directory.CreateSymbolicLink(directoryLink, scan);
            }
            catch (Exception error) when (OperatingSystem.IsWindows() &&
                (error.HResult == unchecked((int)0x80070522) ||
                 error is System.ComponentModel.Win32Exception { NativeErrorCode: 1314 }))
            {
                CheckRunner.NotVerified("Windows denied disposable symlink creation (ERROR_PRIVILEGE_NOT_HELD, 1314 / 0x80070522); file and directory symlink exclusion remains unverified on this host.");
            }
            var result = await new DuplicateFinder().FindAsync([scan]);
            CheckRunner.True(!result.Truncated && result.Errors.Count == 0, "Symlink scan did not finish cleanly");
            CheckRunner.Equal(1, result.Groups.Count);
            CheckRunner.Equal(2, result.Groups[0].Paths.Count, "A symlink target was counted as another copy");
            CheckRunner.True(result.SkippedPaths.Contains(fileLink), "File symlink was not reported as skipped");
            CheckRunner.True(result.SkippedPaths.Contains(directoryLink), "Directory symlink was not reported as skipped");
            CheckRunner.True(result.Groups[0].Paths.All(path => path != fileLink && !path.StartsWith(directoryLink + Path.DirectorySeparatorChar, StringComparison.Ordinal)), "Duplicate scan followed a symlink");
        });
        await checks.CheckAsync("Duplicate scan honors cancellation", async () =>
        {
            var root = CaseDirectory(sandbox, "duplicates-cancel");
            await File.WriteAllTextAsync(Path.Combine(root, "one.txt"), "unchanged");
            var before = Snapshot(root);
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            await CheckRunner.ThrowsAsync<OperationCanceledException>(() => new DuplicateFinder().FindAsync([root], cancellationToken: cancellation.Token));
            AssertSnapshot(before, Snapshot(root), "Canceled duplicate scan modified files");
        });
    }

    private static async Task CheckComparisonAsync(CheckRunner checks, string sandbox)
    {
        await checks.CheckAsync("Folder comparison identifies equal missing changed and type conflicts", async () =>
        {
            var fixture = await ComparisonFixtureAsync(sandbox);
            var before = Snapshot(fixture.Root);
            var service = new FolderComparisonService();
            var result = await service.CompareAsync(fixture.Left, fixture.Right);
            CheckRunner.True(result.Errors.Count == 0 && !result.Truncated && result.ComparedContents, "Complete comparison fixture did not produce a content comparison");
            ComparisonState(result, "equal.txt", ComparisonStatus.Equal);
            ComparisonState(result, "different.txt", ComparisonStatus.Different);
            ComparisonState(result, "left-only.txt", ComparisonStatus.LeftOnly);
            ComparisonState(result, "right-only.txt", ComparisonStatus.RightOnly);
            ComparisonState(result, "conflict", ComparisonStatus.TypeConflict);
            ComparisonState(result, "left-folder", ComparisonStatus.LeftOnly);
            AssertSnapshot(before, Snapshot(fixture.Root), "Folder comparison modified source trees");
        });
        await checks.CheckAsync("Folder copy previews exclude replacement unless explicitly requested", async () =>
        {
            var fixture = await ComparisonFixtureAsync(sandbox);
            var before = Snapshot(fixture.Root);
            var service = new FolderComparisonService();
            var result = await service.CompareAsync(fixture.Left, fixture.Right);
            var plan = service.BuildCopyPlan(result, ComparisonDirection.LeftToRight);
            var sources = plan.Requests.SelectMany(request => request.Sources).ToArray();
            CheckRunner.True(sources.Contains(Path.Combine(fixture.Left, "left-only.txt")), "Missing file was not included in copy plan");
            var folder = plan.Requests.Single(request => request.Kind == FileOperationKind.CreateDirectory && request.NewName == "left-folder");
            CheckRunner.Equal(fixture.Right, folder.DestinationDirectory, "Missing folder would be created under the wrong parent");
            CheckRunner.Equal(1, sources.Count(source => source == Path.Combine(fixture.Left, "left-folder", "child.txt")), "Scanned child file is missing or duplicated in the copy plan");
            CheckRunner.True(!sources.Contains(Path.Combine(fixture.Left, "left-folder")), "Copy preview added an unreviewed recursive directory operation");
            CheckRunner.True(!sources.Contains(Path.Combine(fixture.Left, "different.txt")), "Default plan would replace an existing file");
            CheckRunner.True(!sources.Contains(Path.Combine(fixture.Left, "conflict")), "Type conflict entered automatic copy plan");
            CheckRunner.True(!plan.IncludesReplacement && plan.Requests.All(request => request.Collision != CollisionPolicy.Replace), "Default plan allowed replacement");
            CheckRunner.True(plan.Warnings.Any(warning => warning.Contains("conflict", StringComparison.OrdinalIgnoreCase)), "Type conflict was not made visible");
            var explicitPlan = service.BuildCopyPlan(result, ComparisonDirection.LeftToRight, includeChanged: true);
            var changed = explicitPlan.Requests.Where(request => request.Sources.Contains(Path.Combine(fixture.Left, "different.txt"))).ToArray();
            CheckRunner.True(explicitPlan.IncludesReplacement && changed.Length > 0 && changed.All(request => request.Collision == CollisionPolicy.Replace), "Explicit changed-file plan did not request replacement");
            var reverse = service.BuildCopyPlan(result, ComparisonDirection.RightToLeft);
            CheckRunner.True(reverse.Requests.SelectMany(request => request.Sources).Contains(Path.Combine(fixture.Right, "right-only.txt")), "Reverse plan used the wrong side");
            AssertSnapshot(before, Snapshot(fixture.Root), "Copy preview applied changes instead of returning a plan");
        });
        await checks.CheckAsync("Folder copy preview refuses partial or failed comparisons", async () =>
        {
            var fixture = await ComparisonFixtureAsync(sandbox);
            var service = new FolderComparisonService();
            var result = await service.CompareAsync(fixture.Left, fixture.Right);
            Throws<InvalidOperationException>(() => service.BuildCopyPlan(result with { Truncated = true }, ComparisonDirection.LeftToRight));
            Throws<InvalidOperationException>(() => service.BuildCopyPlan(result with { Errors = ["Unverified fixture path"] }, ComparisonDirection.LeftToRight));
        });
        await checks.CheckAsync("Folder comparison detects same length edits with matching timestamps", async () =>
        {
            var root = CaseDirectory(sandbox, "compare-same-stat");
            var left = Path.Combine(root, "left");
            var right = Path.Combine(root, "right");
            Directory.CreateDirectory(left);
            Directory.CreateDirectory(right);
            await File.WriteAllTextAsync(Path.Combine(left, "item.txt"), "AAAA");
            await File.WriteAllTextAsync(Path.Combine(right, "item.txt"), "BBBB");
            var timestamp = new DateTime(2025, 1, 1, 12, 0, 0, DateTimeKind.Utc);
            File.SetLastWriteTimeUtc(Path.Combine(left, "item.txt"), timestamp);
            File.SetLastWriteTimeUtc(Path.Combine(right, "item.txt"), timestamp);
            var result = await new FolderComparisonService().CompareAsync(left, right, new FolderComparisonOptions(CompareContents: true));
            ComparisonState(result, "item.txt", ComparisonStatus.Different);
        });
        await checks.CheckAsync("Folder comparison honors cancellation without changes", async () =>
        {
            var fixture = await ComparisonFixtureAsync(sandbox);
            var before = Snapshot(fixture.Root);
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            await CheckRunner.ThrowsAsync<OperationCanceledException>(() => new FolderComparisonService().CompareAsync(fixture.Left, fixture.Right, cancellationToken: cancellation.Token));
            AssertSnapshot(before, Snapshot(fixture.Root), "Canceled comparison modified source trees");
        });
    }

    private static async Task CheckRenamePlansAsync(CheckRunner checks, string sandbox)
    {
        await checks.CheckAsync("Rename preview preserves extensions and delegates execution to operation owner", async () =>
        {
            var root = CaseDirectory(sandbox, "rename-valid");
            var source = Path.Combine(root, "clip_001.MOV");
            await File.WriteAllTextAsync(source, "Source bytes remain unchanged.");
            var before = Snapshot(root);
            var plan = new BatchRenamePlanner().CreatePlan([source], new RenameOptions(Find: "clip", Replace: "scene", Prefix: "cut_", Suffix: "_edit"));
            CheckRunner.True(plan.IsValid && plan.CanExecute, "Valid rename options produced an invalid preview");
            var item = plan.Items.Single();
            CheckRunner.Equal("cut_scene_001_edit.MOV", item.NewName);
            CheckRunner.Equal(Path.Combine(root, item.NewName), item.Destination);
            var request = plan.ToRequest();
            CheckRunner.Equal(FileOperationKind.BatchRename, request.Kind);
            CheckRunner.Equal(source, request.Sources.Single());
            var mapping = request.Renames!.Single();
            CheckRunner.Equal(source, mapping.Source);
            CheckRunner.Equal(item.NewName, mapping.NewName);
            CheckRunner.Equal(CollisionPolicy.Fail, request.Collision);
            AssertSnapshot(before, Snapshot(root), "Creating a rename plan changed source files");
        });
        await checks.CheckAsync("Rename preview rejects path escape names without changing files", async () =>
        {
            var root = CaseDirectory(sandbox, "rename-escape");
            var source = Path.Combine(root, "clip.txt");
            await File.WriteAllTextAsync(source, "unchanged");
            var before = Snapshot(root);
            var plan = new BatchRenamePlanner().CreatePlan([source], new RenameOptions(Find: "clip", Replace: "../escape"));
            CheckRunner.True(!plan.IsValid && !plan.CanExecute, "A path escape was accepted as a leaf name");
            Throws<InvalidOperationException>(() => plan.ToRequest());
            AssertSnapshot(before, Snapshot(root), "Rejected rename preview changed files");
        });
        await checks.CheckAsync("Rename preview reports malformed regular expressions", async () =>
        {
            var root = CaseDirectory(sandbox, "rename-regex");
            var source = Path.Combine(root, "clip.txt");
            await File.WriteAllTextAsync(source, "unchanged");
            var before = Snapshot(root);
            var plan = new BatchRenamePlanner().CreatePlan([source], new RenameOptions(Find: "[", Replace: "new", UseRegex: true));
            CheckRunner.True(!plan.IsValid, "Malformed regex did not produce a preview error");
            Throws<InvalidOperationException>(() => plan.ToRequest());
            AssertSnapshot(before, Snapshot(root), "Malformed regex preview changed files");
        });
        await checks.CheckAsync("Rename preview rejects duplicate targets", async () =>
        {
            var root = CaseDirectory(sandbox, "rename-duplicate-target");
            var first = Path.Combine(root, "one.txt");
            var second = Path.Combine(root, "two.txt");
            await File.WriteAllTextAsync(first, "first");
            await File.WriteAllTextAsync(second, "second");
            var before = Snapshot(root);
            var plan = new BatchRenamePlanner().CreatePlan([first, second], new RenameOptions(Find: ".+", Replace: "same", UseRegex: true));
            CheckRunner.True(!plan.IsValid && !plan.CanExecute, "Many-to-one rename was accepted");
            Throws<InvalidOperationException>(() => plan.ToRequest());
            AssertSnapshot(before, Snapshot(root), "Collision preview changed source files");
        });
        await checks.CheckAsync("Rename preview retains existing destinations", async () =>
        {
            var root = CaseDirectory(sandbox, "rename-existing");
            var source = Path.Combine(root, "clip.txt");
            await File.WriteAllTextAsync(source, "source");
            await File.WriteAllTextAsync(Path.Combine(root, "taken.txt"), "existing destination");
            var before = Snapshot(root);
            var plan = new BatchRenamePlanner().CreatePlan([source], new RenameOptions(Find: "clip", Replace: "taken"));
            CheckRunner.True(!plan.IsValid, "Existing unselected destination was accepted");
            Throws<InvalidOperationException>(() => plan.ToRequest());
            AssertSnapshot(before, Snapshot(root), "Existing destination was overwritten by a preview");
        });
        await checks.CheckAsync("Rename preview rejects missing sources and no-op execution", async () =>
        {
            var root = CaseDirectory(sandbox, "rename-noop");
            var source = Path.Combine(root, "clip.txt");
            await File.WriteAllTextAsync(source, "unchanged");
            var before = Snapshot(root);
            var planner = new BatchRenamePlanner();
            var noOp = planner.CreatePlan([source], new RenameOptions());
            CheckRunner.True(noOp.IsValid && !noOp.CanExecute, "Unchanged preview must be valid but nonexecutable");
            Throws<InvalidOperationException>(() => noOp.ToRequest());
            var missing = planner.CreatePlan([Path.Combine(root, "missing.txt")], new RenameOptions(Prefix: "new_"));
            CheckRunner.True(!missing.IsValid && !missing.CanExecute, "Missing rename source was accepted");
            Throws<InvalidOperationException>(() => missing.ToRequest());
            AssertSnapshot(before, Snapshot(root), "No-op or missing-source preview changed files");
        });
    }

    private static async Task CheckOperationIntegrationAsync(CheckRunner checks, string sandbox)
    {
        await checks.CheckAsync("Rename preview executes through native operation owner and undo restores bytes", async () =>
        {
            var root = CaseDirectory(sandbox, "rename-native-integration");
            var files = Path.Combine(root, "files");
            Directory.CreateDirectory(files);
            var first = Path.Combine(files, "clip_001.bin");
            var second = Path.Combine(files, "clip_002.txt");
            await File.WriteAllBytesAsync(first, Payload(131101, 7162));
            await File.WriteAllTextAsync(second, "Rename and undo preserve these source bytes.");
            var before = Snapshot(files);
            var plan = new BatchRenamePlanner().CreatePlan([first, second], new RenameOptions(Find: "clip", Replace: "scene", Prefix: "edited_"));
            CheckRunner.True(plan.IsValid && plan.CanExecute, "Integration rename fixture did not produce an executable plan");
            using var operations = new FileOperationService(new DesktopFilePlatform(), Path.Combine(root, "state"));
            var executed = await operations.ExecuteAsync(plan.ToRequest());
            CheckRunner.True(executed.Succeeded && !executed.Canceled && executed.Errors.Count == 0, "Rename operation failed: " + string.Join("; ", executed.Errors));
            var expected = plan.Items.ToDictionary(item => item.NewName, item => before[item.OriginalName], StringComparer.Ordinal);
            AssertSnapshot(expected, Snapshot(files), "Executed rename changed bytes or produced unexpected files");
            CheckRunner.True(plan.Items.All(item => !File.Exists(item.Source) && File.Exists(item.Destination)), "Executed rename did not publish the previewed names");
            CheckRunner.True(operations.CanUndo, "Successful batch rename did not expose undo");
            var undone = await operations.UndoAsync();
            CheckRunner.True(undone.Succeeded && !undone.Canceled && undone.Errors.Count == 0, "Rename undo failed: " + string.Join("; ", undone.Errors));
            AssertSnapshot(before, Snapshot(files), "Rename undo did not restore original names and bytes");
            CheckRunner.True(plan.Items.All(item => File.Exists(item.Source) && !File.Exists(item.Destination)), "Undo left renamed destinations in place");
        });
        await checks.CheckAsync("Folder copy plan executes ordered native operations and preserves destination-only files", async () =>
        {
            var root = CaseDirectory(sandbox, "comparison-native-integration");
            var left = Path.Combine(root, "left");
            var right = Path.Combine(root, "right");
            Directory.CreateDirectory(Path.Combine(left, "nested", "deeper"));
            Directory.CreateDirectory(Path.Combine(left, "nested", "empty-folder"));
            Directory.CreateDirectory(right);
            await File.WriteAllBytesAsync(Path.Combine(left, "nested", "deeper", "child.bin"), Payload(131119, 6589));
            await File.WriteAllBytesAsync(Path.Combine(left, "nested", "empty-file.txt"), []);
            await File.WriteAllTextAsync(Path.Combine(left, "changed.txt"), "New content selected for replacement.");
            await File.WriteAllTextAsync(Path.Combine(right, "changed.txt"), "Previous destination content.");
            await File.WriteAllTextAsync(Path.Combine(left, "common.txt"), "Already identical.");
            await File.WriteAllTextAsync(Path.Combine(right, "common.txt"), "Already identical.");
            var sentinel = Path.Combine(right, "destination-only.txt");
            await File.WriteAllTextAsync(sentinel, "Keep this destination-only file.");
            var sourceBefore = Snapshot(left);
            var sentinelBefore = Hash(sentinel);
            var comparisons = new FolderComparisonService();
            var comparison = await comparisons.CompareAsync(left, right);
            CheckRunner.True(comparison.Errors.Count == 0 && !comparison.Truncated, "Integration fixture comparison was incomplete");
            var plan = comparisons.BuildCopyPlan(comparison, ComparisonDirection.LeftToRight, includeChanged: true);
            CheckRunner.True(plan.IncludesReplacement, "Explicit changed-file replacement was missing");
            CheckRunner.True(plan.Requests.Count > 0 && plan.Requests.All(request => request.Kind is FileOperationKind.CreateDirectory or FileOperationKind.Copy), "Copy plan contains an unexpected mutation");
            using var operations = new FileOperationService(new DesktopFilePlatform(), Path.Combine(root, "state"));
            foreach (var request in plan.Requests)
            {
                var result = await operations.ExecuteAsync(request);
                CheckRunner.True(result.Succeeded && !result.Canceled && result.Errors.Count == 0, $"Planned {request.Kind} failed: {string.Join("; ", result.Errors)}");
            }
            AssertSnapshot(sourceBefore, Snapshot(left), "Executing a copy plan modified the source tree");
            foreach (var item in sourceBefore)
            {
                var destination = Path.Combine(right, item.Key.Replace('/', Path.DirectorySeparatorChar));
                CheckRunner.True(File.Exists(destination), "Planned destination file is missing: " + item.Key);
                CheckRunner.Equal(item.Value, Hash(destination), "Copied or replaced file has different bytes: " + item.Key);
            }
            CheckRunner.True(Directory.Exists(Path.Combine(right, "nested", "empty-folder")), "Missing empty directory was not created");
            CheckRunner.Equal(sentinelBefore, Hash(sentinel), "Destination-only file was changed or removed");
            var after = await comparisons.CompareAsync(left, right);
            CheckRunner.True(after.Errors.Count == 0 && !after.Truncated, "Comparison after ordered copy operations was incomplete");
            CheckRunner.True(after.Items.Where(item => item.LeftPath is not null).All(item => item.Status == ComparisonStatus.Equal), "A source path remains missing or different after the copy plan");
            ComparisonState(after, "destination-only.txt", ComparisonStatus.RightOnly);
        });
    }

    private static async Task RejectArchiveAsync(string root, IReadOnlyList<ZipFixtureEntry> entries, ArchiveLimits? limits = null, CompressionLevel compression = CompressionLevel.NoCompression)
    {
        var archive = Path.Combine(root, "input.zip");
        var destination = Path.Combine(root, "output");
        WriteZip(archive, entries, compression);
        var before = Hash(archive);
        var service = new ArchiveService();
        await CheckRunner.ThrowsAsync<InvalidDataException>(() => service.ListAsync(archive, limits));
        await CheckRunner.ThrowsAsync<InvalidDataException>(() => service.ExtractZipAsync(archive, destination, limits));
        CheckRunner.Equal(before, Hash(archive), "Rejected archive was modified");
        CheckRunner.True(!Directory.Exists(destination) && !File.Exists(destination), "Rejected archive committed an output");
        OnlyChildren(root, "input.zip");
    }

    private static async Task<(string Root, string Left, string Right)> ComparisonFixtureAsync(string sandbox)
    {
        var root = CaseDirectory(sandbox, "comparison");
        var left = Path.Combine(root, "left");
        var right = Path.Combine(root, "right");
        Directory.CreateDirectory(left);
        Directory.CreateDirectory(right);
        Directory.CreateDirectory(Path.Combine(left, "left-folder"));
        Directory.CreateDirectory(Path.Combine(right, "conflict"));
        await File.WriteAllTextAsync(Path.Combine(left, "equal.txt"), "same bytes");
        await File.WriteAllTextAsync(Path.Combine(right, "equal.txt"), "same bytes");
        File.SetLastWriteTimeUtc(Path.Combine(left, "equal.txt"), new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        File.SetLastWriteTimeUtc(Path.Combine(right, "equal.txt"), new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        await File.WriteAllTextAsync(Path.Combine(left, "different.txt"), "1234");
        await File.WriteAllTextAsync(Path.Combine(right, "different.txt"), "4321");
        await File.WriteAllTextAsync(Path.Combine(left, "left-only.txt"), "left");
        await File.WriteAllTextAsync(Path.Combine(right, "right-only.txt"), "right");
        await File.WriteAllTextAsync(Path.Combine(left, "left-folder", "child.txt"), "nested left");
        await File.WriteAllTextAsync(Path.Combine(left, "conflict"), "file opposed to directory");
        await File.WriteAllTextAsync(Path.Combine(right, "conflict", "child.txt"), "nested right");
        return (root, left, right);
    }

    private static void ComparisonState(FolderComparisonResult result, string relativePath, ComparisonStatus expected)
    {
        var item = result.Items.Single(entry => entry.RelativePath.Replace('\\', '/') == relativePath);
        CheckRunner.Equal(expected, item.Status, $"Unexpected comparison state for {relativePath}");
    }

    private static string CaseDirectory(string sandbox, string name)
    {
        var root = Path.Combine(Path.GetFullPath(sandbox), $"{name}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        return root;
    }

    private static byte[] Bytes(string value) => Encoding.UTF8.GetBytes(value);

    private static byte[] Payload(int length, int seed)
    {
        var bytes = new byte[length];
        new Random(seed).NextBytes(bytes);
        return bytes;
    }

    private static string Hash(string path)
    {
        using var input = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(input));
    }

    private static SortedDictionary<string, string> Snapshot(string root)
    {
        var result = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            result.Add(Path.GetRelativePath(root, file).Replace('\\', '/'), Hash(file));
        return result;
    }

    private static void AssertSnapshot(IReadOnlyDictionary<string, string> expected, IReadOnlyDictionary<string, string> actual, string message)
    {
        CheckRunner.Equal(expected.Count, actual.Count, message);
        foreach (var item in expected)
            CheckRunner.True(actual.TryGetValue(item.Key, out var value) && value == item.Value, $"{message}: {item.Key}");
    }

    private static void OnlyChildren(string root, params string[] expected)
    {
        var actual = Directory.EnumerateFileSystemEntries(root).Select(Path.GetFileName).ToHashSet(StringComparer.Ordinal);
        CheckRunner.True(actual.SetEquals(expected), $"Unexpected output or retained staging inside fixture: {string.Join(", ", actual)}");
    }

    private static void WriteZip(string path, IReadOnlyList<ZipFixtureEntry> entries, CompressionLevel compression = CompressionLevel.NoCompression)
    {
        using var output = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
        using var archive = new ZipArchive(output, ZipArchiveMode.Create);
        foreach (var item in entries)
        {
            var entry = archive.CreateEntry(item.Name, compression);
            entry.LastWriteTime = new DateTimeOffset(2025, 1, 2, 3, 4, 6, TimeSpan.Zero);
            if (item.ExternalAttributes.HasValue) entry.ExternalAttributes = item.ExternalAttributes.Value;
            using var content = entry.Open();
            content.Write(item.Content);
        }
    }

    private static void Throws<T>(Action action) where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        throw new InvalidOperationException($"Expected {typeof(T).Name}");
    }

    private sealed record ZipFixtureEntry(string Name, byte[] Content, int? ExternalAttributes = null);
    private sealed class InlineProgress(Action<ToolProgress> callback) : IProgress<ToolProgress>
    {
        public void Report(ToolProgress value) => callback(value);
    }
}
