using System.Buffers.Binary;
using System.Security.Cryptography;
using OpusMac.Core;
using OpusMac.Core.Integration;
using OpusMac.Core.Operations;
using OpusMac.Core.Platform;

namespace OpusMac.Checks.Suites;

public sealed class WindowsClipboardIntentChecks : ICheckSuite
{
    public async Task RunAsync(CheckRunner checks, string sandbox)
    {
        await checks.CheckAsync("Clipboard intent: exact Windows Explorer COPY and MOVE DWORDs decode", () =>
        {
            foreach (var effect in new[] { WindowsClipboardDropIntent.CopyEffect, WindowsClipboardDropIntent.MoveEffect })
            {
                var data = new byte[4];
                BinaryPrimitives.WriteUInt32LittleEndian(data, effect);
                CheckRunner.True(WindowsClipboardDropIntent.TryParsePreferredEffect(data, out var decoded), "Valid effect was rejected.");
                CheckRunner.Equal(effect, decoded);
            }
            var explorerCopy = new byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(explorerCopy, WindowsClipboardDropIntent.CopyOrLinkEffect);
            CheckRunner.True(WindowsClipboardDropIntent.TryParsePreferredEffect(explorerCopy, out var normalizedCopy),
                "Explorer's COPY|LINK preference was rejected.");
            CheckRunner.Equal(WindowsClipboardDropIntent.CopyEffect, normalizedCopy);
            return Task.CompletedTask;
        });

        await checks.CheckAsync("Clipboard intent: duplicates and overlapping folder selections cannot authorize Move", () =>
        {
            var folder = Path.Combine(sandbox, "Source", "Pictures");
            var child = Path.Combine(folder, "résumé 東京.txt");
            var duplicate = new WindowsClipboardDropSnapshot(45, [child, child], WindowsClipboardDropIntent.MoveEffect);
            CheckRunner.True(!duplicate.IsMoveFor([child, child], 45),
                "Duplicate paths were admitted as two independent Move sources.");
            var overlap = new WindowsClipboardDropSnapshot(46, [folder, child], WindowsClipboardDropIntent.MoveEffect);
            CheckRunner.True(!overlap.IsMoveFor([folder, child], 46),
                "A folder and selected descendant were admitted for destructive Move.");
            var reverse = new WindowsClipboardDropSnapshot(47, [child, folder], WindowsClipboardDropIntent.MoveEffect);
            CheckRunner.True(!reverse.IsMoveFor([child, folder], 47),
                "A selected descendant and its folder were admitted in reverse order.");
            // Windows directories are generally case-insensitive; the
            // clipboard spelling may differ even when paths overlap.
            var caseVariantFolder = Path.Combine(sandbox, "source", "PICTURES");
            var caseVariantChild = Path.Combine(caseVariantFolder, "résumé 東京.txt");
            var caseDuplicate = new WindowsClipboardDropSnapshot(49,
                [folder, caseVariantFolder], WindowsClipboardDropIntent.MoveEffect);
            CheckRunner.True(!caseDuplicate.IsMoveFor([folder, caseVariantFolder], 49),
                "Case-variant paths were admitted as separate Move sources.");
            var caseOverlap = new WindowsClipboardDropSnapshot(50,
                [folder, caseVariantChild], WindowsClipboardDropIntent.MoveEffect);
            CheckRunner.True(!caseOverlap.IsMoveFor([folder, caseVariantChild], 50),
                "Case-variant folder/descendant paths were admitted for Move.");
            var caseCopyOverlap = new WindowsClipboardDropSnapshot(51,
                [folder, caseVariantChild], WindowsClipboardDropIntent.CopyEffect);
            CheckRunner.Equal(WindowsClipboardTransferDecision.Refuse,
                caseCopyOverlap.DecideFor([folder, caseVariantChild], 51),
                "Case-variant folder/descendant paths were admitted for Copy.");
            var independent = new WindowsClipboardDropSnapshot(48,
                [child, Path.Combine(sandbox, "Elsewhere", "other.txt")], WindowsClipboardDropIntent.MoveEffect);
            CheckRunner.True(independent.IsMoveFor(independent.Paths, 48),
                "Independent, unchanged absolute paths were rejected.");
            return Task.CompletedTask;
        });

        await checks.CheckAsync("Clipboard intent: absent, malformed and ambiguous effects cannot imply Move", () =>
        {
            foreach (var data in new byte[][] { [], [2], [2, 0, 0], [0, 0, 0, 0], [3, 0, 0, 0], [4, 0, 0, 0], [6, 0, 0, 0], [7, 0, 0, 0], [0, 0, 0, 2], [3, 0, 0, 0, 0, 0, 0, 0] })
            {
                CheckRunner.True(!WindowsClipboardDropIntent.TryParsePreferredEffect(data, out _), "Invalid or ambiguous effect was admitted.");
            }
            return Task.CompletedTask;
        });

        await checks.CheckAsync("Clipboard intent: Move requires unchanged sequence, ordered Unicode paths and explicit Move", () =>
        {
            var a = Path.Combine(sandbox, "Source", "résumé 東京.txt");
            var b = Path.Combine(sandbox, "Source", "another.txt");
            var move = new WindowsClipboardDropSnapshot(109, [a, b], WindowsClipboardDropIntent.MoveEffect);
            CheckRunner.True(move.IsMoveFor([a, b], 109), "Matching native file selection was rejected.");
            CheckRunner.True(!move.IsMoveFor([a, b], 110), "Changed clipboard sequence was accepted.");
            CheckRunner.True(!move.IsMoveFor([b, a], 109), "Reordered file selection was accepted.");
            CheckRunner.True(!move.IsMoveFor([a], 109), "Partial file selection was accepted.");
            CheckRunner.True(!move.IsMoveFor([a, Path.Combine(sandbox, "Other", "another.txt")], 109), "Wrong file path was accepted.");
            CheckRunner.True(!move.IsMoveFor(["", b], 109), "Missing file path was accepted.");
            CheckRunner.True(!move.IsMoveFor(["not-an-absolute-path", b], 109), "Relative file path was accepted.");
            CheckRunner.True(!move.IsMoveFor([a + "\0", b], 109), "Malformed file path was accepted.");
            CheckRunner.True(!new WindowsClipboardDropSnapshot(0, [a, b], WindowsClipboardDropIntent.MoveEffect).IsMoveFor([a, b], 0),
                "Missing sequence was accepted.");
            CheckRunner.True(!new WindowsClipboardDropSnapshot(109, [a, b], WindowsClipboardDropIntent.CopyEffect).IsMoveFor([a, b], 109),
                "COPY was incorrectly promoted to Move.");
            return Task.CompletedTask;
        });

        await checks.CheckAsync("Clipboard intent: native Windows snapshot is read-only and self-consistent", () =>
        {
            if (!OperatingSystem.IsWindows())
            {
                CheckRunner.NotVerified("Win32 clipboard API is unavailable on this platform.");
            }

            // This only observes the user's current clipboard. It does not set
            // clipboard data, create a Cut, or disclose its file paths in a receipt.
            var sequenceBefore = WindowsClipboardDropIntent.CurrentSequence;
            var snapshot = WindowsClipboardDropIntent.TryCapture();
            var sequenceAfter = WindowsClipboardDropIntent.CurrentSequence;
            CheckRunner.Equal(sequenceBefore, sequenceAfter,
                "The clipboard changed during the observation; this cannot qualify a stable snapshot.");
            if (snapshot is not null)
            {
                CheckRunner.Equal(sequenceAfter, snapshot.Sequence,
                    "A snapshot must reflect the observed clipboard sequence.");
                CheckRunner.True(snapshot.Paths.Count > 0,
                    "A snapshot without a complete file list was accepted.");
                CheckRunner.True(snapshot.PreferredEffect is WindowsClipboardDropIntent.CopyEffect or WindowsClipboardDropIntent.MoveEffect,
                    "A native snapshot contains an unsupported effect.");
                foreach (var path in snapshot.Paths)
                {
                    CheckRunner.True(!string.IsNullOrWhiteSpace(path) && Path.IsPathFullyQualified(path),
                        "A native snapshot contains an invalid path.");
                }
            }
            return Task.CompletedTask;
        });

        await checks.CheckAsync("Clipboard intent: external Move decision reaches native journal and cold-reopened Undo", async () =>
        {
            if (!OperatingSystem.IsWindows())
                CheckRunner.NotVerified("The Windows native file operation is not available on this platform.");
            // Use a unique disposable state/profile and source, not the live OS clipboard.
            var root = Path.Combine(sandbox, "native-move-" + Guid.NewGuid().ToString("N"));
            var from = Path.Combine(root, "from");
            var to = Path.Combine(root, "to");
            var state = Path.Combine(root, "state");
            Directory.CreateDirectory(from);
            Directory.CreateDirectory(to);
            var source = Path.Combine(from, "résumé 東京.txt");
            var destination = Path.Combine(to, Path.GetFileName(source));
            var sentinel = Path.Combine(to, "existing-sentinel.txt");
            var collidingSource = Path.Combine(from, Path.GetFileName(sentinel));
            await File.WriteAllTextAsync(source, "Native clipboard Move qualification — Unicode fixture");
            await File.WriteAllTextAsync(sentinel, "unrelated file must stay unchanged");
            await File.WriteAllTextAsync(collidingSource, "source must survive a collision refusal");
            var originalHash = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(source)));
            var sentinelHash = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(sentinel)));
            var collidingSourceHash = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(collidingSource)));
            var captured = new WindowsClipboardDropSnapshot(89, [source], WindowsClipboardDropIntent.MoveEffect);
            CheckRunner.Equal(WindowsClipboardTransferDecision.Move, captured.DecideFor([source], 89));
            CheckRunner.Equal(WindowsClipboardTransferDecision.Refuse, captured.DecideFor([source], 90),
                "A stale external Cut was silently accepted.");
            CheckRunner.Equal(WindowsClipboardTransferDecision.Refuse,
                captured.DecideFor([Path.Combine(from, "other.txt")], 89), "A changed source was accepted.");
            var copy = new WindowsClipboardDropSnapshot(91, [source], WindowsClipboardDropIntent.CopyEffect);
            CheckRunner.Equal(WindowsClipboardTransferDecision.Copy, copy.DecideFor([source], 91));
            CheckRunner.Equal(WindowsClipboardTransferDecision.Refuse, copy.DecideFor([source], 92),
                "A stale Copy was silently accepted.");
            using (var collisionOwner = new FileOperationService(new DesktopFilePlatform(), Path.Combine(root, "collision-state")))
            {
                var collision = await collisionOwner.ExecuteAsync(new(FileOperationKind.Move, [collidingSource], to));
                CheckRunner.True(!collision.Succeeded && collision.Changes.Count == 0,
                    "A colliding native Move unexpectedly succeeded.");
                CheckRunner.Equal(collidingSourceHash,
                    Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(collidingSource))));
                CheckRunner.Equal(sentinelHash,
                    Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(sentinel))));
            }
            string operationId;
            using (var owner = new FileOperationService(new DesktopFilePlatform(), state))
            {
                var decision = captured.DecideFor([source], 89);
                var result = await owner.ExecuteAsync(new(
                    decision == WindowsClipboardTransferDecision.Move ? FileOperationKind.Move : FileOperationKind.Copy,
                    [source], to));
                CheckRunner.True(result.Succeeded && result.Changes.Count == 1,
                    "Native Move was not committed: " + string.Join(" | ", result.Errors));
                operationId = result.Id;
                CheckRunner.True(!File.Exists(source) && File.Exists(destination),
                    "Native Move left the original source or lost its destination.");
                CheckRunner.Equal(originalHash, Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(destination))));
            }
            using (var reopened = new FileOperationService(new DesktopFilePlatform(), state))
            {
                CheckRunner.True(reopened.CanUndo && reopened.History.Any(entry => entry.Id == operationId && entry.CanUndo),
                    "The committed Move was not recoverable after a cold journal reopen.");
                var undone = await reopened.UndoAsync();
                CheckRunner.True(undone.Succeeded, "Cold-reopened native Undo failed: " + string.Join(" | ", undone.Errors));
            }
            using (var reopenedAgain = new FileOperationService(new DesktopFilePlatform(), state))
            {
                CheckRunner.True(reopenedAgain.History.Any(entry => entry.Id == operationId &&
                        entry.Status == "Undone" && !entry.CanUndo),
                    "A second cold reopen lost the settled Undo state.");
                CheckRunner.True(!File.Exists(destination) && File.Exists(source),
                    "Undo left a second copy or removed the restored source.");
                CheckRunner.Equal(originalHash, Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(source))));
                CheckRunner.Equal(sentinelHash, Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(sentinel))));
            }
        });
        await checks.CheckAsync("Clipboard intent: Explorer Copy|Link becomes native Copy, survives reopen and Undo", async () =>
        {
            if (!OperatingSystem.IsWindows())
                CheckRunner.NotVerified("The Windows native file operation is unavailable.");
            var root = Path.Combine(sandbox, "native-copy-" + Guid.NewGuid().ToString("N"));
            var from = Path.Combine(root, "from");
            var to = Path.Combine(root, "to");
            var state = Path.Combine(root, "state");
            Directory.CreateDirectory(from);
            Directory.CreateDirectory(to);
            var source = Path.Combine(from, "Explorer Copy – résumé 東京.txt");
            var destination = Path.Combine(to, Path.GetFileName(source));
            var sentinel = Path.Combine(to, "sentinel.txt");
            await File.WriteAllTextAsync(source, "Explorer Copy|Link preference keeps source immutable.");
            await File.WriteAllTextAsync(sentinel, "Preexisting destination fixture stays unchanged.");
            var sourceHash = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(source)));
            var sentinelHash = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(sentinel)));
            var bytes = new byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(bytes, WindowsClipboardDropIntent.CopyOrLinkEffect);
            CheckRunner.True(WindowsClipboardDropIntent.TryParsePreferredEffect(bytes, out var normalized),
                "A real Explorer Copy|Link DWORD must be recognized as Copy.");
            CheckRunner.Equal(WindowsClipboardDropIntent.CopyEffect, normalized);
            var capture = new WindowsClipboardDropSnapshot(83, [source], normalized);
            CheckRunner.Equal(WindowsClipboardTransferDecision.Copy, capture.DecideFor([source], 83));
            CheckRunner.Equal(WindowsClipboardTransferDecision.Refuse,
                capture.DecideIfCurrent(new WindowsClipboardDropSnapshot(84, [source], normalized)),
                "A newer clipboard selection was accepted after confirmation.");
            CheckRunner.Equal(WindowsClipboardTransferDecision.Refuse,
                capture.DecideIfCurrent(new WindowsClipboardDropSnapshot(83, [source], WindowsClipboardDropIntent.MoveEffect)),
                "Copy silently became Move after confirmation.");

            string operationId;
            using (var owner = new FileOperationService(new DesktopFilePlatform(), state))
            {
                var decision = capture.DecideIfCurrent(new WindowsClipboardDropSnapshot(83, [source], normalized));
                CheckRunner.Equal(WindowsClipboardTransferDecision.Copy, decision);
                var result = await owner.ExecuteAsync(new(
                    decision == WindowsClipboardTransferDecision.Move ? FileOperationKind.Move : FileOperationKind.Copy,
                    [source], to));
                CheckRunner.True(result.Succeeded && result.Changes.Count == 1,
                    "Native Copy failed: " + string.Join(" | ", result.Errors));
                operationId = result.Id;
                CheckRunner.True(File.Exists(source) && File.Exists(destination),
                    "Copy removed the source or failed to publish the destination.");
                CheckRunner.Equal(sourceHash, Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(source))));
                CheckRunner.Equal(sourceHash, Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(destination))));
                CheckRunner.Equal(sentinelHash, Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(sentinel))));
            }
            using (var reopened = new FileOperationService(new DesktopFilePlatform(), state))
            {
                CheckRunner.True(reopened.CanUndo && reopened.History.Any(entry => entry.Id == operationId && entry.CanUndo),
                    "Native Copy journal was not retained after cold reopen.");
                var undone = await reopened.UndoAsync();
                CheckRunner.True(undone.Succeeded, "Cold-reopened Copy Undo failed: " + string.Join(" | ", undone.Errors));
            }
            using (var reopenedAgain = new FileOperationService(new DesktopFilePlatform(), state))
            {
                CheckRunner.True(reopenedAgain.History.Any(entry => entry.Id == operationId && entry.Status == "Undone" && !entry.CanUndo),
                    "The second reopen lost Copy Undo's settled state.");
                CheckRunner.True(File.Exists(source) && !File.Exists(destination),
                    "Copy Undo must preserve the original and remove only the published copy.");
                CheckRunner.Equal(sourceHash, Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(source))));
                CheckRunner.Equal(sentinelHash, Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(sentinel))));
            }
        });
        await checks.CheckAsync("Clipboard intent: multi-item Unicode file and folder Copy survives journal reopen and Undo", async () =>
        {
            if (!OperatingSystem.IsWindows())
                CheckRunner.NotVerified("Native Windows filesystem qualification requires Windows.");
            var root = Path.Combine(sandbox, "native-multicopy-" + Guid.NewGuid().ToString("N"));
            var from = Path.Combine(root, "from");
            var to = Path.Combine(root, "to");
            var state = Path.Combine(root, "state");
            var sourceFolder = Path.Combine(from, "Images 東京");
            Directory.CreateDirectory(sourceFolder);
            Directory.CreateDirectory(to);
            var sourceFile = Path.Combine(from, "résumé 東京.txt");
            var sourceNested = Path.Combine(sourceFolder, "subfolder", "original.txt");
            Directory.CreateDirectory(Path.GetDirectoryName(sourceNested)!);
            var sentinel = Path.Combine(to, "unrelated-sentinel.txt");
            await File.WriteAllTextAsync(sourceFile, "Multi-selection source must not be removed by Copy.");
            await File.WriteAllTextAsync(sourceNested, "Folder child retained with Unicode parent.");
            await File.WriteAllTextAsync(sentinel, "Unrelated destination stays byte-identical.");
            var sourceHash = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(sourceFile)));
            var nestedHash = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(sourceNested)));
            var sentinelHash = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(sentinel)));
            var selectedPaths = new[] { sourceFile, sourceFolder };
            var capture = new WindowsClipboardDropSnapshot(103, selectedPaths, WindowsClipboardDropIntent.CopyEffect);
            CheckRunner.Equal(WindowsClipboardTransferDecision.Copy, capture.DecideFor(selectedPaths, 103));
            CheckRunner.Equal(WindowsClipboardTransferDecision.Refuse, capture.DecideFor([sourceFolder, sourceFile], 103),
                "A reordered clipboard selection was accepted.");
            CheckRunner.Equal(WindowsClipboardTransferDecision.Refuse,
                new WindowsClipboardDropSnapshot(104, [sourceFolder, sourceNested], WindowsClipboardDropIntent.CopyEffect)
                    .DecideFor([sourceFolder, sourceNested], 104),
                "Clipboard accepted both a folder and its child as independent sources.");
            var copiedFile = Path.Combine(to, Path.GetFileName(sourceFile));
            var copiedFolder = Path.Combine(to, Path.GetFileName(sourceFolder));
            var copiedNested = Path.Combine(copiedFolder, "subfolder", "original.txt");
            string operationId;
            using (var owner = new FileOperationService(new DesktopFilePlatform(), state))
            {
                var decision = capture.DecideIfCurrent(new WindowsClipboardDropSnapshot(103, selectedPaths, WindowsClipboardDropIntent.CopyEffect));
                CheckRunner.Equal(WindowsClipboardTransferDecision.Copy, decision);
                var result = await owner.ExecuteAsync(new(FileOperationKind.Copy, selectedPaths, to));
                CheckRunner.True(result.Succeeded, "Multi-item native Copy failed: " + string.Join(" | ", result.Errors));
                CheckRunner.True(result.Changes.Count >= 2, "Multi-item Copy did not record both independent sources.");
                operationId = result.Id;
                CheckRunner.True(File.Exists(sourceFile) && File.Exists(sourceNested) && File.Exists(copiedFile) && File.Exists(copiedNested),
                    "A source disappeared or a copied item was missing.");
                CheckRunner.Equal(sourceHash, Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(copiedFile))));
                CheckRunner.Equal(nestedHash, Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(copiedNested))));
                CheckRunner.Equal(sentinelHash, Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(sentinel))));
            }
            using (var reopened = new FileOperationService(new DesktopFilePlatform(), state))
            {
                CheckRunner.True(reopened.CanUndo && reopened.History.Any(entry => entry.Id == operationId && entry.CanUndo),
                    "Multi-item Copy was not undoable after journal cold reopen.");
                var undone = await reopened.UndoAsync();
                CheckRunner.True(undone.Succeeded, "Multi-item Copy Undo failed: " + string.Join(" | ", undone.Errors));
            }
            using (var reopenedAgain = new FileOperationService(new DesktopFilePlatform(), state))
            {
                CheckRunner.True(reopenedAgain.History.Any(entry => entry.Id == operationId && entry.Status == "Undone" && !entry.CanUndo),
                    "A second cold reopen lost the multi-item Undo state.");
                CheckRunner.True(!File.Exists(copiedFile) && !Directory.Exists(copiedFolder),
                    "Undo failed to remove one or more copied destinations.");
                CheckRunner.True(File.Exists(sourceFile) && File.Exists(sourceNested),
                    "Undo destroyed a source item.");
                CheckRunner.Equal(sourceHash, Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(sourceFile))));
                CheckRunner.Equal(nestedHash, Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(sourceNested))));
                CheckRunner.Equal(sentinelHash, Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(sentinel))));
            }
        });
        await checks.CheckAsync("Clipboard intent: multi-item Unicode file and folder Move survives journal reopen and Undo", async () =>
        {
            if (!OperatingSystem.IsWindows())
                CheckRunner.NotVerified("Native Windows filesystem qualification requires Windows.");
            var root = Path.Combine(sandbox, "native-multimove-" + Guid.NewGuid().ToString("N"));
            var from = Path.Combine(root, "from");
            var to = Path.Combine(root, "to");
            var state = Path.Combine(root, "state");
            var sourceFolder = Path.Combine(from, "Archives 東京");
            Directory.CreateDirectory(sourceFolder);
            Directory.CreateDirectory(to);
            var sourceFile = Path.Combine(from, "résumé 東京.txt");
            var nestedSource = Path.Combine(sourceFolder, "2026", "original.txt");
            Directory.CreateDirectory(Path.GetDirectoryName(nestedSource)!);
            var sentinel = Path.Combine(to, "existing-sentinel.txt");
            await File.WriteAllTextAsync(sourceFile, "Move two independent Unicode sources together.");
            await File.WriteAllTextAsync(nestedSource, "Folder payload must be restored after Undo.");
            await File.WriteAllTextAsync(sentinel, "Never mutate this unrelated destination.");
            var fileHash = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(sourceFile)));
            var nestedHash = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(nestedSource)));
            var sentinelHash = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(sentinel)));
            var selections = new[] { sourceFile, sourceFolder };
            var approved = new WindowsClipboardDropSnapshot(105, selections, WindowsClipboardDropIntent.MoveEffect);
            CheckRunner.Equal(WindowsClipboardTransferDecision.Move,
                approved.DecideIfCurrent(new WindowsClipboardDropSnapshot(105, selections, WindowsClipboardDropIntent.MoveEffect)));
            CheckRunner.Equal(WindowsClipboardTransferDecision.Refuse,
                approved.DecideIfCurrent(new WindowsClipboardDropSnapshot(106, selections, WindowsClipboardDropIntent.MoveEffect)),
                "Changed clipboard sequence was admitted for multi-source Move.");
            CheckRunner.Equal(WindowsClipboardTransferDecision.Refuse,
                approved.DecideFor([sourceFolder, sourceFile], 105),
                "Reordered multi-source Move was admitted.");
            var destinationFile = Path.Combine(to, Path.GetFileName(sourceFile));
            var destinationFolder = Path.Combine(to, Path.GetFileName(sourceFolder));
            var destinationNested = Path.Combine(destinationFolder, "2026", "original.txt");
            string operationId;
            using (var service = new FileOperationService(new DesktopFilePlatform(), state))
            {
                var result = await service.ExecuteAsync(new(FileOperationKind.Move, selections, to));
                CheckRunner.True(result.Succeeded && result.Changes.Count >= 2,
                    "Multi-item native Move failed: " + string.Join(" | ", result.Errors));
                operationId = result.Id;
                CheckRunner.True(!File.Exists(sourceFile) && !Directory.Exists(sourceFolder) &&
                                 File.Exists(destinationFile) && File.Exists(destinationNested),
                    "Move left a source behind or omitted a destination.");
                CheckRunner.Equal(fileHash, Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(destinationFile))));
                CheckRunner.Equal(nestedHash, Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(destinationNested))));
                CheckRunner.Equal(sentinelHash, Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(sentinel))));
            }
            using (var reopened = new FileOperationService(new DesktopFilePlatform(), state))
            {
                CheckRunner.True(reopened.CanUndo && reopened.History.Any(entry => entry.Id == operationId && entry.CanUndo),
                    "Multi-source Move was not undoable after cold journal reopen.");
                var undone = await reopened.UndoAsync();
                CheckRunner.True(undone.Succeeded, "Multi-source Move Undo failed: " + string.Join(" | ", undone.Errors));
            }
            using (var reopenedAgain = new FileOperationService(new DesktopFilePlatform(), state))
            {
                CheckRunner.True(reopenedAgain.History.Any(entry => entry.Id == operationId && entry.Status == "Undone" && !entry.CanUndo),
                    "A second cold reopen lost the multi-source Move's settled Undo status.");
                CheckRunner.True(File.Exists(sourceFile) && File.Exists(nestedSource) &&
                                 !File.Exists(destinationFile) && !Directory.Exists(destinationFolder),
                    "Multi-source Move Undo left a destination or lost a source.");
                CheckRunner.Equal(fileHash, Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(sourceFile))));
                CheckRunner.Equal(nestedHash, Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(nestedSource))));
                CheckRunner.Equal(sentinelHash, Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(sentinel))));
            }
        });
        await checks.CheckAsync("Clipboard intent: Explorer-style Copy collision refuses without overwriting on reopen", async () =>
        {
            if (!OperatingSystem.IsWindows())
                CheckRunner.NotVerified("Native Windows file operation qualification requires Windows.");
            var root = Path.Combine(sandbox, "native-copy-collision-" + Guid.NewGuid().ToString("N"));
            var from = Path.Combine(root, "from");
            var to = Path.Combine(root, "to");
            var state = Path.Combine(root, "state");
            Directory.CreateDirectory(from);
            Directory.CreateDirectory(to);
            var source = Path.Combine(from, "collision – résumé 東京.txt");
            var destination = Path.Combine(to, Path.GetFileName(source));
            var sentinel = Path.Combine(to, "unrelated-sentinel.txt");
            await File.WriteAllTextAsync(source, "The clipboard source must survive a rejected Copy.");
            await File.WriteAllTextAsync(destination, "This preexisting destination must never be overwritten.");
            await File.WriteAllTextAsync(sentinel, "This unrelated destination must not change.");
            var sourceHash = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(source)));
            var destinationHash = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(destination)));
            var sentinelHash = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(sentinel)));
            var payload = new byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(payload, WindowsClipboardDropIntent.CopyOrLinkEffect);
            CheckRunner.True(WindowsClipboardDropIntent.TryParsePreferredEffect(payload, out var copyEffect),
                "Explorer Copy|Link preference was not recognized.");
            var captured = new WindowsClipboardDropSnapshot(117, [source], copyEffect);
            CheckRunner.Equal(WindowsClipboardTransferDecision.Refuse,
                captured.DecideIfCurrent(new WindowsClipboardDropSnapshot(118, [source], copyEffect)),
                "Stale clipboard selection was admitted.");
            CheckRunner.Equal(WindowsClipboardTransferDecision.Refuse,
                captured.DecideIfCurrent(new WindowsClipboardDropSnapshot(117, [source], WindowsClipboardDropIntent.MoveEffect)),
                "Changed clipboard effect was admitted.");
            CheckRunner.Equal(WindowsClipboardTransferDecision.Copy,
                captured.DecideIfCurrent(new WindowsClipboardDropSnapshot(117, [source], copyEffect)));
            string operationId;
            using (var owner = new FileOperationService(new DesktopFilePlatform(), state))
            {
                var refused = await owner.ExecuteAsync(new(FileOperationKind.Copy, [source], to,
                    Collision: CollisionPolicy.Fail));
                operationId = refused.Id;
                CheckRunner.True(!refused.Succeeded && refused.Changes.Count == 0,
                    "Native Copy overwrote an existing destination despite Fail policy.");
                CheckRunner.True(refused.Errors.Count > 0, "Collision refusal did not explain the failure.");
                CheckRunner.True(!owner.CanUndo, "A rejected Copy was advertised as undoable.");
            }
            using (var reopened = new FileOperationService(new DesktopFilePlatform(), state))
            {
                CheckRunner.True(!reopened.CanUndo &&
                    reopened.History.Any(item => item.Id == operationId && item.Status == "Failed" && !item.CanUndo),
                    "Cold reopen changed a rejected Copy into a replayable operation.");
                CheckRunner.True(File.Exists(source) && File.Exists(destination) && File.Exists(sentinel),
                    "A collision refusal removed a source or destination.");
                CheckRunner.Equal(sourceHash, Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(source))));
                CheckRunner.Equal(destinationHash, Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(destination))));
                CheckRunner.Equal(sentinelHash, Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(sentinel))));
            }
        });
    }
}
