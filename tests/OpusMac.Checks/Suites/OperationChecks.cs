using System.Text.Json.Nodes;
using OpusMac.Core;
using OpusMac.Core.Operations;
using OpusMac.Core.Platform;
using OpusMac.Core.Search;
using OpusMac.Core.Tools;

namespace OpusMac.Checks.Suites;

public sealed class OperationChecks : ICheckSuite
{
    public async Task RunAsync(CheckRunner checks, string sandbox)
    {
        async Task Run(string name, Func<Fixture, Task> check) =>
            await checks.CheckAsync("operations: " + name, () => check(new Fixture(sandbox, name))).ConfigureAwait(false);

        await Run("copy tree and protected undo", async f =>
        {
            var source = f.Write("from/tree/sub/file.txt", "complete content");
            f.Write("from/tree/.owner", "an ordinary user filename");
            f.Directory("to");
            using var service = f.Service();
            var result = await service.ExecuteAsync(new(FileOperationKind.Copy, [f.PathOf("from/tree")], f.PathOf("to")));
            Success(result);
            CheckRunner.Equal("complete content", File.ReadAllText(f.PathOf("to/tree/sub/file.txt")));
            CheckRunner.Equal("an ordinary user filename", File.ReadAllText(f.PathOf("to/tree/.owner")));
            CheckRunner.True(File.Exists(source), "Copy removed its source.");
            Success(await service.UndoAsync());
            CheckRunner.True(!Directory.Exists(f.PathOf("to/tree")) && File.Exists(source), "Undo changed the source or left the copy.");
            CheckRunner.True(f.Platform.TrashCalls == 1, "Copy undo must use recoverable Trash.");
        });

        await Run("collision policies preserve unrelated files", async f =>
        {
            var source = f.Write("from/file.txt", "incoming");
            var destination = f.Write("to/file.txt", "existing");
            using var service = f.Service();
            var failed = await service.ExecuteAsync(new(FileOperationKind.Copy, [source], f.PathOf("to")));
            CheckRunner.True(!failed.Succeeded && failed.Changes.Count == 0, "Fail collision unexpectedly succeeded.");
            CheckRunner.Equal("existing", File.ReadAllText(destination));
            Success(await service.ExecuteAsync(new(FileOperationKind.Copy, [source], f.PathOf("to"), Collision: CollisionPolicy.Skip)));
            CheckRunner.Equal("existing", File.ReadAllText(destination));
            Success(await service.ExecuteAsync(new(FileOperationKind.Copy, [source], f.PathOf("to"), Collision: CollisionPolicy.KeepBoth)));
            CheckRunner.Equal("incoming", File.ReadAllText(f.PathOf("to/file (2).txt")));
            CheckRunner.Equal("existing", File.ReadAllText(destination));
        });

        await Run("replace retains prior item and undo restores it", async f =>
        {
            var source = f.Write("from/file.txt", "new content");
            var destination = f.Write("to/file.txt", "original content");
            using var service = f.Service();
            Success(await service.ExecuteAsync(new(FileOperationKind.Copy, [source], f.PathOf("to"), Collision: CollisionPolicy.Replace)));
            CheckRunner.Equal("new content", File.ReadAllText(destination));
            Success(await service.UndoAsync());
            CheckRunner.Equal("original content", File.ReadAllText(destination));
            CheckRunner.Equal("new content", File.ReadAllText(source));
        });

        await Run("same-folder keep both does not overwrite source", async f =>
        {
            var source = f.Write("folder/name.ext", "original");
            using var service = f.Service();
            Success(await service.ExecuteAsync(new(FileOperationKind.Copy, [source], f.PathOf("folder"), Collision: CollisionPolicy.KeepBoth)));
            CheckRunner.Equal("original", File.ReadAllText(source));
            CheckRunner.Equal("original", File.ReadAllText(f.PathOf("folder/name (2).ext")));
        });

        await Run("copy validates adapter output before commit", async f =>
        {
            var source = f.Write("from/file.txt", "source bytes");
            var target = f.Write("to/file.txt", "existing destination");
            f.Platform.AfterCopy = (_, destination) =>
            {
                var modified = File.GetLastWriteTimeUtc(destination);
                File.WriteAllText(destination, "wrong bytes!");
                File.SetLastWriteTimeUtc(destination, modified);
            };
            using var service = f.Service();
            var result = await service.ExecuteAsync(new(FileOperationKind.Copy, [source], f.PathOf("to"), Collision: CollisionPolicy.Replace));
            CheckRunner.True(!result.Succeeded, "Corrupted adapter output was accepted.");
            CheckRunner.Equal("existing destination", File.ReadAllText(target));
            CheckRunner.Equal("source bytes", File.ReadAllText(source));
            CheckRunner.True(!f.PrivateAreas("to").Any(), "Failed copy left owned staging artifacts.");
        });

        await Run("reviewed update restores a different destination that arrives at the backup move", async f =>
        {
            var source = f.Write("from/update.txt", "incoming");
            var target = f.Write("to/update.txt", "reviewed destination");
            var stamp = DateTime.UtcNow.Date.AddDays(-1);
            File.SetLastWriteTimeUtc(source, stamp.AddHours(1));
            File.SetLastWriteTimeUtc(target, stamp);
            var comparisons = new FolderComparisonService();
            var scan = await comparisons.CompareAsync(f.PathOf("from"), f.PathOf("to"),
                new FolderComparisonOptions(CompareContents: false, TimestampTolerance: TimeSpan.Zero));
            var request = comparisons.BuildCopyPlan(scan, ComparisonDirection.LeftToRight, update: CopyUpdateMode.Existing).Requests.Single();
            var held = f.PathOf("to/reviewed-held.txt");
            string? replacementIdentity = null;
            f.Platform.BeforeMove = (from, to, _) =>
            {
                if (from == target && Path.GetFileName(to) == ".previous" && replacementIdentity is null)
                {
                    File.Move(target, held);
                    File.WriteAllText(target, "unreviewed replacement");
                    replacementIdentity = ListaryClient.ReadNativeItemIdentity(target);
                }
                return Task.CompletedTask;
            };
            using var service = f.Service();
            var result = await service.ExecuteAsync(request);
            CheckRunner.True(!result.Succeeded && result.Errors.Any(error => error.Contains("reviewed file changed")),
                "The update did not reject the object moved by the backup race.");
            CheckRunner.Equal("unreviewed replacement", File.ReadAllText(target));
            CheckRunner.Equal(replacementIdentity, ListaryClient.ReadNativeItemIdentity(target), "Rollback did not restore the exact replaced object.");
            CheckRunner.Equal("reviewed destination", File.ReadAllText(held));
            CheckRunner.Equal("incoming", File.ReadAllText(source));
            CheckRunner.True(!f.PrivateAreas("to").Any(), "The rejected update left a private area.");
        });

        await Run("update preview rejects a same-content replacement made after its scan", async f =>
        {
            var source = f.Write("from/update.txt", "incoming");
            var target = f.Write("to/update.txt", "original");
            var stamp = DateTime.UtcNow.Date.AddDays(-1);
            File.SetLastWriteTimeUtc(source, stamp.AddHours(1));
            File.SetLastWriteTimeUtc(target, stamp);
            var comparisons = new FolderComparisonService();
            var scan = await comparisons.CompareAsync(f.PathOf("from"), f.PathOf("to"),
                new FolderComparisonOptions(CompareContents: false, TimestampTolerance: TimeSpan.Zero));
            File.Move(target, f.PathOf("to/original-held.txt"));
            File.WriteAllText(target, "original");
            File.SetLastWriteTimeUtc(target, stamp);
            await CheckRunner.ThrowsAsync<IOException>(() => Task.Run(() =>
                comparisons.BuildCopyPlan(scan, ComparisonDirection.LeftToRight, update: CopyUpdateMode.Existing)));
        });

        await Run("cancel partial copy retains source and prior destination", async f =>
        {
            var source = f.Write("from/file.bin", new string('s', 1024 * 1024));
            var target = f.Write("to/file.bin", "original");
            using var cancel = new CancellationTokenSource();
            f.Platform.BeforeCopy = async (_, destination, _) =>
            {
                await File.WriteAllTextAsync(destination, "partial staging bytes");
                cancel.Cancel();
                cancel.Token.ThrowIfCancellationRequested();
            };
            using var service = f.Service();
            var result = await service.ExecuteAsync(new(FileOperationKind.Copy, [source], f.PathOf("to"), Collision: CollisionPolicy.Replace), cancellationToken: cancel.Token);
            CheckRunner.True(result.Canceled && !result.Succeeded, "Cancellation was not reported.");
            CheckRunner.Equal("original", File.ReadAllText(target));
            CheckRunner.Equal(1024 * 1024L, new FileInfo(source).Length);
            CheckRunner.True(!f.PrivateAreas("to").Any(), "Canceled copy left disposable staging files.");
        });

        await Run("source changes are detected before copying commits", async f =>
        {
            var source = f.Write("from/file.txt", "before");
            f.Directory("to");
            f.Platform.AfterCopy = (original, _) => File.WriteAllText(original, "after!");
            using var service = f.Service();
            var result = await service.ExecuteAsync(new(FileOperationKind.Copy, [source], f.PathOf("to")));
            CheckRunner.True(!result.Succeeded, "Source mutation was not detected.");
            CheckRunner.True(!File.Exists(f.PathOf("to/file.txt")), "An inconsistent copy was published.");
            CheckRunner.Equal("after!", File.ReadAllText(source));
        });

        await Run("fast same-volume move and undo", async f =>
        {
            var source = f.Write("from/file.txt", "move me");
            f.Directory("to");
            using var service = f.Service();
            Success(await service.ExecuteAsync(new(FileOperationKind.Move, [source], f.PathOf("to"))));
            CheckRunner.Equal(0, f.Platform.CopyCalls, "Same-volume move copied file data.");
            CheckRunner.True(!File.Exists(source), "Move left the source path.");
            Success(await service.UndoAsync());
            CheckRunner.Equal("move me", File.ReadAllText(source));
            CheckRunner.True(!File.Exists(f.PathOf("to/file.txt")), "Move undo left a duplicate.");
        });

        await Run("cross-volume fallback stages before retiring source", async f =>
        {
            var source = f.Write("from/tree/file.txt", "cross volume");
            f.Directory("to");
            f.Platform.CrossVolume = (a, b) => f.Crosses("from", "to", a, b);
            using var service = f.Service();
            Success(await service.ExecuteAsync(new(FileOperationKind.Move, [f.PathOf("from/tree")], f.PathOf("to"))));
            CheckRunner.True(f.Platform.CopyCalls == 1 && !File.Exists(source), "Cross-volume move did not complete.");
            CheckRunner.Equal("cross volume", File.ReadAllText(f.PathOf("to/tree/file.txt")));
            Success(await service.UndoAsync());
            CheckRunner.Equal("cross volume", File.ReadAllText(source));
            CheckRunner.True(!Directory.Exists(f.PathOf("to/tree")), "Cross-volume undo left the destination.");
        });

        await Run("postcommit cancellation does not misreport atomic move", async f =>
        {
            var source = f.Write("from/file.txt", "moved");
            f.Directory("to");
            using var cancel = new CancellationTokenSource();
            f.Platform.AfterMove = (a, b) => { if (a == source && b == f.PathOf("to/file.txt")) cancel.Cancel(); };
            using var service = f.Service();
            var result = await service.ExecuteAsync(new(FileOperationKind.Move, [source], f.PathOf("to")), cancellationToken: cancel.Token);
            Success(result);
            CheckRunner.True(!result.Canceled && File.Exists(f.PathOf("to/file.txt")), "A committed native move was reported canceled.");
        });

        await Run("trash and native restore contract", async f =>
        {
            var source = f.Write("from/file.txt", "trash and restore");
            using var service = f.Service();
            Success(await service.ExecuteAsync(new(FileOperationKind.Trash, [source])));
            CheckRunner.True(!File.Exists(source) && service.CanUndo, "Trash was not recorded as reversible.");
            Success(await service.UndoAsync());
            CheckRunner.Equal(1, f.Platform.RestoreCalls, "Trash undo bypassed the native restore interface.");
            CheckRunner.Equal("trash and restore", File.ReadAllText(source));
        });

        await Run("trash without returned location is not falsely undoable", async f =>
        {
            var source = f.Write("from/file.txt", "trashed");
            f.Platform.ReturnTrashLocation = false;
            using var service = f.Service();
            Success(await service.ExecuteAsync(new(FileOperationKind.Trash, [source])));
            CheckRunner.True(!service.CanUndo, "An unknown native Trash path was advertised as reversible.");
        });

        await Run("undo rejects same-size content changes with restored timestamp", async f =>
        {
            var source = f.Write("from/file.txt", "AAAA");
            f.Directory("to");
            using var service = f.Service();
            Success(await service.ExecuteAsync(new(FileOperationKind.Copy, [source], f.PathOf("to"))));
            var target = f.PathOf("to/file.txt");
            var timestamp = File.GetLastWriteTimeUtc(target);
            File.WriteAllText(target, "BBBB");
            File.SetLastWriteTimeUtc(target, timestamp);
            var result = await service.UndoAsync();
            CheckRunner.True(!result.Succeeded && result.Errors.Count > 0, "Undo ignored changed content.");
            CheckRunner.Equal("BBBB", File.ReadAllText(target));
            CheckRunner.Equal(0, f.Platform.TrashCalls, "Undo touched a modified file.");
        });

        await Run("undo rejects a new item at original source", async f =>
        {
            var source = f.Write("from/file.txt", "moved");
            f.Directory("to");
            using var service = f.Service();
            Success(await service.ExecuteAsync(new(FileOperationKind.Move, [source], f.PathOf("to"))));
            File.WriteAllText(source, "new unrelated file");
            var result = await service.UndoAsync();
            CheckRunner.True(!result.Succeeded, "Undo overwrote a newly created source item.");
            CheckRunner.Equal("new unrelated file", File.ReadAllText(source));
            CheckRunner.Equal("moved", File.ReadAllText(f.PathOf("to/file.txt")));
        });

        await Run("creation and nonempty-directory undo guard", async f =>
        {
            f.Directory("folder");
            using var service = f.Service();
            Success(await service.ExecuteAsync(new(FileOperationKind.CreateFile, [], f.PathOf("folder"), ".owner")));
            CheckRunner.True(File.Exists(f.PathOf("folder/.owner")), "A valid filename collided with the internal marker.");
            Success(await service.UndoAsync());
            Success(await service.ExecuteAsync(new(FileOperationKind.CreateDirectory, [], f.PathOf("folder"), "new")));
            f.Write("folder/new/user.txt", "keep me");
            var result = await service.UndoAsync();
            CheckRunner.True(!result.Succeeded, "Undo removed a folder that acquired user data.");
            CheckRunner.Equal("keep me", File.ReadAllText(f.PathOf("folder/new/user.txt")));
        });

        await Run("batch rename swap and cycle-safe undo", async f =>
        {
            var first = f.Write("folder/a.txt", "A");
            var second = f.Write("folder/b.txt", "B");
            using var service = f.Service();
            Success(await service.ExecuteAsync(new(FileOperationKind.BatchRename, [], Renames: [new(first, "b.txt"), new(second, "a.txt")])));
            CheckRunner.Equal("B", File.ReadAllText(first));
            CheckRunner.Equal("A", File.ReadAllText(second));
            Success(await service.UndoAsync());
            CheckRunner.Equal("A", File.ReadAllText(first));
            CheckRunner.Equal("B", File.ReadAllText(second));
        });

        await Run("failed batch publish rolls every original back", async f =>
        {
            var first = f.Write("folder/a.txt", "A");
            var second = f.Write("folder/b.txt", "B");
            var failures = 0;
            f.Platform.BeforeMove = (_, destination, _) =>
            {
                if (destination == f.PathOf("folder/d.txt") && failures++ == 0) throw new IOException("Injected second publish failure");
                return Task.CompletedTask;
            };
            using var service = f.Service();
            var result = await service.ExecuteAsync(new(FileOperationKind.BatchRename, [], Renames: [new(first, "c.txt"), new(second, "d.txt")]));
            CheckRunner.True(!result.Succeeded && result.Changes.Count == 0, "Batch rollback did not report its actual final state.");
            CheckRunner.Equal("A", File.ReadAllText(first));
            CheckRunner.Equal("B", File.ReadAllText(second));
            CheckRunner.True(!File.Exists(f.PathOf("folder/c.txt")) && !f.PrivateAreas("folder").Any(), "Batch rollback left a committed or staged item.");
        });

        await Run("cancel staged batch rename restores originals", async f =>
        {
            var first = f.Write("folder/a.txt", "A");
            var second = f.Write("folder/b.txt", "B");
            using var cancel = new CancellationTokenSource();
            f.Platform.AfterMove = (source, _) => { if (source == first) cancel.Cancel(); };
            using var service = f.Service();
            var result = await service.ExecuteAsync(new(FileOperationKind.BatchRename, [], Renames: [new(first, "c.txt"), new(second, "d.txt")]), cancellationToken: cancel.Token);
            CheckRunner.True(result.Canceled, "The canceled rename was reported completed.");
            CheckRunner.Equal("A", File.ReadAllText(first));
            CheckRunner.Equal("B", File.ReadAllText(second));
        });

        await Run("path guards reject recursive and overlapping mutations", async f =>
        {
            var source = f.Write("folder/tree/child/file.txt", "unchanged");
            using var service = f.Service();
            var recursive = await service.ExecuteAsync(new(FileOperationKind.Copy, [f.PathOf("folder/tree")], f.PathOf("folder/tree/child")));
            var overlap = await service.ExecuteAsync(new(FileOperationKind.Trash, [f.PathOf("folder/tree"), source]));
            var traverse = await service.ExecuteAsync(new(FileOperationKind.Rename, [source], NewName: "../escape"));
            var ownState = await service.ExecuteAsync(new(FileOperationKind.Trash, [f.State]));
            CheckRunner.True(!recursive.Succeeded && !overlap.Succeeded && !traverse.Succeeded && !ownState.Succeeded, "An unsafe path was admitted.");
            CheckRunner.Equal("unchanged", File.ReadAllText(source));
            CheckRunner.Equal(0, f.Platform.TrashCalls, "Rejected selections touched the filesystem.");
        });

        await Run("serialized requests never run native copies concurrently", async f =>
        {
            var first = f.Write("from/a.txt", "A");
            var second = f.Write("from/b.txt", "B");
            f.Directory("to");
            f.Platform.BeforeCopy = async (_, _, token) => await Task.Delay(40, token);
            using var service = f.Service();
            var results = await Task.WhenAll(service.ExecuteAsync(new(FileOperationKind.Copy, [first], f.PathOf("to"))),
                service.ExecuteAsync(new(FileOperationKind.Copy, [second], f.PathOf("to"))));
            foreach (var result in results) Success(result);
            CheckRunner.Equal(1, f.Platform.MaxConcurrentCopies, "Two mutation jobs overlapped.");
        });

        await Run("journal reload retains protected undo", async f =>
        {
            var source = f.Write("from/file.txt", "persisted undo");
            f.Directory("to");
            using (var service = f.Service()) Success(await service.ExecuteAsync(new(FileOperationKind.Copy, [source], f.PathOf("to"))));
            using var reopened = f.Service();
            CheckRunner.True(reopened.CanUndo && reopened.History.Count == 1, "Journal history was lost.");
            Success(await reopened.UndoAsync());
            CheckRunner.True(!File.Exists(f.PathOf("to/file.txt")) && File.Exists(source), "Reloaded undo failed.");
        });

        await Run("interrupted commit reconciles exact recorded destination", async f =>
        {
            var source = f.Write("from/file.txt", "recover a committed copy");
            f.Directory("to");
            using (var service = f.Service()) Success(await service.ExecuteAsync(new(FileOperationKind.Copy, [source], f.PathOf("to"))));
            var json = JsonNode.Parse(File.ReadAllText(f.Journal))!;
            var operation = json["Operations"]![0]!;
            operation["Status"] = "Running";
            var mutation = operation["Mutations"]![0]!;
            mutation["Applied"] = false;
            mutation["Phase"] = "Publishing";
            File.WriteAllText(f.Journal, json.ToJsonString());
            using var reopened = f.Service();
            CheckRunner.True(reopened.History[0].Status == "Interrupted" && reopened.CanUndo, "Interrupted commit was not exposed.");
            Success(await reopened.UndoAsync());
            CheckRunner.True(File.Exists(source) && !File.Exists(f.PathOf("to/file.txt")), "Reconciled undo changed the wrong item.");
        });

        await Run("interrupted replacement restores each backup commit boundary", async f =>
        {
            foreach (var phase in new[] { "SavingBackup", "BackupSaved", "RecoveringReplacement" })
            {
                var caseFixture = new Fixture(f.Root, phase);
                var source = caseFixture.Write("from/file.txt", "incoming content");
                var destination = caseFixture.Write("to/file.txt", "original destination");
                using (var service = caseFixture.Service())
                    Success(await service.ExecuteAsync(new(FileOperationKind.Copy, [source], caseFixture.PathOf("to"), Collision: CollisionPolicy.Replace)));
                var json = JsonNode.Parse(File.ReadAllText(caseFixture.Journal))!;
                var operation = json["Operations"]![0]!;
                var item = operation["Mutations"]![0]!;
                var stage = item["Stage"]!.GetValue<string>();
                var backup = item["Backup"]!.GetValue<string>();
                File.Move(destination, stage);
                if (phase != "BackupSaved") File.Move(backup, destination);
                operation["Status"] = "Running";
                item["Applied"] = false; item["Phase"] = phase;
                File.WriteAllText(caseFixture.Journal, json.ToJsonString());
                using var resumed = caseFixture.Service();
                CheckRunner.True(resumed.CanUndo, "A replacement backup was hidden from recovery: " + phase);
                var result = await resumed.UndoAsync();
                Success(result);
                CheckRunner.True(result.Changes.Any(c => c.Destination == destination), "Recovery did not report the restored destination: " + phase);
                CheckRunner.Equal("original destination", File.ReadAllText(destination));
                CheckRunner.Equal("incoming content", File.ReadAllText(source));
                CheckRunner.True(!caseFixture.PrivateAreas("to").Any() && !resumed.CanUndo, "Completed replacement recovery retained staging or advertised replay: " + phase);
            }
        });

        await Run("interrupted replacement never overwrites a concurrent destination", async f =>
        {
            var source = f.Write("from/file.txt", "incoming content");
            var destination = f.Write("to/file.txt", "original destination");
            using (var service = f.Service())
                Success(await service.ExecuteAsync(new(FileOperationKind.Copy, [source], f.PathOf("to"), Collision: CollisionPolicy.Replace)));
            var json = JsonNode.Parse(File.ReadAllText(f.Journal))!;
            var operation = json["Operations"]![0]!;
            var item = operation["Mutations"]![0]!;
            var stage = item["Stage"]!.GetValue<string>();
            var backup = item["Backup"]!.GetValue<string>();
            File.Move(destination, stage);
            f.Write("to/file.txt", "concurrent new destination");
            operation["Status"] = "Running";
            item["Applied"] = false; item["Phase"] = "BackupSaved";
            File.WriteAllText(f.Journal, json.ToJsonString());
            using var resumed = f.Service();
            var blocked = await resumed.UndoAsync();
            CheckRunner.True(!blocked.Succeeded && resumed.CanUndo, "A conflicting original-destination restore was not retained for recovery.");
            CheckRunner.Equal("concurrent new destination", File.ReadAllText(destination));
            CheckRunner.Equal("original destination", File.ReadAllText(backup));
            CheckRunner.Equal("incoming content", File.ReadAllText(stage));
            File.Move(destination, f.PathOf("to/concurrent.txt"));
            Success(await resumed.UndoAsync());
            CheckRunner.Equal("original destination", File.ReadAllText(destination));
            CheckRunner.Equal("concurrent new destination", File.ReadAllText(f.PathOf("to/concurrent.txt")));
        });

        await Run("journal has an exclusive owner", async f =>
        {
            using (var service = f.Service())
                await CheckRunner.ThrowsAsync<IOException>(() => { using var duplicate = f.Service(); return Task.CompletedTask; });
            using var reopened = f.Service();
            CheckRunner.True(reopened.History.Count == 0, "Failed secondary owner altered journal state.");
        });

        await Run("cross-volume destination mutation retains original source", async f =>
        {
            var source = f.Write("from/file.txt", "original data");
            f.Directory("to");
            var destination = f.PathOf("to/file.txt");
            f.Platform.CrossVolume = (a, b) => f.Crosses("from", "to", a, b);
            f.Platform.AfterMove = (_, target) => { if (target == destination) File.WriteAllText(target, "external changed data"); };
            using var service = f.Service();
            var result = await service.ExecuteAsync(new(FileOperationKind.Move, [source], f.PathOf("to")));
            CheckRunner.True(!result.Succeeded && result.Errors.Count > 0, "A mutated published destination was accepted.");
            CheckRunner.Equal("original data", File.ReadAllText(source));
            CheckRunner.Equal("external changed data", File.ReadAllText(destination));
        });

        await Run("cross-volume undo mutation retains original destination", async f =>
        {
            var source = f.Write("from/file.txt", "original data");
            f.Directory("to");
            f.Platform.CrossVolume = (a, b) => f.Crosses("from", "to", a, b);
            using var service = f.Service();
            Success(await service.ExecuteAsync(new(FileOperationKind.Move, [source], f.PathOf("to"))));
            f.Platform.AfterMove = (_, target) => { if (target == source) File.WriteAllText(target, "external changed data"); };
            var result = await service.UndoAsync();
            CheckRunner.True(!result.Succeeded && result.Changes.Count > 0, "Undo mutation was not reported as partial.");
            CheckRunner.Equal("original data", File.ReadAllText(f.PathOf("to/file.txt")));
            CheckRunner.Equal("external changed data", File.ReadAllText(source));
        });

        await Run("duplicate replacement destinations are rejected before mutation", async f =>
        {
            var first = f.Write("a/report.txt", "A");
            var second = f.Write("b/report.txt", "B");
            var destination = f.Write("to/report.txt", "OLD");
            using var service = f.Service();
            var copied = await service.ExecuteAsync(new(FileOperationKind.Copy, [first, second], f.PathOf("to"), Collision: CollisionPolicy.Replace));
            var moved = await service.ExecuteAsync(new(FileOperationKind.Move, [first, second], f.PathOf("to"), Collision: CollisionPolicy.Replace));
            CheckRunner.True(!copied.Succeeded && !moved.Succeeded && copied.Changes.Count == 0 && moved.Changes.Count == 0, "An ambiguous replacement chain was admitted.");
            CheckRunner.Equal("A", File.ReadAllText(first));
            CheckRunner.Equal("B", File.ReadAllText(second));
            CheckRunner.Equal("OLD", File.ReadAllText(destination));
        });

        await Run("case-only rename and undo preserve spelling", async f =>
        {
            var source = f.Write("folder/Foo.txt", "case rename");
            using var service = f.Service();
            Success(await service.ExecuteAsync(new(FileOperationKind.Rename, [source], NewName: "foo.txt")));
            CheckRunner.True(System.IO.Directory.EnumerateFiles(f.PathOf("folder")).Any(p => Path.GetFileName(p) == "foo.txt"), "Rename did not change the actual directory-entry spelling.");
            Success(await service.UndoAsync());
            CheckRunner.True(System.IO.Directory.EnumerateFiles(f.PathOf("folder")).Any(p => Path.GetFileName(p) == "Foo.txt"), "Undo did not restore spelling.");
        });

        await Run("committed trash restore metadata error is never replayed", async f =>
        {
            var source = f.Write("from/file.txt", "restore once");
            using var service = f.Service();
            Success(await service.ExecuteAsync(new(FileOperationKind.Trash, [source])));
            f.Platform.FailRestoreMetadataAfterCommit = true;
            var first = await service.UndoAsync();
            CheckRunner.True(!first.Succeeded && first.Changes.Count == 1 && first.Errors.Count > 0, "Committed restoration and metadata warning were not exposed.");
            CheckRunner.Equal("restore once", File.ReadAllText(source));
            CheckRunner.True(!service.CanUndo, "A committed Trash restore was advertised for replay.");
            await service.UndoAsync();
            CheckRunner.Equal(1, f.Platform.RestoreCalls, "Native restore was replayed after it committed.");
        });

        await Run("interrupted rename staging can restore the original", async f =>
        {
            var source = f.Write("from/file.txt", "recover staged source");
            f.Directory("to");
            using (var service = f.Service()) Success(await service.ExecuteAsync(new(FileOperationKind.Copy, [source], f.PathOf("to"))));
            var token = Guid.NewGuid().ToString("N");
            var area = f.Directory("from/.opusmac-recovery-" + token);
            File.WriteAllText(Path.Combine(area, ".owner"), token);
            var stage = Path.Combine(area, "payload");
            File.Move(source, stage);
            var json = JsonNode.Parse(File.ReadAllText(f.Journal))!;
            var operation = json["Operations"]![0]!;
            operation["Status"] = "Running";
            operation["Kind"] = (int)FileOperationKind.BatchRename;
            var item = operation["Mutations"]![0]!;
            item["Action"] = "Rename"; item["Applied"] = false; item["SourceRemoved"] = true;
            item["Area"] = area; item["Token"] = token; item["Stage"] = stage;
            item["Destination"] = f.PathOf("from/renamed.txt"); item["Phase"] = "StagedRename";
            File.WriteAllText(f.Journal, json.ToJsonString());
            using var resumed = f.Service();
            var recovered = await resumed.UndoAsync();
            Success(recovered);
            CheckRunner.True(recovered.Changes.Any(c => c.Destination == source), "Interrupted rename recovery omitted its committed change.");
            CheckRunner.Equal("recover staged source", File.ReadAllText(source));
            CheckRunner.True(!System.IO.Directory.Exists(area), "Recovered rename left an owned empty area.");
        });

        await Run("interrupted undo holding is reconciled before retry", async f =>
        {
            var source = f.Write("from/file.txt", "recover held undo");
            f.Directory("to");
            using (var service = f.Service()) Success(await service.ExecuteAsync(new(FileOperationKind.Copy, [source], f.PathOf("to"))));
            var token = Guid.NewGuid().ToString("N");
            var area = f.Directory("to/.opusmac-undo-" + token);
            File.WriteAllText(Path.Combine(area, ".owner"), token);
            var hold = Path.Combine(area, "payload");
            File.Move(f.PathOf("to/file.txt"), hold);
            var json = JsonNode.Parse(File.ReadAllText(f.Journal))!;
            var operation = json["Operations"]![0]!;
            operation["Status"] = "UndoRunning";
            var item = operation["Mutations"]![0]!;
            item["UndoArea"] = area; item["UndoToken"] = token; item["UndoHold"] = hold; item["Phase"] = "UndoHeld";
            File.WriteAllText(f.Journal, json.ToJsonString());
            using var resumed = f.Service();
            Success(await resumed.UndoAsync());
            CheckRunner.True(File.Exists(source) && !File.Exists(f.PathOf("to/file.txt")), "Interrupted undo did not complete safely.");
        });

        await Run("interrupted copy undo never replays a committed Trash removal", async f =>
        {
            var source = f.Write("from/file.txt", "copy undo trash commit");
            f.Directory("to");
            using (var service = f.Service()) Success(await service.ExecuteAsync(new(FileOperationKind.Copy, [source], f.PathOf("to"))));
            var json = JsonNode.Parse(File.ReadAllText(f.Journal))!;
            var operation = json["Operations"]![0]!;
            var item = operation["Mutations"]![0]!;
            var token = Guid.NewGuid().ToString("N");
            var area = f.Directory("to/.opusmac-undo-" + token);
            File.WriteAllText(Path.Combine(area, ".owner"), token);
            var hold = Path.Combine(area, "payload");
            File.Move(f.PathOf("to/file.txt"), hold);
            item["UndoArea"] = area; item["UndoToken"] = token; item["UndoHold"] = hold;
            item["Phase"] = "UndoTrashing"; operation["Status"] = "UndoRunning";
            File.WriteAllText(f.Journal, json.ToJsonString());
            var committed = await f.Platform.TrashAsync(hold, CancellationToken.None);
            CheckRunner.True(committed.TrashedPath is not null && !File.Exists(hold), "The simulated Trash commit did not move the held copy.");
            using var journal = f.Service();
            CheckRunner.True(journal.CanUndo, "The interrupted Trash commit was hidden from recovery.");
            var result = await journal.UndoAsync();
            CheckRunner.True(result.Succeeded && journal.History.Single().Summary.Contains("exact system Trash location could not be confirmed", StringComparison.Ordinal),
                "Recovery did not complete without replay and disclose the missing native Trash receipt.");
            CheckRunner.True(!journal.CanUndo && File.Exists(source) && !File.Exists(f.PathOf("to/file.txt")),
                "Recovery replayed or retained the already removed copy.");
            CheckRunner.Equal(1, f.Platform.TrashCalls, "Recovery invoked native Trash a second time.");
            CheckRunner.True(result.Changes.Any(change => change.Action.Contains("no replay", StringComparison.Ordinal)),
                "Recovery omitted the ambiguous committed-removal result.");
        });

        await Run("interrupted copy undo reconciles both sides of the system Trash commit", async f =>
        {
            foreach (var committed in new[] { false, true })
            {
                var caseFixture = new Fixture(f.Root, committed ? "undo-trash-committed" : "undo-trash-pending");
                var source = caseFixture.Write("from/file.txt", "original copy source survives");
                caseFixture.Directory("to");
                using (var service = caseFixture.Service())
                    Success(await service.ExecuteAsync(new(FileOperationKind.Copy, [source], caseFixture.PathOf("to"))));

                var json = JsonNode.Parse(File.ReadAllText(caseFixture.Journal))!;
                var operation = json["Operations"]![0]!;
                var item = operation["Mutations"]![0]!;
                var token = Guid.NewGuid().ToString("N");
                var area = caseFixture.Directory("to/.opusmac-undo-" + token);
                File.WriteAllText(Path.Combine(area, ".owner"), token);
                var hold = Path.Combine(area, "payload");
                var destination = caseFixture.PathOf("to/file.txt");
                File.Move(destination, hold);

                string? trashPath = null;
                if (committed)
                {
                    trashPath = caseFixture.PathOf("fake-trash/committed-copy.txt");
                    System.IO.Directory.CreateDirectory(Path.GetDirectoryName(trashPath)!);
                    File.Move(hold, trashPath);
                }

                operation["Status"] = "UndoRunning";
                item["UndoArea"] = area;
                item["UndoToken"] = token;
                item["UndoHold"] = hold;
                item["Phase"] = "UndoTrashing";
                item["RecoveryRequired"] = true;
                File.WriteAllText(caseFixture.Journal, json.ToJsonString());

                using (var resumed = caseFixture.Service())
                {
                    Success(await resumed.UndoAsync());
                    CheckRunner.Equal("original copy source survives", File.ReadAllText(source));
                    CheckRunner.True(!File.Exists(destination), "Recovered Copy Undo left its destination visible.");
                    CheckRunner.True(!resumed.CanUndo && resumed.History.Single().Status == "Undone",
                        "Recovered Copy Undo remained replayable in history.");

                    if (committed)
                    {
                        CheckRunner.True(trashPath is not null && File.Exists(trashPath),
                            "The already-trashed payload was not preserved.");
                        CheckRunner.Equal(0, caseFixture.Platform.TrashCalls,
                            "Recovery replayed a system Trash action that had already committed.");
                        CheckRunner.True(resumed.History.Single().Summary.Contains("exact system Trash location could not be confirmed", StringComparison.Ordinal),
                            "History did not disclose the missing system Trash receipt.");
                    }
                    else
                    {
                        CheckRunner.Equal(1, caseFixture.Platform.TrashCalls,
                            "Recovery did not resume the pending Trash action exactly once.");
                    }
                }

                using var reopenedAgain = caseFixture.Service();
                CheckRunner.True(!reopenedAgain.CanUndo && reopenedAgain.History.Single().Status == "Undone",
                    "A second cold reopen resurrected the completed Undo.");
                CheckRunner.Equal(committed ? 0 : 1, caseFixture.Platform.TrashCalls,
                    "Repeated reopen/recovery duplicated or replayed the Trash action.");
            }
        });

        await Run("native adapter copies directories and atomically undoes moves", async f =>
        {
            var source = f.Write("from/tree/file.txt", "native data");
            f.Directory("to"); f.Directory("moved");
            using var service = new FileOperationService(new DesktopFilePlatform(), f.State);
            Success(await service.ExecuteAsync(new(FileOperationKind.Copy, [f.PathOf("from/tree")], f.PathOf("to"))));
            CheckRunner.Equal("native data", File.ReadAllText(f.PathOf("to/tree/file.txt")));
            Success(await service.ExecuteAsync(new(FileOperationKind.Move, [source], f.PathOf("moved"))));
            Success(await service.UndoAsync());
            CheckRunner.Equal("native data", File.ReadAllText(source));
        });

        await Run("directory-link recursion guard and broken-link copy", async f =>
        {
            f.Write("from/tree/file.txt", "must stay");
            f.Directory("to");
            try { System.IO.Directory.CreateSymbolicLink(f.PathOf("alias"), f.PathOf("from/tree")); }
            catch (Exception error) when (error is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
            {
                CheckRunner.NotVerified("The host could not create the disposable directory-link fixture: " + error.Message);
            }
            var link = f.PathOf("from/broken-link");
            File.CreateSymbolicLink(link, "missing-target");
            using var service = f.Service();
            var rejected = await service.ExecuteAsync(new(FileOperationKind.Copy, [f.PathOf("from/tree")], f.PathOf("alias")));
            CheckRunner.True(!rejected.Succeeded, "A directory link bypassed the recursive-copy guard.");
            Success(await service.ExecuteAsync(new(FileOperationKind.Copy, [link], f.PathOf("to"))));
            CheckRunner.Equal("missing-target", new FileInfo(f.PathOf("to/broken-link")).LinkTarget);
        });

        await Run("journal hard limit preserves the last reloadable state", async f =>
        {
            var limits = new OperationJournalLimits(16 * 1024, 12 * 1024, 2);
            using var journal = new OperationJournal(f.State, limits);
            var pending = new JournalOperation
            {
                Id = "protected-recovery", Status = "Interrupted", Kind = FileOperationKind.Move,
                Mutations = [new() { Source = f.PathOf("source"), Stage = f.PathOf("held"), SourceRemoved = true, RecoveryRequired = true }]
            };
            var entries = new List<JournalOperation> { pending };
            journal.Save(entries);
            var committed = File.ReadAllBytes(f.Journal);
            pending.Errors.Add(new string('x', limits.MaximumBytes));
            await CheckRunner.ThrowsAsync<OperationHistoryCapacityException>(() => { journal.Save(entries); return Task.CompletedTask; });
            CheckRunner.True(File.ReadAllBytes(f.Journal).SequenceEqual(committed), "An oversized journal replaced the last valid state.");
            CheckRunner.True(new FileInfo(f.Journal).Length <= limits.MaximumBytes, "The writer exceeded its own reader limit.");
            CheckRunner.Equal("protected-recovery", journal.Load().Single().Id);
            CheckRunner.Equal(0, journal.Load().Single().Errors.Count);
            CheckRunner.True(!System.IO.Directory.EnumerateFiles(f.State, ".operations-*.tmp").Any(), "Rejected serialization left a temporary journal.");
        });

        await Run("retired history pruning preserves replacement undo and recovery", async f =>
        {
            var limits = new OperationJournalLimits(128 * 1024, 96 * 1024, 2);
            var source = f.Write("from/file.txt", "new version");
            var destination = f.Write("to/file.txt", "previous version");
            using (var service = f.Service(limits))
                Success(await service.ExecuteAsync(new(FileOperationKind.Copy, [source], f.PathOf("to"), Collision: CollisionPolicy.Replace)));
            var held = f.Write("recovery/held.txt", "preserved recovery payload");
            using (var journal = new OperationJournal(f.State, limits))
            {
                var entries = journal.Load();
                for (var index = 0; index < 12; index++)
                    entries.Add(new JournalOperation { Id = "retired-" + index, Status = "Undone", Kind = FileOperationKind.Copy });
                entries.Add(new JournalOperation
                {
                    Id = "interrupted-stage", Status = "Interrupted", Kind = FileOperationKind.Copy,
                    Mutations = [new() { Source = source, Stage = held, Phase = "Staging", RecoveryRequired = true }]
                });
                journal.Save(entries);
                CheckRunner.Equal(4, journal.Load().Count, "Retired metadata was not bounded independently of live records.");
                CheckRunner.True(journal.Load().Any(o => o.Id == "retired-11") && journal.Load().All(o => o.Id != "retired-0"), "Pruning did not keep the newest retired history.");
            }
            using var resumed = f.Service(limits);
            CheckRunner.True(resumed.CanUndo && resumed.History.Any(o => o.Id == "interrupted-stage"), "Pruning dropped an undo or recovery record.");
            Success(await resumed.UndoAsync());
            CheckRunner.Equal("previous version", File.ReadAllText(destination));
            CheckRunner.Equal("new version", File.ReadAllText(source));
            CheckRunner.Equal("preserved recovery payload", File.ReadAllText(held));
        });

        await Run("byte pressure prunes only retired metadata", f =>
        {
            var limits = new OperationJournalLimits(16 * 1024, 12 * 1024, 200);
            using var journal = new OperationJournal(f.State, limits);
            var protectedRecord = new JournalOperation
            {
                Id = "live-undo", Status = "Succeeded", Kind = FileOperationKind.Copy,
                Mutations = [new() { Source = f.PathOf("source"), Destination = f.PathOf("destination"), Applied = true, Fingerprint = "retained" }]
            };
            var entries = new List<JournalOperation>
            {
                new() { Id = "retired-large-diagnostic", Status = "Failed", Errors = [new string('x', 24 * 1024)] },
                protectedRecord
            };
            journal.Save(entries);
            var reopened = journal.Load();
            CheckRunner.Equal(1, reopened.Count);
            CheckRunner.Equal("live-undo", reopened[0].Id);
            CheckRunner.Equal("retained", reopened[0].Mutations[0].Fingerprint);
            CheckRunner.True(new FileInfo(f.Journal).Length <= limits.MaximumBytes, "Byte-pressure pruning did not respect the shared limit.");
            return Task.CompletedTask;
        });

        await Run("protected history pressure blocks new work before mutation and permits undo", async f =>
        {
            var limits = new OperationJournalLimits(64 * 1024, 48 * 1024, 200);
            var source = f.Write("from/file.txt", "new version");
            var destination = f.Write("to/file.txt", "previous version");
            f.Directory("new-work");
            using (var service = f.Service(limits))
                Success(await service.ExecuteAsync(new(FileOperationKind.Copy, [source], f.PathOf("to"), Collision: CollisionPolicy.Replace)));
            using (var journal = new OperationJournal(f.State, limits))
            {
                var entries = journal.Load();
                entries.Single().Errors.Add(new string('r', 36 * 1024));
                journal.Save(entries);
            }
            using var resumed = f.Service(limits);
            var blocked = await resumed.ExecuteAsync(new(FileOperationKind.CreateFile, [], f.PathOf("new-work"), "file.txt"));
            CheckRunner.True(!blocked.Succeeded && blocked.Changes.Count == 0 && blocked.Errors.Any(e => e.Contains("history", StringComparison.OrdinalIgnoreCase)), "Protected history pressure did not block new work with an actionable result.");
            CheckRunner.True(!File.Exists(f.PathOf("new-work/file.txt")) && !f.PrivateAreas("new-work").Any(), "Rejected work created a user file or staging area.");
            CheckRunner.Equal("new version", File.ReadAllText(destination));
            CheckRunner.True(resumed.CanUndo, "Capacity rejection disabled the retained undo operation.");
            Success(await resumed.UndoAsync());
            CheckRunner.Equal("previous version", File.ReadAllText(destination));
            Success(await resumed.ExecuteAsync(new(FileOperationKind.CreateFile, [], f.PathOf("new-work"), "file.txt")));
            CheckRunner.True(File.Exists(f.PathOf("new-work/file.txt")), "Retiring protected history did not allow work to resume.");
        });

        await Run("storage review and reviewed discard reclaim replacement backup", async f =>
        {
            const string original = "original replacement payload";
            var source = f.Write("from/file.txt", "new replacement payload");
            var destination = f.Write("to/file.txt", original);
            using var service = f.Service();
            var operation = await service.ExecuteAsync(new(FileOperationKind.Copy, [source], f.PathOf("to"), Collision: CollisionPolicy.Replace));
            Success(operation);

            var review = await service.ReviewStorageAsync();
            var record = review.Records.Single(item => item.OperationId == operation.Id);
            CheckRunner.True(record.CanUndo && record.CanDiscard && !record.RecoveryProtected,
                "A stable replacement Undo record was not offered for explicit discard.");
            CheckRunner.True(record.ReclaimableBytes >= original.Length && record.ReclaimableFiles >= 1,
                "Replacement-backup storage was not measured for review.");
            CheckRunner.True(record.Areas.Any(area => area.Purpose.Contains("Replacement backup", StringComparison.Ordinal)),
                "The review did not identify replacement-backup storage.");

            var json = JsonNode.Parse(File.ReadAllText(f.Journal))!;
            var journalOperation = json["Operations"]!.AsArray().Single(item => item!["Id"]!.GetValue<string>() == operation.Id)!;
            var mutation = journalOperation["Mutations"]![0]!;
            var area = mutation["Area"]!.GetValue<string>();
            var backup = mutation["Backup"]!.GetValue<string>();
            CheckRunner.True(File.Exists(backup), "The replacement backup was missing before discard.");

            var discarded = await service.DiscardUndoHistoryAsync([operation.Id]);
            CheckRunner.True(discarded.DiscardedOperationIds.Contains(operation.Id) && discarded.PendingOperationIds.Count == 0,
                "Reviewed replacement history was not fully discarded.");
            CheckRunner.True(discarded.ReclaimedBytes >= original.Length && discarded.ReclaimedFiles >= 1,
                "Discard did not report reclaimed replacement bytes/files.");
            CheckRunner.True(!System.IO.Directory.Exists(area) && !File.Exists(backup), "Discard left its verified private backup area behind.");
            CheckRunner.True(!service.CanUndo && service.History.All(item => item.Id != operation.Id),
                "Discarded history remained advertised as Undoable.");
            CheckRunner.Equal(0, JsonNode.Parse(File.ReadAllText(f.Journal))!["Operations"]!.AsArray().Count,
                "Discard completion was not committed to the journal.");

            var again = await service.DiscardUndoHistoryAsync([operation.Id]);
            CheckRunner.True(again.DiscardedOperationIds.Count == 0 && again.MissingOperationIds.Contains(operation.Id) &&
                again.ReclaimedBytes == 0 && again.ReclaimedFiles == 0,
                "Repeating a completed discard was not idempotent.");
            CheckRunner.Equal("new replacement payload", File.ReadAllText(destination));
        });

        await Run("reviewed ids never sweep later undo history", async f =>
        {
            var first = f.Write("from/first.txt", "first");
            var second = f.Write("from/second.txt", "second");
            f.Directory("to-first");
            f.Directory("to-second");
            using var service = f.Service();
            var firstResult = await service.ExecuteAsync(new(FileOperationKind.Copy, [first], f.PathOf("to-first")));
            Success(firstResult);
            var reviewedIds = (await service.ReviewStorageAsync()).Records.Where(item => item.CanDiscard).Select(item => item.OperationId).ToArray();
            CheckRunner.True(reviewedIds.SequenceEqual([firstResult.Id]), "The first review did not isolate the expected Undo record.");

            var secondResult = await service.ExecuteAsync(new(FileOperationKind.Copy, [second], f.PathOf("to-second")));
            Success(secondResult);
            var discarded = await service.DiscardUndoHistoryAsync(reviewedIds);
            CheckRunner.True(discarded.DiscardedOperationIds.SequenceEqual([firstResult.Id]), "Confirmation included history that was not in the review.");
            CheckRunner.True(service.History.Any(item => item.Id == secondResult.Id && item.CanUndo), "Later Undo history was swept by an older review.");
            Success(await service.UndoAsync());
            CheckRunner.True(!File.Exists(f.PathOf("to-second/second.txt")) && File.Exists(f.PathOf("to-first/first.txt")),
                "Undo after reviewed discard targeted the wrong operation.");
        });

        await Run("recovery-protected history refuses discard and preserves backup", async f =>
        {
            var source = f.Write("from/file.txt", "new content");
            f.Write("to/file.txt", "original content");
            string operationId;
            string backup;
            using (var service = f.Service())
            {
                var result = await service.ExecuteAsync(new(FileOperationKind.Copy, [source], f.PathOf("to"), Collision: CollisionPolicy.Replace));
                Success(result);
                operationId = result.Id;
            }
            var json = JsonNode.Parse(File.ReadAllText(f.Journal))!;
            var operation = json["Operations"]!.AsArray().Single(item => item!["Id"]!.GetValue<string>() == operationId)!;
            var mutation = operation["Mutations"]![0]!;
            backup = mutation["Backup"]!.GetValue<string>();
            operation["Status"] = "Running";
            mutation["Phase"] = "BackupSaved";
            mutation["RecoveryRequired"] = true;
            File.WriteAllText(f.Journal, json.ToJsonString());

            using var resumed = f.Service();
            var review = await resumed.ReviewStorageAsync();
            var record = review.Records.Single(item => item.OperationId == operationId);
            CheckRunner.True(record.RecoveryProtected && !record.CanDiscard, "Interrupted replacement storage was offered for discard.");
            var discard = await resumed.DiscardUndoHistoryAsync([operationId]);
            CheckRunner.True(discard.RefusedOperationIds.Contains(operationId), "Recovery-protected history was not explicitly refused.");
            CheckRunner.True(File.Exists(backup) && resumed.History.Any(item => item.Id == operationId),
                "Refusing discard damaged interrupted replacement recovery data.");
            var after = JsonNode.Parse(File.ReadAllText(f.Journal))!["Operations"]!.AsArray().Single()!;
            CheckRunner.True(after["DiscardRequested"]?.GetValue<bool>() != true, "A refused recovery record persisted discard intent.");
        });

        await Run("ownership preflight isolates bad token and keeps its undo", async f =>
        {
            var firstSource = f.Write("from/first.txt", "first incoming");
            var secondSource = f.Write("from/second.txt", "second incoming");
            f.Write("to-first/first.txt", "first original");
            f.Write("to-second/second.txt", "second original");
            using var service = f.Service();
            var first = await service.ExecuteAsync(new(FileOperationKind.Copy, [firstSource], f.PathOf("to-first"), Collision: CollisionPolicy.Replace));
            var second = await service.ExecuteAsync(new(FileOperationKind.Copy, [secondSource], f.PathOf("to-second"), Collision: CollisionPolicy.Replace));
            Success(first); Success(second);
            var reviewedIds = (await service.ReviewStorageAsync()).Records.Where(item => item.CanDiscard).Select(item => item.OperationId).ToArray();

            var json = JsonNode.Parse(File.ReadAllText(f.Journal))!;
            var secondOperation = json["Operations"]!.AsArray().Single(item => item!["Id"]!.GetValue<string>() == second.Id)!;
            var secondMutation = secondOperation["Mutations"]![0]!;
            var area = secondMutation["Area"]!.GetValue<string>();
            var token = secondMutation["Token"]!.GetValue<string>();
            var backup = secondMutation["Backup"]!.GetValue<string>();
            var marker = Path.Combine(area, ".owner");
            File.WriteAllText(marker, new string('x', token.Length));

            var discarded = await service.DiscardUndoHistoryAsync(reviewedIds);
            CheckRunner.True(discarded.DiscardedOperationIds.Contains(first.Id) && discarded.RefusedOperationIds.Contains(second.Id),
                "One bad owner token did not remain isolated to its own operation.");
            CheckRunner.True(File.Exists(backup) && service.History.Any(item => item.Id == second.Id && item.CanUndo),
                "Ownership failure damaged the protected Undo record.");
            var persisted = JsonNode.Parse(File.ReadAllText(f.Journal))!["Operations"]!.AsArray()
                .Single(item => item!["Id"]!.GetValue<string>() == second.Id)!;
            CheckRunner.True(persisted["DiscardRequested"]?.GetValue<bool>() != true, "Ownership failure persisted discard intent.");

            File.WriteAllText(marker, token);
            Success(await service.UndoAsync());
            CheckRunner.Equal("second original", File.ReadAllText(f.PathOf("to-second/second.txt")));
        });

        await Run("persisted discard intent resumes cleanup after reopen", async f =>
        {
            var source = f.Write("from/file.txt", "new content");
            f.Write("to/file.txt", "original content");
            string operationId;
            string area;
            using (var service = f.Service())
            {
                var result = await service.ExecuteAsync(new(FileOperationKind.Copy, [source], f.PathOf("to"), Collision: CollisionPolicy.Replace));
                Success(result);
                operationId = result.Id;
            }
            var json = JsonNode.Parse(File.ReadAllText(f.Journal))!;
            var operation = json["Operations"]!.AsArray().Single(item => item!["Id"]!.GetValue<string>() == operationId)!;
            area = operation["Mutations"]![0]!["Area"]!.GetValue<string>();
            operation["DiscardRequested"] = true;
            File.WriteAllText(f.Journal, json.ToJsonString());
            CheckRunner.True(System.IO.Directory.Exists(area), "Crash fixture lost its private area before restart.");

            using var resumed = f.Service();
            CheckRunner.True(!resumed.CanUndo && resumed.History.All(item => item.Id != operationId),
                "Restart did not finish a durably confirmed discard.");
            CheckRunner.True(!System.IO.Directory.Exists(area), "Restart left confirmed private discard storage behind.");
            CheckRunner.Equal(0, JsonNode.Parse(File.ReadAllText(f.Journal))!["Operations"]!.AsArray().Count,
                "Restart cleanup did not commit journal removal.");
        });

        await Run("discard never touches external undo trash path", async f =>
        {
            var source = f.Write("from/file.txt", "source content");
            f.Directory("to");
            using var service = f.Service();
            var copied = await service.ExecuteAsync(new(FileOperationKind.Copy, [source], f.PathOf("to")));
            Success(copied);
            Success(await service.UndoAsync());
            var json = JsonNode.Parse(File.ReadAllText(f.Journal))!;
            var operation = json["Operations"]!.AsArray().Single(item => item!["Id"]!.GetValue<string>() == copied.Id)!;
            var trashPath = operation["Mutations"]![0]!["UndoTrashPath"]!.GetValue<string>();
            CheckRunner.True(File.Exists(trashPath), "Undo Trash fixture did not retain its external path.");

            var result = await service.DiscardUndoHistoryAsync([copied.Id]);
            CheckRunner.True(result.RefusedOperationIds.Contains(copied.Id), "Retired Undo metadata was unexpectedly accepted as live Undo discard.");
            CheckRunner.True(File.Exists(trashPath), "Discard touched an external Undo Trash path.");
        });

        await Run("retired pruning retains extant private storage metadata", f =>
        {
            var limits = new OperationJournalLimits(128 * 1024, 96 * 1024, 1);
            var parent = f.Directory("to");
            var area = OperationPaths.CreateArea(parent, "retired-storage", out var token);
            var payload = Path.Combine(area, "payload");
            File.WriteAllText(payload, "retained private storage");
            using var journal = new OperationJournal(f.State, limits);
            var entries = new List<JournalOperation>
            {
                new()
                {
                    Id = "retired-storage", Status = "Undone", Kind = FileOperationKind.Copy,
                    Mutations = [new() { Source = f.PathOf("source"), Phase = "Undone", Undone = true,
                        Area = area, Token = token, Stage = payload }]
                },
                new() { Id = "retired-0", Status = "Undone", Kind = FileOperationKind.Copy },
                new() { Id = "retired-1", Status = "Undone", Kind = FileOperationKind.Copy },
                new() { Id = "retired-2", Status = "Undone", Kind = FileOperationKind.Copy }
            };
            journal.Save(entries);
            var loaded = journal.Load();
            CheckRunner.True(loaded.Any(item => item.Id == "retired-storage") && loaded.Any(item => item.Id == "retired-2") && loaded.Count == 2,
                "Automatic retired pruning forgot a record that still owns private storage.");
            CheckRunner.True(File.Exists(payload), "Automatic retired pruning changed private storage.");
            return Task.CompletedTask;
        });

        await Run("invalid journal is retained rather than reset", async f =>
        {
            f.Directory("state");
            File.WriteAllText(f.Journal, "{invalid journal");
            await CheckRunner.ThrowsAsync<System.Text.Json.JsonException>(() => { using var service = f.Service(); return Task.CompletedTask; });
            CheckRunner.Equal("{invalid journal", File.ReadAllText(f.Journal));
        });
    }

    private static void Success(FileOperationResult result) =>
        CheckRunner.True(result.Succeeded, string.Join(" | ", result.Errors.DefaultIfEmpty("Operation did not succeed.")));

    private sealed class Fixture
    {
        internal string Root { get; }
        internal string State => PathOf("state");
        internal string Journal => Path.Combine(State, "operations.json");
        internal FixturePlatform Platform { get; }
        internal Fixture(string sandbox, string name)
        {
            Root = Path.Combine(Path.GetFullPath(sandbox), new string(name.Where(char.IsLetterOrDigit).ToArray()) + "-" + Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(Root);
            Platform = new(Root);
        }
        internal string PathOf(string relative) => Path.Combine(Root, relative.Replace('/', Path.DirectorySeparatorChar));
        internal string Directory(string relative)
        {
            var path = PathOf(relative);
            System.IO.Directory.CreateDirectory(path);
            return path;
        }
        internal string Write(string relative, string content)
        {
            var path = PathOf(relative);
            System.IO.Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content);
            return path;
        }
        internal FileOperationService Service(OperationJournalLimits? limits = null) =>
            limits is null ? new(Platform, State) : new(Platform, State, limits);
        internal IEnumerable<string> PrivateAreas(string relative) => System.IO.Directory.EnumerateDirectories(PathOf(relative), ".opusmac-*");
        internal bool Crosses(string first, string second, string source, string destination)
        {
            var a = PathOf(first) + Path.DirectorySeparatorChar;
            var b = PathOf(second) + Path.DirectorySeparatorChar;
            return (source.StartsWith(a, StringComparison.OrdinalIgnoreCase) && destination.StartsWith(b, StringComparison.OrdinalIgnoreCase)) ||
                (source.StartsWith(b, StringComparison.OrdinalIgnoreCase) && destination.StartsWith(a, StringComparison.OrdinalIgnoreCase));
        }
    }

    private sealed class FixturePlatform(string sandbox) : IFilePlatform
    {
        internal Func<string, string, CancellationToken, Task>? BeforeCopy { get; set; }
        internal Action<string, string>? AfterCopy { get; set; }
        internal Func<string, string, CancellationToken, Task>? BeforeMove { get; set; }
        internal Action<string, string>? AfterMove { get; set; }
        internal Func<string, string, bool>? CrossVolume { get; set; }
        internal bool ReturnTrashLocation { get; set; } = true;
        internal bool FailRestoreMetadataAfterCommit { get; set; }
        internal int CopyCalls;
        internal int TrashCalls;
        internal int RestoreCalls;
        internal int MaxConcurrentCopies;
        private int _activeCopies;

        private void Guard(string path)
        {
            var full = Path.GetFullPath(path);
            if (!full.StartsWith(Path.GetFullPath(sandbox) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new IOException("Test platform refused a path outside its provided sandbox.");
        }

        public async Task CopyLeafAsync(string source, string destination, CancellationToken cancellationToken)
        {
            Guard(source); Guard(destination);
            CopyCalls++;
            var active = Interlocked.Increment(ref _activeCopies);
            MaxConcurrentCopies = Math.Max(MaxConcurrentCopies, active);
            try
            {
                if (BeforeCopy is not null) await BeforeCopy(source, destination, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                var attributes = File.GetAttributes(source);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    if ((attributes & FileAttributes.Directory) != 0)
                        System.IO.Directory.CreateSymbolicLink(destination, new DirectoryInfo(source).LinkTarget!);
                    else File.CreateSymbolicLink(destination, new FileInfo(source).LinkTarget!);
                    return;
                }
                await using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read))
                await using (var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    await input.CopyToAsync(output, cancellationToken);
                    output.Flush(true);
                }
                File.SetCreationTimeUtc(destination, File.GetCreationTimeUtc(source));
                File.SetLastWriteTimeUtc(destination, File.GetLastWriteTimeUtc(source));
                File.SetAttributes(destination, attributes);
                AfterCopy?.Invoke(source, destination);
            }
            finally { Interlocked.Decrement(ref _activeCopies); }
        }

        public Task CopyDirectoryMetadataAsync(string source, string destination, CancellationToken cancellationToken)
        {
            Guard(source); Guard(destination); cancellationToken.ThrowIfCancellationRequested();
            System.IO.Directory.SetCreationTimeUtc(destination, System.IO.Directory.GetCreationTimeUtc(source));
            System.IO.Directory.SetLastWriteTimeUtc(destination, System.IO.Directory.GetLastWriteTimeUtc(source));
            File.SetAttributes(destination, File.GetAttributes(source));
            return Task.CompletedTask;
        }

        public async Task<bool> TryMoveEntryAsync(string source, string destination, CancellationToken cancellationToken)
        {
            Guard(source); Guard(destination);
            if (BeforeMove is not null) await BeforeMove(source, destination, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (CrossVolume?.Invoke(source, destination) == true) return false;
            if (File.Exists(destination) || System.IO.Directory.Exists(destination)) throw new IOException("Destination already exists.");
            if ((File.GetAttributes(source) & FileAttributes.Directory) != 0) System.IO.Directory.Move(source, destination);
            else File.Move(source, destination, false);
            AfterMove?.Invoke(source, destination);
            return true;
        }

        public async Task<TrashResult> TrashAsync(string path, CancellationToken cancellationToken)
        {
            Guard(path);
            TrashCalls++;
            var directory = Path.Combine(sandbox, "fake-trash", Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(directory);
            var target = Path.Combine(directory, Path.GetFileName(path));
            if (!await TryMoveEntryAsync(path, target, cancellationToken)) throw new IOException("Fixture Trash unexpectedly crosses volumes.");
            return new(ReturnTrashLocation ? target : null);
        }

        public async Task RestoreTrashAsync(string trashedPath, string destination, CancellationToken cancellationToken)
        {
            Guard(trashedPath); Guard(destination);
            RestoreCalls++;
            if (!await TryMoveEntryAsync(trashedPath, destination, cancellationToken)) throw new IOException("Fixture restore unexpectedly crosses volumes.");
            if (FailRestoreMetadataAfterCommit)
                throw new TrashRestoreMetadataException(trashedPath, destination, new IOException("Injected exact recycle metadata cleanup failure."));
        }

        public Task OpenAsync(string path, CancellationToken cancellationToken = default) => throw new NotSupportedException("Fixture does not launch applications.");
        public Task RevealAsync(string path, CancellationToken cancellationToken = default) => throw new NotSupportedException("Fixture does not launch applications.");
        public Task QuickLookAsync(IReadOnlyList<string> paths, CancellationToken cancellationToken = default) => throw new NotSupportedException("Fixture does not launch applications.");
        public Task OpenTerminalAsync(string directory, CancellationToken cancellationToken = default) => throw new NotSupportedException("Fixture does not launch applications.");
    }
}
