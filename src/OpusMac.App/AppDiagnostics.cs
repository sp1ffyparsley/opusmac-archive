using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Input.Raw;
using Avalonia.Platform.Storage;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using OpusMac.Core;
using OpusMac.Core.Persistence;
using OpusMac.Core.Integration;
using OpusMac.Core.Browsing;
using OpusMac.Core.Search;
using SkiaSharp;

namespace OpusMac.App;

internal static class AppDiagnostics
{
    // The Avalonia headless clipboard has file items but no native CF_HDROP or
    // Preferred DropEffect. Pair those items with an isolated, versioned
    // Windows-intent snapshot for the Paste UI test. Never publish to the OS.
    private sealed class ScriptedWindowsClipboardIntentReader : IWindowsClipboardIntentReader
    {
        private uint _sequence = 1000;
        private WindowsClipboardDropSnapshot? _snapshot;

        public uint CurrentSequence => _sequence;
        public WindowsClipboardDropSnapshot? Capture() => _snapshot;

        public void SetCopy(IReadOnlyList<string> paths) =>
            _snapshot = new WindowsClipboardDropSnapshot(++_sequence, paths, WindowsClipboardDropIntent.CopyEffect);

        public void SetMove(IReadOnlyList<string> paths) =>
            _snapshot = new WindowsClipboardDropSnapshot(++_sequence, paths, WindowsClipboardDropIntent.MoveEffect);

        public void Clear()
        {
            ++_sequence;
            _snapshot = null;
        }
    }

    public static Task<int> RunAsync(LaunchOptions options) =>
        RunAsync(options, Environment.GetEnvironmentVariable("OPUSMAC_DIAGNOSTIC_ONLY")?.Trim());

    private static async Task<int> RunAsync(LaunchOptions options, string? onlyCheck, List<Check>? suite = null)
    {
        var output = options.RenderDirectory is null ? null : Path.GetFullPath(options.RenderDirectory);
        if (output is not null) Directory.CreateDirectory(output);
        // Every run owns a unique fixture and profile, even when --state-dir is supplied.
        var root = Path.Combine(options.StateDirectory ?? Path.GetTempPath(), "opusmac-ui-check-" + Guid.NewGuid().ToString("N"));
        var left = Path.Combine(root, "Work"); var right = Path.Combine(root, "Destination");
        var state = Path.Combine(root, "State");
        Directory.CreateDirectory(left); Directory.CreateDirectory(right);
        Directory.CreateDirectory(Path.Combine(left, "Design")); Directory.CreateDirectory(Path.Combine(left, "Documents"));
        Directory.CreateDirectory(Path.Combine(right, "Archive"));
        await File.WriteAllTextAsync(Path.Combine(left, "Project notes.txt"), "Project notes\n\nDirectory Opus Mac\n\nSource and destination browsing.\nCopy, move, rename and undo use the same operation journal.\n");
        await File.WriteAllTextAsync(Path.Combine(left, "Research.md"), "# Research\nA content preview from a real fixture file.\n");
        await File.WriteAllTextAsync(Path.Combine(left, "Budget.csv"), "Item,Amount\nHardware,1200\nSoftware,180\n");
        await File.WriteAllTextAsync(Path.Combine(left, ".hidden-check"), "hidden");
        await File.WriteAllTextAsync(Path.Combine(left, "Design", "Layout.txt"), "Nested file\n");
        await File.WriteAllTextAsync(Path.Combine(right, "Readme.txt"), "The destination file display receives Copy Files and Move Files operations.");
        var launch = new LaunchOptions { StateDirectory = state };
        launch.Paths.Add(left); launch.Paths.Add(right);
        MainWindow? window = null;
        var checks = new List<Check>();
        var skipped = new List<string>();
        var stopwatch = Stopwatch.StartNew();
       const string indexedSizeCheck = "Indexed folder sizes use current Listary identity";
       const string rootReadinessCheck = "Indexed search explains rootless, disabled and offline folders in the actual lister";
       const string searchClearCheck = "Clearing a pending indexed search restores normal folder without late results";
       const string indexedFailureCaptureCheck = "Indexed Undo failure capture retains operation and journal evidence";
        const string longPathRoundTripCheck = "Windows long paths preserve Copy Move and cold-reopened Undo";
       const string flippedVideoCheck = "Video flip playback and image preview preserve originals and recover selection";
       const string malformedAppearanceCheck = "Malformed shared appearance falls back without mutation";
        var diagnosticPhase = "diagnostic-startup";
        string? indexedSourcePath = null;
        string? indexedDestinationPath = null;
        string? indexedSourceIdentityPath = null;
        string? indexedDestinationIdentityPath = null;
        string? indexedSourceHash = null;
        string? indexedOperationId = null;
        ListarySearchResult? indexedResultIdentity = null;
        var preserveDiagnosticRoot = false;
       var priorAppearanceRoot = Environment.GetEnvironmentVariable("SS_WORKFLOW_SETTINGS_ROOT");
       var priorAgentState = Environment.GetEnvironmentVariable("SPIFFY_LISTARY_STATE_DIR");
       DiagnosticIndexFixture? rootFixture = null;
        object CapturePathIdentity(string? path)
        {
            if (string.IsNullOrWhiteSpace(path)) return new { path, exists = false, error = "Path was not recorded." };
            try
            {
                var attributes = File.GetAttributes(path);
                var isDirectory = (attributes & FileAttributes.Directory) != 0;
                FileSystemInfo info = isDirectory ? new DirectoryInfo(path) : new FileInfo(path);
                info.Refresh();
                var length = isDirectory ? (long?)null : ((FileInfo)info).Length;
                var hash = !isDirectory && length <= 8 * 1024 * 1024
                    ? Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))) : null;
                return new
                {
                    path, exists = true, isDirectory, attributes = attributes.ToString(), length,
                    modifiedUtc = info.LastWriteTimeUtc,
                    nativeIdentity = ListaryClient.ReadNativeItemIdentity(path),
                    sha256 = hash, hashOmittedAboveBytes = 8 * 1024 * 1024
                };
            }
            catch (Exception captureError)
            {
                return new { path, exists = File.Exists(path) || Directory.Exists(path), errorType = captureError.GetType().FullName, error = captureError.Message };
            }
        }
        object CapturePaneState(PaneView pane) => new
        {
            currentDirectory = pane.CurrentDirectory, isSearchResults = pane.IsSearchResults,
            selected = pane.SelectedEntries.Select(entry =>
            {
                var identity = pane.IndexedIdentity(entry);
                return new
                {
                    entry.FullPath, entry.Name, entry.IsDirectory, entry.Length, entry.ModifiedUtc,
                    nativeIdentity = ListaryClient.ReadNativeItemIdentity(entry.FullPath),
                    indexedIdentity = identity is null ? null : new
                    {
                        identity.ItemId, identity.RootId, identity.StableFileId, identity.EndpointIdentity,
                        identity.IndexedKind, identity.IndexedSize, identity.IndexedModifiedUnixMilliseconds
                    }
                };
            }).ToArray()
        };
        object CaptureJournalState(bool includeSnapshot, out string? snapshotPath)
        {
            var journalPath = Path.Combine(state, "operations.json");
            snapshotPath = null;
            if (!File.Exists(journalPath)) return new { path = journalPath, exists = false };
            var journalInfo = new FileInfo(journalPath);
            using var stream = File.OpenRead(journalPath);
           var journalHash = Convert.ToHexString(SHA256.HashData(stream));
           if (journalInfo.Length > 8 * 1024 * 1024)
            {
                if (includeSnapshot && journalInfo.Length <= 64 * 1024 * 1024)
                {
                    snapshotPath = Path.Combine(output!, "indexed-undo-failure-journal.json");
                    File.Copy(journalPath, snapshotPath, true);
                }
                return new { path = journalPath, exists = true, length = journalInfo.Length, sha256 = journalHash,
                    summaryOmitted = "Journal exceeds the 8 MiB diagnostic parsing bound.", snapshotPath,
                    snapshotOmitted = journalInfo.Length > 64 * 1024 * 1024 ? "Journal exceeds the 64 MiB snapshot bound." : null };
            }
            var json = File.ReadAllText(journalPath);
            using var document = JsonDocument.Parse(json);
            var summaries = new List<object>();
            if (document.RootElement.TryGetProperty("Operations", out var operations) && operations.ValueKind == JsonValueKind.Array)
            {
                foreach (var operation in operations.EnumerateArray())
                {
                    var mutations = new List<object>();
                    if (operation.TryGetProperty("Mutations", out var items) && items.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var item in items.EnumerateArray())
                        {
                            string? ReadText(string property) => item.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
                            bool ReadFlag(string property) => item.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.True;
                            mutations.Add(new
                            {
                                source = ReadText("Source"), destination = ReadText("Destination"), action = ReadText("Action"), phase = ReadText("Phase"),
                                fingerprint = ReadText("Fingerprint"), sourceFingerprint = ReadText("SourceFingerprint"),
                                applied = ReadFlag("Applied"), sourceRemoved = ReadFlag("SourceRemoved"), undone = ReadFlag("Undone"),
                                recoveryRequired = ReadFlag("RecoveryRequired"), area = ReadText("Area"), stage = ReadText("Stage"),
                                backup = ReadText("Backup"), sourceHold = ReadText("SourceHold"), undoHold = ReadText("UndoHold"),
                                undoTrashPath = ReadText("UndoTrashPath")
                            });
                        }
                    }
                    string? OperationText(string property) => operation.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
                    var errors = operation.TryGetProperty("Errors", out var errorValues) && errorValues.ValueKind == JsonValueKind.Array
                        ? errorValues.EnumerateArray().Where(value => value.ValueKind == JsonValueKind.String).Select(value => value.GetString()).ToArray()
                        : Array.Empty<string?>();
                    summaries.Add(new { id = OperationText("Id"), kind = operation.TryGetProperty("Kind", out var kind) ? kind.ToString() : null,
                        status = OperationText("Status"), errors, mutations });
                }
            }
            if (includeSnapshot)
            {
                snapshotPath = Path.Combine(output!, "indexed-undo-failure-journal.json");
                File.Copy(journalPath, snapshotPath, true);
            }
            return new { path = journalPath, exists = true, length = journalInfo.Length, sha256 = journalHash, operations = summaries, snapshotPath };
        }
        async Task<string?> CaptureIndexedUndoEvidenceAsync(Exception error, bool probeOnly)
        {
            if (output is null) return null;
            string? journalSnapshotPath;
            var journal = CaptureJournalState(includeSnapshot: true, out journalSnapshotPath);
            var evidencePath = Path.Combine(output, probeOnly
                ? "indexed-undo-diagnostic-capture-contract.json"
                : "indexed-undo-failure-state.json");
            var evidence = new
            {
                schema = "opusmac_indexed_undo_failure_evidence_v1",
                captureMode = probeOnly ? "synthetic-contract-probe" : "observed-diagnostic-failure",
                failureClass = error is OperationCanceledException ? "cancellation" : error is IOException ? "filesystem-or-journal-io" : error is TimeoutException ? "timeout" : error is InvalidOperationException ? "diagnostic-assertion-or-state" : "unexpected",
                errorType = error.GetType().FullName, error = error.ToString(),
                diagnosticPhase, operationId = indexedOperationId ?? window?.LastOperation?.Id,
                indexedResultIdentity = indexedResultIdentity is null ? null : new
                {
                    indexedResultIdentity.ItemId, indexedResultIdentity.RootId, indexedResultIdentity.StableFileId,
                    indexedResultIdentity.EndpointIdentity, indexedResultIdentity.IndexedKind, indexedResultIdentity.IndexedSize,
                    indexedResultIdentity.IndexedModifiedUnixMilliseconds
                },
                sourceOperationPath = indexedSourcePath, destinationOperationPath = indexedDestinationPath,
                source = CapturePathIdentity(indexedSourceIdentityPath ?? indexedSourcePath),
                destination = CapturePathIdentity(indexedDestinationIdentityPath ?? indexedDestinationPath),
                sourceFolder = CapturePathIdentity(indexedSourcePath), destinationFolder = CapturePathIdentity(indexedDestinationPath),
                expectedSourceSha256 = indexedSourceHash,
                statusMessage = window?.StatusMessage, lastOperation = window?.LastOperation,
                undoResponse = diagnosticPhase.Contains("undo-result", StringComparison.OrdinalIgnoreCase)
                    ? window?.LastOperation : null,
                canUndo = window?.CanUndoOperation, operationHistory = window?.OperationHistory,
                activePane = window is null ? null : CapturePaneState(window.ActivePane),
                otherPane = window is null ? null : CapturePaneState(window.OtherPane),
                journal, journalSnapshotPath,
                recoveryDecision = probeOnly
                    ? "Synthetic capture-contract probe only; no recovery, Undo, Redo or replay was attempted."
                    : "Observation only; no automatic replay or recovery was attempted. Preserve the task-owned fixture and journal for diagnosis.",
                fixtureRoot = root, fixtureRootPreserved = !probeOnly,
                processId = Environment.ProcessId, platform = System.Runtime.InteropServices.RuntimeInformation.OSDescription,
                capturedAtUtc = DateTimeOffset.UtcNow, capturedAtLocal = DateTimeOffset.Now, localTimeZone = TimeZoneInfo.Local.Id
            };
            await File.WriteAllTextAsync(evidencePath, JsonSerializer.Serialize(evidence, new JsonSerializerOptions { WriteIndented = true }));
            return evidencePath;
        }
        if (onlyCheck == rootReadinessCheck || onlyCheck == indexedSizeCheck || onlyCheck == searchClearCheck)
        {
            rootFixture = new DiagnosticIndexFixture(Path.Combine(root, "DisposableListaryAgent"),
                Path.Combine(left, "Design"));
            Environment.SetEnvironmentVariable("SPIFFY_LISTARY_STATE_DIR", rootFixture.StateDirectory);
        }
        if (onlyCheck == "PDF preview renders pages and recovers from invalid input")
        {
            // This is disposable task-owned content. The main file operation owner never
            // modifies the user's installed Directory Opus documents or preferences.
            using (var stream = File.Create(Path.Combine(left, "Viewer fixture.pdf")))
            using (var document = SKDocument.CreatePdf(stream))
            using (var paint = new SKPaint { Color = SKColors.Black, TextSize = 26, IsAntialias = true })
            {
                for (var page = 1; page <= 2; page++)
                {
                    var canvas = document.BeginPage(380, 540);
                    canvas.Clear(SKColors.White);
                    canvas.DrawText($"Preview page {page}", 20, 90, paint);
                    document.EndPage();
                }
                document.Close();
            }
            await File.WriteAllTextAsync(Path.Combine(left, "Invalid fixture.pdf"), "This is not a PDF document.");
        }
        if (onlyCheck is "Reference bookmark toolbar navigates and updates when a favorite is added"
            or "Reference view toolbar controls the existing lister layout"
            or "Folder tree header collapses branches and hides the existing tree"
            or "Folder tree find opens exact loaded folder with keyboard and preserves focus"
            or "Reference breadcrumb preserves editable folder navigation and focus"
            or "Reference sort headers show direction across tabs and indexed results"
            or "Fresh installed-reference Details sorting respects saved tab overrides"
            or "Physical-size installed-reference viewer proportions preserve lister usability"
            or "Default details columns preserve installed reference proportions at wide DPI"
            or "Indexed clipboard retains occurrence order and copies relocated file with Undo"
            or "PDF preview renders pages and recovers from invalid input")
            await new SettingsStore(state).SaveAsync(new AppSettings
            {
                Theme = "Dark", SplitOrientation = "Horizontal",
                WindowWidth = onlyCheck is "Indexed clipboard retains occurrence order and copies relocated file with Undo"
                    or "Default details columns preserve installed reference proportions at wide DPI"
                    or "Fresh installed-reference Details sorting respects saved tab overrides" ? 2176 :
                    onlyCheck is "Physical-size installed-reference viewer proportions preserve lister usability" ? 1664 : 1080,
                WindowHeight = onlyCheck switch
                {
                    "Indexed clipboard retains occurrence order and copies relocated file with Undo" => 1190,
                    "Default details columns preserve installed reference proportions at wide DPI" => 1190,
                    "Fresh installed-reference Details sorting respects saved tab overrides" => 1190,
                    "Physical-size installed-reference viewer proportions preserve lister usability" => 910,
                    "Reference sort headers show direction across tabs and indexed results" => 728,
                    _ => 847
                }
            });
        async Task CheckAsync(string name, Func<Task> action)
        {
            if (!string.IsNullOrWhiteSpace(onlyCheck) && !string.Equals(name, onlyCheck, StringComparison.Ordinal))
                return;
            if (string.IsNullOrWhiteSpace(onlyCheck) &&
                (name == "Shared appearance preserves indexed navigation authority" &&
                 string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("OPUSMAC_SHARED_APPEARANCE_INDEXED_FOLDER")) ||
                 name == flippedVideoCheck && string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("OPUSMAC_VIDEO_PREVIEW_FIXTURE"))))
            {
                skipped.Add(name);
                Console.WriteLine("SKIP " + name + " (dedicated fixture unavailable)");
                return;
            }
            if (string.IsNullOrWhiteSpace(onlyCheck))
            {
                await RunAsync(options, name, checks);
                return;
            }
           try { await action(); checks.Add(new Check(name, true, null)); Console.WriteLine("PASS " + name); }
            catch (Exception error)
            {
                if (name is rootReadinessCheck or longPathRoundTripCheck && output is not null)
                {
                    preserveDiagnosticRoot = true;
                    try
                    {
                        var evidencePath = await CaptureIndexedUndoEvidenceAsync(error, probeOnly: false);
                        Console.Error.WriteLine("Indexed Undo failure evidence: " + evidencePath);
                    }
                    catch (Exception captureError)
                    {
                        Console.Error.WriteLine("Indexed Undo failure evidence capture failed: " + captureError);
                    }
                }
                checks.Add(new Check(name, false, error.ToString()));
                Console.Error.WriteLine("FAIL " + name + ": " + error.Message);
            }
        }
        try
        {
            if (onlyCheck == malformedAppearanceCheck)
            {
                var appearanceRoot = Directory.CreateDirectory(Path.Combine(root, "SharedAppearance")).FullName;
                await File.WriteAllTextAsync(Path.Combine(appearanceRoot, "appearance.json"), "{invalid appearance");
                await new SettingsStore(state).SaveAsync(new AppSettings { Theme = "Light" });
                Environment.SetEnvironmentVariable("SS_WORKFLOW_SETTINGS_ROOT", appearanceRoot);
            }
           window = new MainWindow(launch); window.Show(); await window.InitializeAsync();
          await CheckAsync(indexedFailureCaptureCheck, async () =>
          {
               diagnosticPhase = "synthetic-capture-contract-probe";
                var sourcePath = Path.Combine(left, "Indexed failure evidence – café.txt");
                var destinationPath = Path.Combine(right, Path.GetFileName(sourcePath));
                const string contents = "Task-owned failure-evidence probe; original must survive.\n";
                await File.WriteAllTextAsync(sourcePath, contents);
                await window.ActivePane.NavigateAsync(left);
                await window.OtherPane.NavigateAsync(right);
                window.ActivePane.SelectPaths([sourcePath]);
                diagnosticPhase = "capture-probe-copy-dispatch";
                Click(window, "Copy Files"); await window.LastCommand;
                Assert(File.Exists(destinationPath) &&
                    await File.ReadAllTextAsync(destinationPath) == contents &&
                    window.LastOperation is { Succeeded: true },
                    "The task-owned copy fixture did not commit before the diagnostic evidence probe.");
                indexedSourcePath = left;
                indexedDestinationPath = right;
                indexedSourceIdentityPath = sourcePath;
                indexedDestinationIdentityPath = destinationPath;
               indexedSourceHash = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(sourcePath)));
               indexedOperationId = window.LastOperation?.Id;
                diagnosticPhase = "capture-probe-after-committed-copy-before-undo";
               var evidencePath = await CaptureIndexedUndoEvidenceAsync(
                    new InvalidOperationException("Synthetic capture probe after a committed task-owned Copy; no recovery was attempted."),
                   probeOnly: true);
               Assert(evidencePath is not null && File.Exists(evidencePath),
                    "The indexed Undo diagnostic did not persist its bounded evidence contract.");
                using var document = JsonDocument.Parse(await File.ReadAllTextAsync(evidencePath!));
                var captured = document.RootElement;
               Assert(captured.GetProperty("schema").GetString() == "opusmac_indexed_undo_failure_evidence_v1" &&
                   captured.GetProperty("captureMode").GetString() == "synthetic-contract-probe" &&
                   captured.GetProperty("failureClass").GetString() == "diagnostic-assertion-or-state" &&
                   captured.GetProperty("recoveryDecision").GetString()!.Contains("no recovery", StringComparison.OrdinalIgnoreCase) &&
                    captured.GetProperty("operationId").GetString() == indexedOperationId &&
                    captured.GetProperty("expectedSourceSha256").GetString() == indexedSourceHash &&
                    captured.GetProperty("source").GetProperty("nativeIdentity").GetString() is not null &&
                    captured.GetProperty("destination").GetProperty("sha256").GetString() == indexedSourceHash &&
                    captured.GetProperty("journal").GetProperty("exists").GetBoolean() &&
                    captured.GetProperty("journal").GetProperty("operations").GetArrayLength() == 1 &&
                    File.Exists(captured.GetProperty("journalSnapshotPath").GetString()),
                    "The diagnostic evidence omitted operation identity, filesystem identity/hash, journal summary or exact journal snapshot.");
               Console.WriteLine("METRIC indexed_failure_capture_bytes=" + new FileInfo(evidencePath!).Length);
                diagnosticPhase = "capture-probe-undo";
                Click(window, "Undo"); await window.LastCommand;
                diagnosticPhase = "capture-probe-undo-result-after-undo";
                var afterUndoEvidencePath = await CaptureIndexedUndoEvidenceAsync(
                    new InvalidOperationException("Synthetic capture probe after completed Copy Undo; no recovery was attempted."),
                    probeOnly: true);
                Assert(afterUndoEvidencePath is not null && File.Exists(afterUndoEvidencePath),
                    "The indexed Undo diagnostic did not persist its post-Undo evidence.");
                using var afterUndoDocument = JsonDocument.Parse(await File.ReadAllTextAsync(afterUndoEvidencePath!));
                var afterUndo = afterUndoDocument.RootElement;
                Assert(afterUndo.GetProperty("operationId").GetString() == indexedOperationId &&
                    afterUndo.GetProperty("undoResponse").GetProperty("Succeeded").GetBoolean() &&
                    !afterUndo.GetProperty("destination").GetProperty("exists").GetBoolean() &&
                    afterUndo.GetProperty("source").GetProperty("sha256").GetString() == indexedSourceHash &&
                    afterUndo.GetProperty("journal").GetProperty("operations")[0].GetProperty("status").GetString() == "Undone" &&
                    File.Exists(afterUndo.GetProperty("journalSnapshotPath").GetString()),
                    "The post-Undo evidence omitted the successful response, filesystem state or Undone journal snapshot.");
                Assert(File.Exists(sourcePath) && !File.Exists(destinationPath) &&
                    Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(sourcePath))) == indexedSourceHash,
                    "The diagnostic evidence probe did not restore the original fixture after capturing the journal.");
          });
            await CheckAsync(longPathRoundTripCheck, async () =>
            {
                Assert(OperatingSystem.IsWindows(), "The long-path integration fixture is Windows-specific.");
                var segments = Enumerable.Range(0, 5).Select(index =>
                    $"Long path segment {index:D2} – {new string((char)('a' + index), 38)}").ToArray();
                var sourceFolder = segments.Aggregate(left, (current, segment) => Path.Combine(current, segment));
               var destinationFolder = segments.Aggregate(right, (current, segment) => Path.Combine(current, segment));
                var moveDestinationFolder = segments.Aggregate(Path.Combine(right, "Moved"), (current, segment) => Path.Combine(current, segment));
               Directory.CreateDirectory(sourceFolder);
               Directory.CreateDirectory(destinationFolder);
                Directory.CreateDirectory(moveDestinationFolder);
               var sourcePath = Path.Combine(sourceFolder, "Unicode notes – café 東京.txt");
               var destinationPath = Path.Combine(destinationFolder, Path.GetFileName(sourcePath));
                indexedSourcePath = sourceFolder;
                indexedDestinationPath = destinationFolder;
                indexedSourceIdentityPath = sourcePath;
                indexedDestinationIdentityPath = destinationPath;
                const string contents = "Windows long-path Copy/Move/Undo round trip.\nUnicode: café 東京.\n";
                await File.WriteAllTextAsync(sourcePath, contents);
                var sourceLength = sourcePath.Length;
                Assert(sourceLength > 260 && File.Exists(sourcePath),
                    $"The fixture did not exceed the traditional MAX_PATH boundary: {sourceLength} characters.");
               var expectedHash = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(sourcePath)));
                indexedSourceHash = expectedHash;
                await window.ActivePane.NavigateAsync(sourceFolder);
                await window.OtherPane.NavigateAsync(destinationFolder);
                window.ActivePane.SelectPaths([sourcePath]);
                Click(window, "Copy Files"); await window.LastCommand;
                Assert(window.LastOperation is { Succeeded: true } && File.Exists(destinationPath) &&
                    Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(destinationPath))) == expectedHash,
                    "Copy did not preserve the Unicode long-path file and exact content hash.");
                var copyId = window.LastOperation!.Id;
                await window.CloseForDiagnosticsAsync();
                window = new MainWindow(launch); window.Show(); await window.InitializeAsync();
               Assert(window.OperationHistory.Any(item => item.Id == copyId && item.CanUndo),
                   "Cold reopen lost the long-path Copy journal or Undo authorization.");
                indexedOperationId = copyId;
                diagnosticPhase = "long-path-copy-undo-after-reopen";
                Click(window, "Undo"); await window.LastCommand;
                Assert(window.LastOperation is { Succeeded: true } && !File.Exists(destinationPath) &&
                    File.Exists(sourcePath) && Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(sourcePath))) == expectedHash,
                    "Cold-reopened Undo did not remove only the copied long-path destination.");
               await window.ActivePane.NavigateAsync(sourceFolder);
                await window.OtherPane.NavigateAsync(moveDestinationFolder);
               window.ActivePane.SelectPaths([sourcePath]);
               Click(window, "Move Files"); await window.LastCommand;
                indexedOperationId = window.LastOperation?.Id;
                diagnosticPhase = "long-path-move-committed";
               Assert(window.LastOperation is { Succeeded: true } && !File.Exists(sourcePath) &&
                    File.Exists(Path.Combine(moveDestinationFolder, Path.GetFileName(sourcePath))) &&
                    Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(Path.Combine(moveDestinationFolder, Path.GetFileName(sourcePath))))) == expectedHash,
                   "Move did not preserve the long-path payload at its destination.");
                var moveId = window.LastOperation!.Id;
                await window.CloseForDiagnosticsAsync();
                window = new MainWindow(launch); window.Show(); await window.InitializeAsync();
                Assert(window.OperationHistory.Any(item => item.Id == moveId && item.CanUndo),
                    "Cold reopen lost the long-path Move journal or Undo authorization.");
               Click(window, "Undo"); await window.LastCommand;
                diagnosticPhase = "long-path-move-undo-result";
               Assert(window.LastOperation is { Succeeded: true } && File.Exists(sourcePath) &&
                   !File.Exists(Path.Combine(moveDestinationFolder, Path.GetFileName(sourcePath))) &&
                   Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(sourcePath))) == expectedHash,
                    "Cold-reopened Move Undo did not restore the original long-path file.");
                Console.WriteLine($"METRIC windows_long_path_characters={sourceLength} windows_long_path_copy_move_undo=PASS");
            });
            await CheckAsync("Physical-size installed-reference viewer proportions preserve lister usability", async () =>
            {
                var workspace = window.GetVisualDescendants().OfType<Grid>()
                    .Single(grid => grid.Children.OfType<GridSplitter>().Count() == 2 &&
                        grid.ColumnDefinitions.Count == 5);
                (double Tree, double Viewer, double Center) Geometry()
                {
                    var columns = workspace.ColumnDefinitions;
                    var tree = columns[0].Width.Value;
                    var viewer = columns[4].Width.Value;
                    return (tree, viewer, window.Width - tree - viewer - columns[1].Width.Value - columns[3].Width.Value);
                }
                if (output is not null)
                    SaveFrame(window, Path.Combine(output, "opusmac-physical-reference-1664x910.png"));
                var (tree, viewer, center) = Geometry();
                Assert(Math.Abs(tree - 180) <= 1,
                    $"At physical 1664px the tree consumes {tree:F1}px rather than the observed ~180px native rail.");
                Assert(Math.Abs(viewer - 590.72) <= 3,
                    $"At physical 1664px the viewer {viewer:F1}px shifts the observed native ~1073px boundary.");
                Assert(center >= 800,
                    "The indexed file listers lack sufficient center width for Details columns.");
                window.Dispatch("tree"); await window.LastCommand;
                Assert(!window.TreeVisible && Math.Abs(Geometry().Tree) < .1,
                    "Hiding the tree kept space reserved for the hidden rail.");
                window.Dispatch("tree"); await window.LastCommand;
                Assert(window.TreeVisible && Math.Abs(Geometry().Tree - 180) <= 1,
                    "Restoring tree visibility lost the source-measured original-reference rail width.");
                Assert(window.ActivePane.VisibleEntries.Count > 0,
                    "Responsive lister changes lost the loaded real disposable files.");
                DragSplitter(window, "ViewerSplitter", new Vector(-60, 0));
                await window.SaveSettingsAsync();
                var customWidth = new SettingsStore(state).Load().ViewerWidth;
                Assert(customWidth is { } saved && Math.Abs(saved - (viewer + 60)) <= 5,
                    $"The actual Viewer drag did not persist its width (saved {customWidth}, original {viewer}).");
                window.Dispatch("viewer"); await window.LastCommand;
                window.Dispatch("viewer"); await window.LastCommand;
                Assert(Math.Abs(Geometry().Viewer - customWidth.GetValueOrDefault()) <= 1,
                    "Toggling the Viewer discarded its custom width.");
                DragSplitter(window, "TreeSplitter", new Vector(45, 0));
                await window.SaveSettingsAsync();
                var treeWidth = new SettingsStore(state).Load().TreeWidth;
                Assert(treeWidth is { } savedTree && Math.Abs(savedTree - 225) <= 5,
                    $"The Folder Tree drag did not persist its width (saved {treeWidth}).");
                window.Dispatch("tree"); await window.LastCommand;
                window.Dispatch("tree"); await window.LastCommand;
                Assert(Math.Abs(Geometry().Tree - treeWidth.GetValueOrDefault()) <= 1,
                    "Toggling the Folder Tree discarded its custom width.");
                window.Dispatch("horizontal"); await window.LastCommand;
                DragSplitter(window, "FilePaneSplitter", new Vector(0, 55));
                window.Dispatch("vertical"); await window.LastCommand;
                DragSplitter(window, "FilePaneSplitter", new Vector(-45, 0));
                await window.SaveSettingsAsync();
                var splitSettings = new SettingsStore(state).Load();
                Assert(splitSettings.StackedPaneShare > .5 && splitSettings.SideBySidePaneShare < .5,
                    $"The dual-pane splits were not independently saved ({splitSettings.StackedPaneShare}, {splitSettings.SideBySidePaneShare}).");
                window.Dispatch("horizontal"); await window.LastCommand;
                window.UpdateLayout();
                var firstPane = window.ActivePaneIndex == 0 ? window.ActivePane : window.OtherPane;
                var secondPane = window.ActivePaneIndex == 0 ? window.OtherPane : window.ActivePane;
                var displayedShare = firstPane.Bounds.Height / (firstPane.Bounds.Height + secondPane.Bounds.Height);
                Assert(Math.Abs(displayedShare - splitSettings.StackedPaneShare) < .01,
                    "Switching back to stacked panes lost the saved divider position.");
                if (output is not null)
                    File.WriteAllText(Path.Combine(output, "native-proportion-measurements.json"),
                        JsonSerializer.Serialize(new { Width = window.Width, Height = window.Height,
                            Tree = Geometry().Tree, Viewer = Geometry().Viewer, Center = Geometry().Center,
                            ReferenceNativeTreeApprox = 180, ReferenceNativeViewerStartApprox = 1073 },
                            new JsonSerializerOptions { WriteIndented = true }));
            });
            await CheckAsync("Fresh installed-reference Details sorting respects saved tab overrides", async () =>
            {
                // Exact installed 13.22.11 wide dark reference shows both listers
                // sorted by Modified descending. Earlier fresh Avalonia defaults
                // highlighted Name ascending instead. Existing saved tabs must win.
                var pane = window.ActivePane;
                Assert(pane.CurrentSortColumn == "Modified" && pane.CurrentSortDescending,
                    "Fresh details tab does not match the observed native Modified-descending sort.");
                Assert(window.OtherPane.CurrentSortColumn == "Modified" && window.OtherPane.CurrentSortDescending,
                    "The second fresh lister unexpectedly uses a different sort.");
                if (output is not null) SaveFrame(window, Path.Combine(output, "opusmac-modified-default-2176x1190.png"));
                await pane.AddTabAsync(right);
                Assert(pane.CurrentSortColumn == "Modified" && pane.CurrentSortDescending,
                    "A new tab lost the current installed-reference default.");
                var name = pane.FileGrid.Columns.Cast<DataGridColumn>()
                    .Select(column => column.Header).OfType<Button>()
                    .Single(button => AutomationProperties.GetName(button) == "Sort by Name");
                name.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await pane.FilteringTask;
                Assert(pane.CurrentSortColumn == "Name" && !pane.CurrentSortDescending,
                    "Explicit per-tab Name ascending override did not take effect.");
                await window.SaveSettingsAsync();
                var loaded = new SettingsStore(state).Load();
                Assert(loaded.Panes[window.ActivePaneIndex].Tabs.Single(tab => tab.SortColumn == "Name").Descending == false,
                    "Explicit persisted Name sort was overwritten by the fresh default.");
            });
            await CheckAsync("Reference sort headers show direction across tabs and indexed results", async () =>
            {
                var pane = window.ActivePane;
                Button Header(string id) => pane.FileGrid.Columns.Cast<DataGridColumn>()
                    .Select(column => column.Header).OfType<Button>()
                    .Single(button => (AutomationProperties.GetName(button) ?? "").StartsWith(id + ", sorted", StringComparison.Ordinal)
                        || string.Equals(AutomationProperties.GetName(button), "Sort by " + id, StringComparison.Ordinal));
                void AssertSort(string id, bool descending)
                {
                    var selected = pane.FileGrid.Columns.Cast<DataGridColumn>()
                        .Select(column => column.Header).OfType<Button>()
                        .Where(button => button.Classes.Contains("sorted-column")).ToArray();
                    Assert(selected.Length == 1, "Exactly one folder-sort header must be visibly highlighted.");
                    Assert(AutomationProperties.GetName(selected[0]) == $"{id}, sorted {(descending ? "descending" : "ascending")}",
                        "The highlighted header does not expose its column and direction to accessibility.");
                    Assert(selected[0].Content is Grid grid && grid.Children.OfType<Avalonia.Controls.Shapes.Path>().Count() == 1,
                        "The active column is missing its visible sort-direction marker.");
                }
                AssertSort("Modified", true);
                Header("Modified").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await pane.FilteringTask;
                Assert(pane.CurrentSortColumn == "Modified" && !pane.CurrentSortDescending,
                    "The first header click did not select Modified ascending.");
                AssertSort("Modified", false);
                Header("Modified").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await pane.FilteringTask;
                Assert(pane.CurrentSortDescending, "The second header click did not reverse Modified sort.");
                AssertSort("Modified", true);
                if (output is not null) SaveFrame(window, Path.Combine(output, "opusmac-modified-descending-1080x728.png"));

                await pane.AddTabAsync(right);
                AssertSort("Modified", true);
                pane.NextTab(-1);
                await pane.LoadingTask;
                Assert(pane.CurrentSortColumn == "Modified" && pane.CurrentSortDescending,
                    "Returning to the original tab lost its sort settings.");
                AssertSort("Modified", true);

                var entries = pane.VisibleEntries.Take(2).ToArray();
                var indexed = entries.Select((entry, index) => new ListarySearchResult(entry, (uint)(index + 1),
                    Guid.Parse($"00000000-0000-4000-8000-{index + 1:000000000000}"),
                    $"sort-header-diagnostic:{index + 1}", "file", "sort-header-fixture", entry.Length,
                    entry.ModifiedUtc.ToUnixTimeMilliseconds())).ToArray();
                pane.ShowResults(entries, "Indexed provider order", indexed);
                await pane.LoadingTask;
                Assert(!pane.FileGrid.Columns.Cast<DataGridColumn>()
                    .Select(column => column.Header).OfType<Button>()
                    .Any(button => button.Classes.Contains("sorted-column")),
                    "Indexed provider ranking is incorrectly presented as local folder sorting.");
                await pane.NavigateAsync(left);
                AssertSort("Modified", true);
            });
            await CheckAsync("Default details columns preserve installed reference proportions at wide DPI", async () =>
            {
                // This fresh disposable profile represents the clone at the
                // installed Opus 13.22.11 reference's 2176×1190/96-DPI size.
                // The actual folders differ; compare layout, not file pixels.
                var pane = window.ActivePane;
                var formatIndicator = pane.GetVisualDescendants().OfType<Button>()
                    .Single(control => control.Name == "FolderFormatStatus");
                var freeSpace = pane.GetVisualDescendants().OfType<TextBlock>()
                    .Single(control => control.Name == "FolderFreeSpaceStatus");
                var diskCapacity = pane.GetVisualDescendants().OfType<ProgressBar>()
                    .Single(control => control.Name == "FolderDiskCapacity");
                window.UpdateLayout();
                var indicatorOrigin = formatIndicator.TranslatePoint(new Point(0, 0), pane);
                var freeSpaceOrigin = freeSpace.TranslatePoint(new Point(0, 0), pane);
                Assert(indicatorOrigin is { } indicator && freeSpaceOrigin is { } free &&
                    indicator.X > pane.Bounds.Width * 0.75 && free.X > indicator.X,
                    $"The pane status must place the format indicator before disk space on the right (indicator {indicatorOrigin}, free space {freeSpaceOrigin}, width {pane.Bounds.Width}).");
                var capacityOrigin = diskCapacity.TranslatePoint(new Point(0, 0), pane);
                Assert(diskCapacity.IsVisible && diskCapacity.Value is >= 0 and <= 100 &&
                    capacityOrigin is { } capacity && freeSpaceOrigin is { } text && capacity.X > text.X &&
                    AutomationProperties.GetName(diskCapacity)?.Contains(" free of ", StringComparison.Ordinal) == true,
                    "The capacity meter was missing, mispositioned, or lacked its accessible space summary.");
                if (output is not null)
                    SaveFrame(window, Path.Combine(output, "opusmac-column-layout-2176x1190.png"));
                var labels = pane.VisibleColumnIds.ToArray();
                Assert(labels.SequenceEqual(new[] { "Name", "Size", "Type", "Modified", "Attr" }),
                    "Fresh default file details must retain the canonical five-column order.");
                var widths = pane.FileGrid.Columns.Select(column => column.ActualWidth).ToArray();
                var total = widths.Sum();
                Assert(total > 1000, "The wide native-reference-sized file details were not laid out.");
                var shares = widths.Select(width => Math.Round(width / total, 4)).ToArray();
                if (output is not null)
                    File.WriteAllText(Path.Combine(output, "column-widths.json"), JsonSerializer.Serialize(
                        new { viewport_width = window.Width, viewport_height = window.Height, labels,
                              actual_widths = widths, normalized_shares = shares },
                        new JsonSerializerOptions { WriteIndented = true }));
                Assert(shares[0] is >= 0.40 and <= 0.52 && shares[2] is >= 0.18 and <= 0.24 &&
                       shares[4] is >= 0.13 and <= 0.21,
                    $"Default column shares differ from installed reference: {string.Join(", ", shares)}");
                Assert(widths[1] >= 82, "Size heading clips at the current default width.");
                await Task.CompletedTask;
            });
            await CheckAsync("PDF preview renders pages and recovers from invalid input", async () =>
            {
                var directory = await new DirectoryBrowser().ReadAsync(left, new BrowseOptions());
                var pdf = directory.Entries.Single(entry => entry.Name == "Viewer fixture.pdf");
                var invalid = directory.Entries.Single(entry => entry.Name == "Invalid fixture.pdf");
                Application.Current!.RequestedThemeVariant = ThemeVariant.Dark;
                window.ActivePane.SelectPaths([pdf.FullPath]);
                await WaitAsync(() => window.Viewer.DisplayedPdfPageCount == 2);
                await window.Viewer.LoadingTask;
                Assert(window.Viewer.HasRenderedPdfPage && window.Viewer.DisplayedPdfPage == 1 &&
                    window.Viewer.DisplayedPdfPageCount == 2, "The PDF viewer did not render its first page.");
                if (output is not null) SaveFrame(window, Path.Combine(output, "opusmac-pdf-page-1.png"));
                window.Viewer.ChangePdfPage(1);
                await window.Viewer.LoadingTask;
                Assert(window.Viewer.HasRenderedPdfPage && window.Viewer.DisplayedPdfPage == 2 &&
                    window.Viewer.DisplayedPdfPageCount == 2, "The PDF viewer did not navigate to page two.");
                if (output is not null) SaveFrame(window, Path.Combine(output, "opusmac-pdf-page-2.png"));
                window.Viewer.ChangePdfPage(1);
                Assert(window.Viewer.DisplayedPdfPage == 2, "Next at the last page must be disabled.");
                window.ActivePane.SelectPaths([invalid.FullPath]);
                await WaitAsync(() => window.Viewer.PreviewStatus.StartsWith("Preview unavailable:", StringComparison.Ordinal));
                await window.Viewer.LoadingTask;
                Assert(!window.Viewer.HasRenderedPdfPage &&
                    window.Viewer.PreviewStatus.StartsWith("Preview unavailable:", StringComparison.Ordinal),
                    "Invalid PDF input did not display a recoverable error.");
                window.ActivePane.SelectPaths([pdf.FullPath]);
                await WaitAsync(() => window.Viewer.DisplayedPdfPageCount == 2);
                await window.Viewer.LoadingTask;
                Assert(window.Viewer.HasRenderedPdfPage && window.Viewer.DisplayedPdfPage == 1,
                    "The viewer did not recover after invalid PDF input.");
            });
            await CheckAsync("Shared appearance preserves indexed navigation authority", async () =>
            {
                var expectedFolder = Environment.GetEnvironmentVariable("OPUSMAC_SHARED_APPEARANCE_INDEXED_FOLDER")?.Trim();
                if (string.IsNullOrWhiteSpace(expectedFolder))
                    throw new InvalidOperationException("OPUSMAC_SHARED_APPEARANCE_INDEXED_FOLDER is required for this focused qualification.");
                expectedFolder = Path.GetFullPath(expectedFolder);
                Assert(window.UsesSharedAppearance, "Directory Opus did not admit the canonical shared appearance settings.");
                Assert(window.AppearanceProfile == "listary_slate", $"Unexpected shared appearance profile: {window.AppearanceProfile}");
                Assert(window.AppearanceStatus == "shared_loaded", $"Unexpected shared appearance status: {window.AppearanceStatus}");
                var appearanceBytesBefore = await File.ReadAllBytesAsync(window.AppearanceSettingsPath);

                window.Dispatch("settings");
                var preferences = await FindDialogAsync(window, "Preferences");
                var preferenceText = preferences.GetVisualDescendants().OfType<TextBlock>()
                    .Select(block => block.Text ?? "").ToArray();
                Assert(preferenceText.Any(text => text.Contains("Listary Slate · shared", StringComparison.Ordinal)),
                    "Preferences did not identify Listary Slate as the shared appearance authority.");
                Assert(preferenceText.Any(text => text.Contains("does not write that file", StringComparison.OrdinalIgnoreCase)),
                    "Preferences did not explain the read-only shared appearance contract.");
                var preferenceCombos = preferences.GetVisualDescendants().OfType<ComboBox>().Where(combo => combo.IsVisible).ToArray();
                Assert(preferenceCombos.Length == 1 &&
                    preferenceCombos[0].ItemsSource?.Cast<object>().Select(item => item?.ToString()).SequenceEqual(new[] { "Vertical", "Horizontal" }) == true,
                    "Preferences still exposed a competing local theme selector while shared appearance was authoritative.");
                if (output is not null) SaveFrame(preferences, Path.Combine(output, "opusmac-shared-appearance-preferences.png"), true);
                Click(preferences, "Cancel");
                await window.LastCommand;
                var appearanceBytesAfter = await File.ReadAllBytesAsync(window.AppearanceSettingsPath);
                Assert(appearanceBytesBefore.SequenceEqual(appearanceBytesAfter), "Directory Opus rewrote the shared appearance settings.");

                var scope = window.GetVisualDescendants().OfType<ComboBox>().First(box => box.Name == "SearchScope");
                var query = window.GetVisualDescendants().OfType<TextBox>().First(box => box.Name == "FileSearch");
                scope.SelectedIndex = 2;
                query.Text = Path.GetFileName(Path.TrimEndingDirectorySeparator(expectedFolder));
                var searchTimer = Stopwatch.StartNew();
                Click(window, "Find");
                await window.LastCommand;
                searchTimer.Stop();
                var comparison = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
                    ? StringComparison.OrdinalIgnoreCase
                    : StringComparison.Ordinal;
                var indexed = window.ActivePane.VisibleEntries.SingleOrDefault(entry =>
                    string.Equals(entry.FullPath, expectedFolder, comparison))
                    ?? throw new InvalidOperationException("The real Listary query did not return the exact indexed folder.");
                var identity = window.ActivePane.IndexedIdentity(indexed)
                    ?? throw new InvalidOperationException("The indexed folder lost its canonical Listary identity before navigation.");
                Assert(identity.RootId != Guid.Empty && identity.ItemId != 0 && !string.IsNullOrWhiteSpace(identity.EndpointIdentity),
                    "The indexed result identity is incomplete.");

                window.ActivePane.SelectPaths([indexed.FullPath]);
                var navigationTimer = Stopwatch.StartNew();
                window.Dispatch("open");
                await window.LastCommand;
                await WaitAsync(() => string.Equals(window.ActivePane.CurrentDirectory, expectedFolder, comparison));
                navigationTimer.Stop();
                var retained = window.ActivePane.CurrentDirectoryIndexedIdentity
                    ?? throw new InvalidOperationException("Indexed navigation did not retain the canonical directory identity.");
                Assert(retained.ItemId == identity.ItemId && retained.RootId == identity.RootId &&
                    string.Equals(retained.StableFileId, identity.StableFileId, StringComparison.Ordinal) &&
                    string.Equals(retained.EndpointIdentity, identity.EndpointIdentity, StringComparison.Ordinal),
                    "Action-time revalidation/navigation changed the selected Listary identity.");

                if (output is not null)
                {
                    window.Width = 1400; window.Height = 860;
                    await Task.Delay(50);
                    SaveFrame(window, Path.Combine(output, "opusmac-shared-appearance-indexed-navigation.png"));
                    var receipt = new
                    {
                        schema = "opusmac_shared_appearance_listary_workflow_v1",
                        appearance = new
                        {
                            schema = "spiffy_shared_appearance_v1",
                            profile = window.AppearanceProfile,
                            status = window.AppearanceStatus,
                            settings_path = window.AppearanceSettingsPath,
                            settings_sha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(appearanceBytesBefore)),
                            shared_settings_unchanged = appearanceBytesBefore.SequenceEqual(appearanceBytesAfter),
                            local_theme_writer_exposed = false
                        },
                        search = new
                        {
                            query = query.Text,
                            elapsed_ms = searchTimer.Elapsed.TotalMilliseconds,
                            result_path = indexed.FullPath,
                            identity.ItemId,
                            identity.RootId,
                            identity.StableFileId,
                            identity.EndpointIdentity
                        },
                        navigation = new
                        {
                            elapsed_ms = navigationTimer.Elapsed.TotalMilliseconds,
                            current_directory = window.ActivePane.CurrentDirectory,
                            retained_item_id = retained.ItemId,
                            retained_root_id = retained.RootId,
                            retained_stable_file_id = retained.StableFileId,
                            retained_endpoint_identity = retained.EndpointIdentity,
                            action_time_identity_revalidated = true
                        },
                        utc = DateTimeOffset.UtcNow
                    };
                    await File.WriteAllTextAsync(
                        Path.Combine(output, "shared-appearance-listary-workflow.json"),
                        JsonSerializer.Serialize(receipt, new JsonSerializerOptions { WriteIndented = true }));
                }
            });
            await CheckAsync(malformedAppearanceCheck, async () =>
            {
                Assert(!window.UsesSharedAppearance, "Malformed shared appearance unexpectedly remained authoritative.");
                Assert(window.AppearanceStatus == "shared_invalid", $"Unexpected malformed-settings status: {window.AppearanceStatus}");
                Assert(Application.Current!.RequestedThemeVariant == ThemeVariant.Light,
                    "Malformed shared appearance did not fall back to the legacy Light preference.");
                var appearanceBytesBefore = await File.ReadAllBytesAsync(window.AppearanceSettingsPath);

                window.Dispatch("settings");
                var preferences = await FindDialogAsync(window, "Preferences");
                var themeSelector = preferences.GetVisualDescendants().OfType<ComboBox>()
                    .Where(combo => combo.IsVisible)
                    .FirstOrDefault(combo => combo.ItemsSource?.Cast<object>()
                        .Select(item => item?.ToString()).SequenceEqual(new[] { "Dark", "Light" }) == true);
                Assert(themeSelector is not null && string.Equals(themeSelector.SelectedItem?.ToString(), "Light", StringComparison.Ordinal),
                    "Fallback Preferences did not restore the legacy Light selector.");
                if (output is not null) SaveFrame(preferences, Path.Combine(output, "opusmac-shared-appearance-invalid-fallback.png"), true);
                Click(preferences, "Cancel");
                await window.LastCommand;
                var appearanceBytesAfter = await File.ReadAllBytesAsync(window.AppearanceSettingsPath);
                Assert(appearanceBytesBefore.SequenceEqual(appearanceBytesAfter), "Directory Opus rewrote malformed shared appearance settings during recovery.");

                if (output is not null)
                {
                    var receipt = new
                    {
                        schema = "opusmac_shared_appearance_fallback_v1",
                        status = window.AppearanceStatus,
                        fallback_theme = "Light",
                        settings_path = window.AppearanceSettingsPath,
                        settings_sha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(appearanceBytesBefore)),
                        shared_settings_unchanged = appearanceBytesBefore.SequenceEqual(appearanceBytesAfter),
                        legacy_theme_selector_restored = true,
                        utc = DateTimeOffset.UtcNow
                    };
                    await File.WriteAllTextAsync(
                        Path.Combine(output, "shared-appearance-invalid-fallback.json"),
                        JsonSerializer.Serialize(receipt, new JsonSerializerOptions { WriteIndented = true }));
                }
            });
            await CheckAsync("Real directory listings and source/destination", async () =>
            {
                await WaitAsync(() => window.ActivePane.VisibleEntries.Any(entry => entry.Name == "Project notes.txt"));
                Assert(window.OtherPane.VisibleEntries.Any(entry => entry.Name == "Readme.txt"), "Destination did not load.");
                Assert(!window.ActivePane.VisibleEntries.Any(entry => entry.Name == ".hidden-check"), "Hidden entries are visible by default.");
            });
            await CheckAsync("Reference bookmark toolbar navigates and updates when a favorite is added", async () =>
            {
                var pane = window.ActivePane;
                var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                var builtIn = window.GetVisualDescendants().OfType<Button>().Single(button =>
                    button.Name == "BookmarkShortcut" &&
                    string.Equals(AutomationProperties.GetName(button), "Home", StringComparison.Ordinal));
                Assert(string.Equals(builtIn.Tag as string, home, StringComparison.Ordinal), "Home bookmark targets a different location.");

                window.Dispatch("favorite");
                var prompt = await FindDialogAsync(window, "Add Favorite");
                prompt.GetVisualDescendants().OfType<TextBox>().Single().Text = "Review workspace";
                Click(prompt, "Add");
                await window.LastCommand;
                var favorite = window.GetVisualDescendants().OfType<Button>().Single(button =>
                    button.Name == "BookmarkShortcut" &&
                    string.Equals(AutomationProperties.GetName(button), "Review workspace", StringComparison.Ordinal));
                Assert(string.Equals(favorite.Tag as string, left, StringComparison.Ordinal), "New favorite targets a different directory.");
                await pane.NavigateAsync(right);
                favorite.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await WaitAsync(() => string.Equals(pane.CurrentDirectory, left, StringComparison.Ordinal));
                Assert(pane.VisibleEntries.Any(entry => entry.Name == "Research.md"), "Favorite did not load the destination's actual entries.");
                if (output is not null)
                {
                    SaveFrame(window, Path.Combine(output, "opusmac-reference-bookmark-strip-1080x847.png"));
                }
                var modifiers = RawInputModifiers.Control | RawInputModifiers.Shift;
                pane.FocusList();
                window.KeyPressQwerty(PhysicalKey.B, modifiers);
                window.KeyReleaseQwerty(PhysicalKey.B, modifiers);
                await window.LastCommand;
                Control Bar() => window.GetVisualDescendants().OfType<Control>().Single(control => control.Name == "FavoritesBar");
                Assert(!Bar().IsVisible, "Ctrl+Shift+B did not hide the Favorites Bar.");
                await window.CloseForDiagnosticsAsync();
                window = new MainWindow(new LaunchOptions { StateDirectory = state });
                window.Show(); await window.InitializeAsync();
                Assert(!Bar().IsVisible, "Relaunch did not preserve the hidden Favorites Bar.");
                window.ActivePane.FocusList();
                window.KeyPressQwerty(PhysicalKey.B, modifiers);
                window.KeyReleaseQwerty(PhysicalKey.B, modifiers);
                await window.LastCommand;
                Assert(Bar().IsVisible && Bar().GetVisualDescendants().OfType<Button>().Any(button =>
                    AutomationProperties.GetName(button) == "Review workspace" && Equals(button.Tag, left)),
                    "Showing the Favorites Bar after relaunch did not restore the saved bookmark.");
            });
            await CheckAsync("Paired Folders supply dual display, Navigation Lock and Synchronize targets", async () =>
            {
                var leftChild = Path.Combine(left, "Design");
                var rightChild = Directory.CreateDirectory(Path.Combine(right, "Design")).FullName;
                var source = window.ActivePane;
                var peer = window.OtherPane;
                window.Dispatch("folder-pairs");
                var manager = await FindDialogAsync(window, "Paired Folders");
                var checks = manager.GetVisualDescendants().OfType<CheckBox>().ToArray();
                foreach (var check in checks.Where(check => check.Content is not
                    ("Regular expression" or "Read into existing dual display" or "Switch to dual display automatically")))
                    check.IsChecked = true;
                Click(manager, "Add pair");
                Click(manager, "Apply");
                await window.LastCommand;
                var stored = new SettingsStore(state).Load().FolderPairs.Single();
                Assert(stored.Resolve(leftChild) == rightChild && stored.Resolve(rightChild) == leftChild &&
                    stored.DefaultDual && stored.DefaultNavigationLock && stored.DefaultSynchronize && stored.LockOnDual && stored.PrimaryOnLeft,
                    "The paired-folder choices did not persist or resolve in both directions.");
                window.Dispatch("dual"); await window.LastCommand;
                await peer.NavigateAsync(Path.GetDirectoryName(right)!);
                window.Dispatch("dual"); await window.LastCommand;
                Assert(window.DualPaneVisible && peer.CurrentDirectory == right, "Dual display ignored the saved pair.");
                await source.NavigateAsync(leftChild);
                await peer.LoadingTask;
                Assert(peer.CurrentDirectory == rightChild, "The default dual option did not enable Navigation Lock.");
                window.Dispatch("dual"); await window.LastCommand;
                await peer.NavigateAsync(right);
                Click(window, "Navigation Lock"); await window.LastCommand;
                Assert(window.DualPaneVisible && peer.CurrentDirectory == rightChild,
                    "Navigation Lock did not open the paired descendant from single display.");
                window.Dispatch("navigation-lock"); await window.LastCommand;
                await peer.NavigateAsync(right);
                window.Dispatch("compare");
                var compare = await FindDialogAsync(window, "Synchronize / Compare Folders");
                Assert(compare.GetVisualDescendants().OfType<TextBox>().Any(box => box.Text == rightChild),
                    "Synchronize did not default to the paired descendant.");
                Click(compare, "Close");
                await window.LastCommand;
                window.Dispatch("folder-pairs");
                manager = await FindDialogAsync(window, "Paired Folders");
                manager.GetVisualDescendants().OfType<ListBox>().Single(control => control.Name == "PairedFolders").SelectedIndex = 0;
                Click(manager, "Remove pair");
                Click(manager, "Cancel");
                await window.LastCommand;
                Assert(new SettingsStore(state).Load().FolderPairs.Count == 1, "Cancel applied the staged removal.");

                await source.NavigateAsync(Path.Combine(left, "Documents"));
                await peer.NavigateAsync(rightChild);
                async Task ChangeMissingTargetAsync(MissingFolderPairTarget policy)
                {
                    window.Dispatch("folder-pairs");
                    var dialog = await FindDialogAsync(window, "Paired Folders");
                    dialog.GetVisualDescendants().OfType<ListBox>().Single(control => control.Name == "PairedFolders").SelectedIndex = 0;
                    dialog.GetVisualDescendants().OfType<ComboBox>()
                        .Single(control => control.Name == "MissingPairedFolderTarget").SelectedIndex = (int)policy;
                    Click(dialog, "Update pair");
                    Click(dialog, "Apply");
                    await window.LastCommand;
                    Assert(new SettingsStore(state).Load().FolderPairs.Single().MissingTarget == policy,
                        "The missing-target policy did not persist.");
                }
                async Task VerifySyncTargetAsync(string target)
                {
                    window.Dispatch("compare");
                    var dialog = await FindDialogAsync(window, "Synchronize / Compare Folders");
                    Assert(dialog.GetVisualDescendants().OfType<TextBox>().Any(box => box.Text == target),
                        $"The missing-target policy did not produce {target}.");
                    Click(dialog, "Close");
                    await window.LastCommand;
                }
                await VerifySyncTargetAsync(Path.Combine(right, "Documents"));
                await ChangeMissingTargetAsync(MissingFolderPairTarget.IgnorePair);
                await VerifySyncTargetAsync(rightChild);
                await ChangeMissingTargetAsync(MissingFolderPairTarget.ExistingParent);
                await VerifySyncTargetAsync(right);
                window.Dispatch("dual"); await window.LastCommand;
                window.Dispatch("dual"); await window.LastCommand;
                Assert(window.DualPaneVisible && peer.CurrentDirectory == right,
                    "The existing-parent policy did not open the nearest existing destination.");
                window.Dispatch("settings");
                var preferences = await FindDialogAsync(window, "Preferences");
                preferences.GetVisualDescendants().OfType<CheckBox>()
                    .Single(control => Equals(control.Content, "Cancel without warning when navigating away")).IsChecked = true;
                Click(preferences, "Apply");
                await window.LastCommand;
                await source.NavigateAsync(leftChild);
                await WaitAsync(() => peer.CurrentDirectory == rightChild &&
                    window.StatusMessage.Contains("folders in sync", StringComparison.Ordinal));
                var newLeftChild = Directory.CreateDirectory(Path.Combine(leftChild, "Nested")).FullName;
                var newRightChild = Directory.CreateDirectory(Path.Combine(rightChild, "Nested")).FullName;
                await source.NavigateAsync(newLeftChild);
                await WaitAsync(() => peer.CurrentDirectory == newRightChild);
                Assert(FindButton(window, "Navigation Lock")?.Classes.Contains("navigation-locked") == true,
                    "Moving outside the current bases into a saved pair did not retain and rebase Navigation Lock.");
                window.Dispatch("dual"); await window.LastCommand;
                await source.NavigateAsync(rightChild);
                window.Dispatch("dual"); await window.LastCommand;
                Assert(window.ActivePane == source && window.ActivePaneIndex == 1 &&
                    source.CurrentDirectory == rightChild && peer.CurrentDirectory == leftChild,
                    "Opening from the secondary folder did not preserve focus and place the primary folder first.");
                await window.SaveSettingsAsync();
                var reversed = new SettingsStore(state).Load();
                Assert(reversed.ActivePane == 1 && reversed.Panes[0].Tabs[0].Path == leftChild &&
                    reversed.Panes[1].Tabs[0].Path == rightChild,
                    "The pane placement was not saved in display order.");
            });
            await CheckAsync("Paired folders can open dual display during navigation", async () =>
            {
                var source = window.ActivePane;
                var peer = window.OtherPane;
                var rightDesign = Directory.CreateDirectory(Path.Combine(right, "Design")).FullName;
                var rightDocuments = Directory.CreateDirectory(Path.Combine(right, "Documents")).FullName;
                window.Dispatch("folder-pairs");
                var manager = await FindDialogAsync(window, "Paired Folders");
                var checks = manager.GetVisualDescendants().OfType<CheckBox>().ToArray();
                foreach (var label in new[] { "Default dual-display folder", "Read into existing dual display",
                    "Switch to dual display automatically", "Automatically link paired tabs" })
                    checks.Single(control => Equals(control.Content, label)).IsChecked = true;
                Click(manager, "Add pair");
                Click(manager, "Apply");
                await window.LastCommand;
                Assert(new SettingsStore(state).Load().FolderPairs.Single() is
                    { ReadIntoExistingDual: true, SwitchToDualAutomatically: true, LinkPairedTabs: true },
                    "The automatic paired navigation options did not persist.");
                window.Dispatch("dual"); await window.LastCommand;
                Assert(!window.DualPaneVisible, "The test did not start in single display.");
                await source.NavigateAsync(Path.Combine(left, "Design"));
                await WaitAsync(() => window.DualPaneVisible && peer.CurrentDirectory == rightDesign);
                await source.NavigateAsync(Path.Combine(left, "Documents"));
                await WaitAsync(() => peer.CurrentDirectory == rightDocuments && window.StatusMessage == "Opened the paired folder");
                Assert(window.ActivePane == source && source.CurrentDirectory == Path.Combine(left, "Documents"),
                    "Automatic paired navigation changed the active display or bounced back.");
                Assert(source.ActiveTabLink is { } group && peer.ActiveTabLink == group,
                    "The opened counterparts did not receive the same link.");
                var unrelated = Path.GetDirectoryName(left)!;
                await source.AddTabAsync(unrelated);
                await peer.AddTabAsync(unrelated);
                Assert(source.ActiveTabLink is null && peer.ActiveTabLink is null,
                    "An unrelated tab inherited the previous tab's link.");
                source.NextTab(-1);
                await source.LoadingTask;
                await peer.LoadingTask;
                Assert(source.ActiveTabLink is { } restored && peer.ActiveTabLink == restored &&
                    peer.CurrentDirectory == rightDocuments, "Selecting a linked tab did not activate its counterpart.");
                await window.SaveSettingsAsync();
                var saved = new SettingsStore(state).Load();
                Assert(saved.Panes[0].Tabs[0].LinkedTabGroup is { } savedGroup &&
                    saved.Panes[1].Tabs[0].LinkedTabGroup == savedGroup, "The link did not survive settings reload.");
                source.CloseCurrentTab();
                await source.LoadingTask;
                Assert(peer.ActiveTabLink is null, "Closing a linked tab did not clear its counterpart's link.");
            });
            await CheckAsync("Folder tabs link on Control-click and bypass links on Shift-click", async () =>
            {
                var source = window.ActivePane;
                var peer = window.OtherPane;
                var sourceAlternate = Path.Combine(left, "Design");
                var peerAlternate = Path.Combine(right, "Archive");
                await source.AddTabAsync(sourceAlternate);
                await peer.AddTabAsync(peerAlternate);
                void ClickTab(PaneView pane, string path, RawInputModifiers modifiers)
                {
                    window.UpdateLayout();
                    var tab = pane.GetVisualDescendants().OfType<Button>()
                        .Single(button => button.Name == "FolderTab" && Equals(button.Tag, path));
                    var point = tab.TranslatePoint(new Point(tab.Bounds.Width / 2, tab.Bounds.Height / 2), window)
                        ?? throw new InvalidOperationException("The tab is outside the arranged window.");
                    window.MouseDown(point, MouseButton.Left, modifiers);
                    window.MouseUp(point, MouseButton.Left, modifiers);
                }
                ClickTab(source, left, RawInputModifiers.Control);
                await source.LoadingTask;
                Assert(source.ActiveTabLink is { } group && peer.ActiveTabLink == group,
                    "Control-click did not link the selected tab to the active opposite tab.");
                ClickTab(source, sourceAlternate, RawInputModifiers.None);
                await source.LoadingTask;
                ClickTab(peer, right, RawInputModifiers.None);
                await peer.LoadingTask;
                ClickTab(source, left, RawInputModifiers.Shift);
                await source.LoadingTask;
                Assert(source.CurrentDirectory == left && peer.CurrentDirectory == right,
                    "Shift-click activated the counterpart instead of bypassing the link.");
                ClickTab(source, sourceAlternate, RawInputModifiers.None);
                await source.LoadingTask;
                ClickTab(source, left, RawInputModifiers.None);
                await source.LoadingTask;
                await peer.LoadingTask;
                Assert(peer.CurrentDirectory == peerAlternate, "A normal click did not activate the linked counterpart.");

                ClickTab(source, left, RawInputModifiers.Control | RawInputModifiers.Shift);
                var leftChild = Directory.CreateDirectory(Path.Combine(left, "Linked child")).FullName;
                var rightChild = Directory.CreateDirectory(Path.Combine(peerAlternate, "Linked child")).FullName;
                await source.NavigateAsync(leftChild);
                await peer.LoadingTask;
                Assert(peer.CurrentDirectory == rightChild, "Linked Navigation Lock did not follow a child.");
                await peer.ParentAsync();
                await source.LoadingTask;
                Assert(source.CurrentDirectory == left, "Linked Navigation Lock did not follow a reverse parent.");
                ClickTab(source, sourceAlternate, RawInputModifiers.None);
                await source.LoadingTask;
                await source.NavigateAsync(Path.Combine(left, "Documents"));
                Assert(peer.CurrentDirectory == peerAlternate, "Linked Navigation Lock affected an unrelated tab.");
                ClickTab(source, left, RawInputModifiers.None);
                await source.LoadingTask;
                var lockAction = source.GetVisualDescendants().OfType<Button>()
                    .Single(button => button.Name == "FolderTab" && Equals(button.Tag, left))
                    .ContextMenu!.Items.OfType<MenuItem>().Single(item => Equals(item.Header, "Navigation Lock"));
                lockAction.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                await source.NavigateAsync(leftChild);
                Assert(peer.CurrentDirectory == peerAlternate, "The linked-tab menu did not turn off Navigation Lock.");
                await source.ParentAsync();
                ClickTab(source, left, RawInputModifiers.Control);
                Assert(source.ActiveTabLink is null && peer.ActiveTabLink is null,
                    "Control-clicking the linked pair did not unlink both tabs.");

                MenuItem TabLinkAction(string label)
                {
                    var tab = source.GetVisualDescendants().OfType<Button>()
                        .Single(button => button.Name == "FolderTab" && Equals(button.Tag, left));
                    return tab.ContextMenu!.Items.OfType<MenuItem>().Single(item => Equals(item.Header, label));
                }
                TabLinkAction("Link to Active Tab in Other Pane").RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                Assert(source.ActiveTabLink is { } linked && peer.ActiveTabLink == linked,
                    "The folder-tab context command did not link the tabs.");
                TabLinkAction("Unlink Tab").RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                Assert(source.ActiveTabLink is null && peer.ActiveTabLink is null,
                    "The folder-tab context command did not unlink both endpoints.");
            });
            await CheckAsync("Regex folder pairs expand captures and respect rule order", async () =>
            {
                var design = Path.Combine(left, "Design");
                await window.ActivePane.NavigateAsync(design);
                var preferred = Directory.CreateDirectory(Path.Combine(right, "Preferred", "Design")).FullName;
                var alternate = Directory.CreateDirectory(Path.Combine(right, "Alternate", "Design")).FullName;
                window.Dispatch("folder-pairs");
                var manager = await FindDialogAsync(window, "Paired Folders");
                var controls = manager.GetVisualDescendants().ToArray();
                controls.OfType<CheckBox>().Single(control => Equals(control.Content, "Regular expression")).IsChecked = true;
                controls.OfType<CheckBox>().Single(control => Equals(control.Content, "Default Synchronize target")).IsChecked = true;
                var match = controls.OfType<TextBox>().Single(control => control.Name == "PairSource");
                var replace = controls.OfType<TextBox>().Single(control => control.Name == "PairDestination");
                match.Text = System.Text.RegularExpressions.Regex.Escape(left + Path.DirectorySeparatorChar) + "(.*)";
                string Replacement(string target) =>
                    (Path.GetDirectoryName(target)! + Path.DirectorySeparatorChar).Replace(@"\", @"\\", StringComparison.Ordinal) + @"\1";
                replace.Text = Replacement(alternate);
                var preview = controls.OfType<TextBox>().Single(control => control.Name == "PairTestResult");
                Click(manager, "Test pair");
                await WaitAsync(() => preview.Text == alternate);
                Assert(new SettingsStore(state).Load().FolderPairs.Count == 0,
                    "Testing an unsaved pair modified the stored rules.");
                Click(manager, "Add pair");
                replace.Text = Replacement(preferred);
                Click(manager, "Add pair");
                Click(manager, "Move up");
                Click(manager, "Apply");
                await window.LastCommand;
                var rules = new SettingsStore(state).Load().FolderPairs;
                Assert(rules.Count == 2 && rules[0].RegularExpression && rules[0].Resolve(design) == preferred &&
                    rules[1].Resolve(design) == alternate && rules[0].Resolve(preferred) is null,
                    "The saved regex rules did not expand captures in the configured direction and order.");
                window.Dispatch("compare");
                var compare = await FindDialogAsync(window, "Synchronize / Compare Folders");
                Assert(compare.GetVisualDescendants().OfType<TextBox>().Any(box => box.Text == preferred),
                    "Synchronize did not use the first applicable regex pair.");
                Click(compare, "Close");
                await window.LastCommand;
            });
            await CheckAsync("Navigation Lock mirrors matching folders and resets for a new tab", async () =>
            {
                var source = window.ActivePane;
                var destination = window.OtherPane;
                var leftChild = Path.Combine(left, "Design");
                var rightChild = Directory.CreateDirectory(Path.Combine(right, "Design")).FullName;
                Click(window, "Navigation Lock"); await window.LastCommand;
                var warning = window.GetVisualDescendants().OfType<Border>()
                    .Single(control => control.Name == "NavigationLockWarning");
                await source.NavigateAsync(leftChild);
                await destination.LoadingTask;
                Assert(destination.CurrentDirectory == rightChild, "Navigation Lock did not mirror a child.");
                await destination.ParentAsync();
                await source.LoadingTask;
                Assert(source.CurrentDirectory == left, "Navigation Lock did not mirror a parent in reverse.");
                await source.NavigateAsync(Path.Combine(left, "Documents"));
                await destination.LoadingTask;
                await WaitAsync(() => window.StatusMessage.Contains("out of sync", StringComparison.Ordinal));
                Assert(destination.CurrentDirectory == right && warning.IsVisible,
                    "A missing counterpart changed the destination or failed to show the warning.");
                Click(window, "Dismiss Navigation Lock warning");
                Assert(!warning.IsVisible, "Dismiss did not close the Navigation Lock warning.");
                await destination.RefreshAsync();
                Assert(source.CurrentDirectory == Path.Combine(left, "Documents"), "A background refresh changed the source.");
                await source.NavigateAsync(left);
                await WaitAsync(() => window.StatusMessage.Contains("folders in sync", StringComparison.Ordinal));
                await source.AddTabAsync(leftChild);
                Assert(destination.CurrentDirectory == right, "Switching tabs navigated the other display.");
                var leftNested = Directory.CreateDirectory(Path.Combine(leftChild, "Nested")).FullName;
                var rightNested = Directory.CreateDirectory(Path.Combine(right, "Nested")).FullName;
                await source.NavigateAsync(leftNested);
                await destination.LoadingTask;
                Assert(destination.CurrentDirectory == rightNested, "The new tab did not reset the pairing bases.");
                var unmatched = Directory.CreateDirectory(Path.Combine(leftNested, "Unmatched")).FullName;
                await source.NavigateAsync(unmatched);
                await destination.LoadingTask;
                await WaitAsync(() => warning.IsVisible);
                Click(window, "Reset sync position"); await window.LastCommand;
                Assert(!warning.IsVisible && destination.CurrentDirectory == rightNested,
                    "Reset moved a display or left the warning visible.");
                await source.ParentAsync();
                await WaitAsync(() => warning.IsVisible);
                Click(window, "Turn off Navigation Lock"); await window.LastCommand;
                Assert(!warning.IsVisible && destination.CurrentDirectory == rightNested &&
                    FindButton(window, "Navigation Lock")?.Classes.Contains("navigation-locked") == false,
                    "Turning off Navigation Lock did not clear the warning and toolbar state.");

                window.Dispatch("settings");
                var preferences = await FindDialogAsync(window, "Preferences");
                var checks = preferences.GetVisualDescendants().OfType<CheckBox>().ToArray();
                checks.Single(control => Equals(control.Content, "Warning message when out of sync")).IsChecked = false;
                checks.Single(control => Equals(control.Content, "Cancel without warning when navigating away")).IsChecked = true;
                Click(preferences, "Apply");
                await window.LastCommand;
                var settings = new SettingsStore(state).Load();
                Assert(!settings.WarnWhenNavigationLockOutOfSync && settings.CancelNavigationLockWhenNavigatingAway,
                    "Navigation Lock preferences did not persist.");
                Click(window, "Navigation Lock"); await window.LastCommand;
                await source.NavigateAsync(unmatched);
                await destination.LoadingTask;
                Assert(!warning.IsVisible && FindButton(window, "Navigation Lock")?.Classes.Contains("navigation-locked") == true,
                    "An unmatched descendant did not respect the disabled warning or keep the lock.");
                await source.NavigateAsync(left);
                Assert(!warning.IsVisible && FindButton(window, "Navigation Lock")?.Classes.Contains("navigation-locked") == false &&
                    destination.CurrentDirectory == rightNested,
                    "Navigating outside the base did not cancel the lock quietly.");
            });
            await CheckAsync("F6 toggles dual display and F7 toggles viewer", async () =>
            {
                window.ActivePane.FocusList(); Press(window, PhysicalKey.F6); await window.LastCommand;
                Assert(!window.DualPaneVisible, "F6 did not disable dual display.");
                Press(window, PhysicalKey.F6); await window.LastCommand; Assert(window.DualPaneVisible, "F6 did not restore dual display.");
                Press(window, PhysicalKey.F7); await window.LastCommand; Assert(!window.ViewerVisible, "F7 did not hide the viewer.");
                Press(window, PhysicalKey.F7); await window.LastCommand; Assert(window.ViewerVisible, "F7 did not restore the viewer.");
            });
            await CheckAsync("Reference view toolbar controls the existing lister layout", async () =>
            {
                var toolbar = window.GetVisualDescendants().OfType<StackPanel>()
                    .Single(panel => panel.Name == "ListerViewToolbar");
                var labels = new[] { "Toggle folder tree", "Toggle viewer pane", "Toggle dual display",
                    "Stack panes vertically", "Place panes side by side", "Navigation Lock" };
                Assert(toolbar.Children.OfType<Button>().Select(AutomationProperties.GetName)
                    .SequenceEqual(labels), "View toolbar lacks distinct accessible native-style controls.");
                var treeWasVisible = window.TreeVisible;
                Click(window, labels[0]); await window.LastCommand;
                Assert(window.TreeVisible != treeWasVisible, "Tree toolbar control did not change the actual pane.");
                Click(window, labels[0]); await window.LastCommand;
                Assert(window.TreeVisible == treeWasVisible, "Tree toolbar control could not restore the pane.");
                var viewerWasVisible = window.ViewerVisible;
                Click(window, labels[1]); await window.LastCommand;
                Assert(window.ViewerVisible != viewerWasVisible, "Viewer toolbar control did not change the actual pane.");
                Click(window, labels[1]); await window.LastCommand;
                Assert(window.ViewerVisible == viewerWasVisible, "Viewer toolbar control could not restore the pane.");
                Click(window, labels[2]); await window.LastCommand;
                Assert(!window.DualPaneVisible, "Dual toolbar control did not hide the second pane.");
                Click(window, labels[3]); await window.LastCommand;
                Assert(window.DualPaneVisible, "Stack toolbar control did not restore the second pane.");
                Click(window, labels[4]); await window.LastCommand;
                Assert(window.DualPaneVisible, "Side-by-side control did not retain both panes.");
                Click(window, labels[3]); await window.LastCommand;
                if (output is not null) SaveFrame(window, Path.Combine(output, "opusmac-view-toolbar-1080x847.png"));
                var sourceFolder = Directory.CreateDirectory(Path.Combine(left, "Source Tree Target")).FullName;
                var destinationFolder = Directory.CreateDirectory(Path.Combine(right, "Destination Tree Target")).FullName;
                foreach (var (flags, orientation) in new[] { ("0x60402b", "Horizontal"), ("0x60400b", "Vertical") })
                {
                    using var dualStyle = new MemoryStream(Encoding.UTF8.GetBytes(
                        $"""<style hidden="no"><layout flags="{flags}"/></style>"""));
                    window.ApplyListerStyle(await ListerStyleImport.ReadAsync(dualStyle));
                    window.UpdateLayout();
                    var trees = window.GetVisualDescendants().OfType<TreeView>().Where(tree => tree.IsVisible).ToArray();
                    Assert(window.DualPaneVisible && window.TreeVisible && window.SplitOrientation == orientation &&
                        trees.Length == 2, $"Imported {orientation} style did not show two folder trees.");
                    var firstPoint = trees[0].TranslatePoint(new Point(), window);
                    var secondPoint = trees[1].TranslatePoint(new Point(), window);
                    Assert(firstPoint.HasValue && secondPoint.HasValue &&
                        (orientation == "Horizontal" ? secondPoint.Value.Y > firstPoint.Value.Y : secondPoint.Value.X > firstPoint.Value.X),
                        $"Imported {orientation} style placed both trees in the same display.");
                    trees[0].ItemsSource = new[] { new TreeViewItem { Header = "Source", Tag = sourceFolder } };
                    trees[1].ItemsSource = new[] { new TreeViewItem { Header = "Destination", Tag = destinationFolder } };
                    trees[1].SelectedItem = trees[1].Items[0];
                    await WaitAsync(() => window.ActivePane.CurrentDirectory == destinationFolder);
                    trees[0].SelectedItem = trees[0].Items[0];
                    await WaitAsync(() => window.ActivePane.CurrentDirectory == sourceFolder);
                    window.Dispatch("switch-pane"); await window.LastCommand;
                    Assert(window.ActivePane.CurrentDirectory == destinationFolder, "The source tree changed the destination pane.");
                    await window.ActivePane.NavigateAsync(right);
                    window.Dispatch("switch-pane"); await window.LastCommand;
                    Assert(window.ActivePane.CurrentDirectory == sourceFolder, "The destination tree changed the source pane.");
                    await window.ActivePane.NavigateAsync(left);
                }
                bool DestinationTreeVisible() => window.GetVisualDescendants().OfType<TreeView>()
                    .Any(tree => tree.Name == "DestinationFolderTree" && tree.IsEffectivelyVisible);
                async Task ToggleDestinationTreeAsync()
                {
                    window.KeyPressQwerty(PhysicalKey.F8, RawInputModifiers.Shift);
                    window.KeyReleaseQwerty(PhysicalKey.F8, RawInputModifiers.Shift);
                    await window.LastCommand;
                }
                await ToggleDestinationTreeAsync();
                Assert(window.TreeVisible && !DestinationTreeVisible(), "Shift+F8 did not hide only the destination tree.");
                Press(window, PhysicalKey.F8); await window.LastCommand;
                Assert(!window.TreeVisible && !DestinationTreeVisible(), "F8 did not hide the remaining source tree.");
                await ToggleDestinationTreeAsync();
                Assert(!window.TreeVisible && DestinationTreeVisible(), "Shift+F8 did not show a destination-only tree.");
                Press(window, PhysicalKey.F8); await window.LastCommand;
                Assert(window.TreeVisible && DestinationTreeVisible(), "F8 did not restore the source tree.");
                DragSplitter(window, "SourceTreeSplitter", new Vector(35, 0));
                DragSplitter(window, "DestinationTreeSplitter", new Vector(-25, 0));
                await window.SaveSettingsAsync();
                var dualWidths = new SettingsStore(state).Load();
                Assert(dualWidths.IndependentFolderTrees && dualWidths.TreeWidth is > 210 and < 220 &&
                    dualWidths.DestinationTreeWidth is > 150 and < 160,
                    "Independent tree splitter widths did not persist.");
                window.Dispatch("style-dual-horizontal"); await window.LastCommand;
                window.UpdateLayout();
                double TreeWidth(string name) => window.GetVisualDescendants().OfType<TreeView>()
                    .Single(tree => tree.Name == name).Bounds.Width;
                Assert(TreeWidth("FolderTree") > TreeWidth("DestinationFolderTree") + 50,
                    "Changing orientation did not restore both independent tree widths.");
                window.Dispatch("style-commander"); await window.LastCommand;
                Assert(window.DualPaneVisible && !window.TreeVisible && !window.ViewerVisible &&
                    window.SplitOrientation == "Vertical", "Commander style did not show two side-by-side displays.");
                window.Dispatch("switch-pane"); await window.LastCommand;
                Assert(window.ActivePane.CurrentDirectory == right, "The style fixture did not activate the destination pane.");
                window.Dispatch("style-explorer"); await window.LastCommand;
                Assert(!window.DualPaneVisible && window.TreeVisible && !window.ViewerVisible &&
                    window.ActivePane.CurrentDirectory == right && window.ActivePane.IsVisible,
                    "Explorer style did not show one display and the tree.");
                window.Dispatch("style-images"); await window.LastCommand;
                Assert(!window.DualPaneVisible && window.TreeVisible && window.ViewerVisible &&
                    window.ActivePane.CurrentViewMode == "Tiles", "Images style did not show tiles and the Viewer.");
                using (var thumbnail = new SKBitmap(48, 32))
                {
                    thumbnail.Erase(SKColors.CornflowerBlue);
                    using var encoded = thumbnail.Encode(SKEncodedImageFormat.Png, 100);
                    var bytes = encoded.ToArray();
                    for (var index = 0; index < 16; index++)
                        await File.WriteAllBytesAsync(Path.Combine(right, $"Filmstrip {index:D2}.png"), bytes);
                }
                await window.ActivePane.RefreshAsync();
                var selected = Path.Combine(right, "Readme.txt");
                window.ActivePane.SelectPaths([selected]);
                using var styleStream = new MemoryStream(Encoding.UTF8.GetBytes(
                    """<style hidden="no"><layout flags="0x4a00045" view_mode="thumbnails,thumbnails"/></style>"""));
                window.ApplyListerStyle(await ListerStyleImport.ReadAsync(styleStream));
                using var unsupportedStyle = new MemoryStream(Encoding.UTF8.GetBytes(
                    """<style hidden="no"><layout flags="0x200600007"/></style>"""));
                var refused = false;
                try { await ListerStyleImport.ReadAsync(unsupportedStyle); }
                catch (InvalidDataException) { refused = true; }
                Assert(refused, "Import silently accepted the unsupported Metadata layout.");
                window.UpdateLayout();
                var strip = window.ActivePane.GetVisualDescendants().OfType<ListBox>()
                    .Single(control => control.Name == "FileTiles");
                Assert(window.ActivePane.CurrentViewMode == "Filmstrip" && window.ViewerVisible && !window.TreeVisible &&
                    strip.GetVisualDescendants().OfType<VirtualizingStackPanel>().Any(panel => panel.Orientation == Orientation.Horizontal),
                    "Filmstrip did not show one horizontally virtualized thumbnail row.");
                Assert(window.ActivePane.TranslatePoint(new Point(), window)?.Y > window.Viewer.TranslatePoint(new Point(), window)?.Y &&
                    window.ActivePane.SelectedEntries.Single().FullPath == selected,
                    "Filmstrip did not place the file row below the Viewer or retain selection.");
                var scroll = strip.GetVisualDescendants().OfType<ScrollViewer>().First();
                Assert(scroll.Extent.Width > scroll.Viewport.Width, "The image strip did not overflow horizontally.");
                scroll.Offset = new Vector(0, 0);
                var lastImage = window.ActivePane.VisibleEntries.Last(entry => entry.Name.EndsWith(".png", StringComparison.Ordinal));
                strip.ScrollIntoView(lastImage);
                await WaitAsync(() => scroll.Offset.X > 0);
                strip.SelectedItem = lastImage;
                await WaitAsync(() => window.Viewer.DisplayedName == lastImage.Name &&
                    window.Viewer.PreviewStatus.Contains("48 × 32 preview", StringComparison.Ordinal) &&
                    window.Viewer.GetVisualDescendants().OfType<Image>().Any(image => image.Source is not null));
                Assert(window.ActivePane.SelectedEntries.Single().FullPath == lastImage.FullPath,
                    "The scrolled filmstrip selection did not bind to the exact previewed image.");
                await window.SaveSettingsAsync();
                var filmstripSettings = new SettingsStore(state).Load();
                Assert(filmstripSettings.Panes[filmstripSettings.ActivePane].Tabs[0].ViewMode == "Filmstrip",
                    "Filmstrip did not survive settings reload.");
                if (output is not null)
                {
                    await WaitAsync(() => window.Viewer.PreviewStatus.Contains("48 × 32 preview", StringComparison.Ordinal));
                    SaveFrame(window, Path.Combine(output, "opusmac-filmstrip.png"));
                }
                window.Dispatch("style-single"); await window.LastCommand;
                Assert(!window.DualPaneVisible && !window.TreeVisible && !window.ViewerVisible &&
                    window.ActivePane.CurrentDirectory == right, "Single style did not preserve the active destination folder.");
                await window.SaveSettingsAsync();
                var single = new SettingsStore(state).Load();
                Assert(!single.DualPane && single.ActivePane == 1 && single.Panes[1].Tabs[0].Path == right,
                    "The single-display destination identity did not survive settings reload.");
                await window.CloseForDiagnosticsAsync();
                window = new MainWindow(new LaunchOptions { StateDirectory = state });
                window.Show(); await window.InitializeAsync();
                Assert(!window.DualPaneVisible && window.ActivePane.CurrentDirectory == right,
                    "Relaunching did not restore the active single-display folder.");
                window.Dispatch("style-commander"); await window.LastCommand;
                Assert(window.ActivePane.CurrentDirectory == right && window.OtherPane.CurrentDirectory == left,
                    "Returning to dual display changed one of the saved folder identities.");
                window.Dispatch("style-dual-vertical"); await window.LastCommand;
                window.UpdateLayout();
                Assert(TreeWidth("FolderTree") > TreeWidth("DestinationFolderTree") + 50,
                    "Relaunching did not restore both tree widths when the dual style was applied.");
            });
            await CheckAsync("Folder tree header collapses branches and hides the existing tree", async () =>
            {
                var tree = window.GetVisualDescendants().OfType<TreeView>().Single(view => view.Name == "FolderTree");
                var roots = tree.ItemsSource!.Cast<TreeViewItem>().ToArray();
                Assert(roots.Any(root => root.IsExpanded), "No navigation branches are expanded.");
                Click(window, "Collapse folder tree branches");
                Assert(roots.All(root => !root.IsExpanded), "The tree still has expanded roots.");
                roots[0].IsExpanded = true;
                if (output is not null) SaveFrame(window, Path.Combine(output, "opusmac-folder-tree-header-1080x847.png"));
                Click(window, "Hide folder tree"); await window.LastCommand;
                Assert(!window.TreeVisible, "The tree header did not hide the tree.");
                Click(window, "Toggle folder tree"); await window.LastCommand;
                Assert(window.TreeVisible && roots[0].IsExpanded, "Tree restoration lost its navigation state.");
            });
            await CheckAsync("Folder tree find opens exact loaded folder with keyboard and preserves focus", async () =>
            {
                var tree = window.GetVisualDescendants().OfType<TreeView>().Single(view => view.Name == "FolderTree");
                var roots = tree.ItemsSource!.Cast<TreeViewItem>().ToArray();
                var fixture = new TreeViewItem { Header = "Destination proof", Tag = right };
                roots[0].Items.Add(fixture);
                var before = window.ActivePane.CurrentDirectory;
                Click(window, "Find in folder tree");
                var field = window.GetVisualDescendants().OfType<TextBox>()
                    .Single(box => box.Name == "FolderTreeFind");
                Assert(field.IsVisible && field.IsFocused, "The tree find control did not open and focus.");
                field.Text = "Destination proof";
                var navigationBeforeFind = window.ActivePane.ViewRevision.Navigation;
                if (output is not null)
                {
                    await Task.Delay(60); // Let the headless layout render the opened inline editor.
                    SaveFrame(window, Path.Combine(output, "opusmac-folder-tree-find-1080x847.png"));
                }
                Press(window, PhysicalKey.Enter);
                await WaitAsync(() => window.ActivePane.CurrentDirectory == right);
                Assert(window.ActivePane.ViewRevision.Navigation == navigationBeforeFind + 1,
                    "Folder Tree Find dispatched more than one navigation for the selected folder.");
                Assert(fixture.IsSelected, "The exact matched tree item was not selected.");
                Assert(before != window.ActivePane.CurrentDirectory, "Tree lookup never navigated the incumbent pane.");
                field.Text = "missing-finite-tree-fixture";
                Press(window, PhysicalKey.Enter);
                Assert(window.ActivePane.CurrentDirectory == right,
                    "An unmatched query navigated or changed the current location.");
                // A previously loaded network/removable folder can disappear after
                // it entered the tree. Find must locate the row without probing
                // every candidate synchronously; the existing pane reports the
                // unavailable destination without committing that navigation.
                var unavailablePath = Path.Combine(left, "offline-tree-" + Guid.NewGuid().ToString("N"));
                var unavailable = new TreeViewItem { Header = "Offline tree proof", Tag = unavailablePath };
                roots[0].Items.Insert(0, unavailable);
                roots[0].IsExpanded = true;
                Dispatcher.UIThread.RunJobs();
                field.Text = "Offline tree proof";
                var navigationBeforeOffline = window.ActivePane.ViewRevision.Navigation;
                Press(window, PhysicalKey.Enter);
                Assert(unavailable.IsSelected, "The loaded but offline folder was hidden from tree find.");
                await WaitAsync(() => window.StatusMessage.Contains("Could not read folder", StringComparison.Ordinal));
                await window.ActivePane.LoadingTask;
                Assert(window.ActivePane.ViewRevision.Navigation == navigationBeforeOffline + 1,
                    "The offline Folder Tree entry caused duplicate navigation attempts.");
                Assert(window.ActivePane.CurrentDirectory == right,
                    "An unavailable folder committed a new current directory.");
                Assert(window.StatusMessage.Contains("Could not read folder", StringComparison.Ordinal),
                    "The failed navigation did not surface the existing recoverable error. Actual: " + window.StatusMessage);
                Press(window, PhysicalKey.Escape);
                Assert(!field.IsVisible, "Escape did not close the inline folder find.");
                await window.ActivePane.NavigateAsync(left);
            });
            await CheckAsync("F4 focuses editable location", async () =>
            {
                Press(window, PhysicalKey.F4); await Task.Yield();
                Assert(window.ActivePane.PathBox.IsFocused, "F4 did not focus the path."); window.ActivePane.FocusList();
            });
            await CheckAsync("Folder aliases resolve location input and saved folder pairs", async () =>
            {
                var aliasSource = Directory.CreateDirectory(Path.Combine(left, "[Staging]")).FullName;
                var aliasDestination = Directory.CreateDirectory(Path.Combine(right, "[Delivery] $")).FullName;
                var leftDesign = Directory.CreateDirectory(Path.Combine(aliasSource, "Design")).FullName;
                var rightDesign = Directory.CreateDirectory(Path.Combine(aliasDestination, "Design")).FullName;
                await File.WriteAllTextAsync(Path.Combine(leftDesign, "Layout.txt"), "Alias fixture");
                window.Dispatch("folder-aliases");
                var manager = await FindDialogAsync(window, "Folder Aliases");
                var inputs = manager.GetVisualDescendants().OfType<TextBox>().ToArray();
                var aliases = manager.GetVisualDescendants().OfType<ListBox>().Single(list => list.Name == "FolderAliases");
                var builtIns = manager.GetVisualDescendants().OfType<CheckBox>()
                    .Single(check => Equals(check.Content, "Show built-in aliases"));
                Assert(!aliases.Items.OfType<FolderAlias>().Any(), "Built-in aliases were visible by default.");
                builtIns.IsChecked = true;
                var dataAlias = aliases.Items.OfType<FolderAlias>().Single(alias => alias.Name == "dopusdata");
                Assert(dataAlias.Path == state, "The built-in data alias does not point at this profile.");
                aliases.SelectedItem = dataAlias;
                Click(manager, "Remove alias");
                Assert(aliases.Items.OfType<FolderAlias>().Any(alias => alias == dataAlias),
                    "The alias editor removed a built-in.");
                builtIns.IsChecked = false;
                inputs.Single(box => box.Name == "FolderAliasName").Text = "WorkRoot";
                inputs.Single(box => box.Name == "FolderAliasPath").Text = aliasSource;
                Click(manager, "Add alias");
                inputs.Single(box => box.Name == "FolderAliasName").Text = "Deliveries";
                inputs.Single(box => box.Name == "FolderAliasPath").Text = aliasDestination;
                Click(manager, "Add alias");
                Click(manager, "Apply");
                await window.LastCommand;
                Assert(new SettingsStore(state).Load().FolderAliases.Count == 2, "The aliases did not persist.");

                var pane = window.ActivePane;
                pane.FocusPath();
                pane.PathBox.Text = "/workroot/Design";
                Press(window, PhysicalKey.Enter);
                await pane.LoadingTask;
                Assert(pane.CurrentDirectory == leftDesign &&
                    pane.VisibleEntries.Any(entry => entry.Name == "Layout.txt"), "The location alias did not enumerate its real target.");

                window.Dispatch("folder-pairs");
                var pairEditor = await FindDialogAsync(window, "Paired Folders");
                pairEditor.GetVisualDescendants().OfType<TextBox>().Single(box => box.Name == "PairSource").Text = "/WorkRoot";
                pairEditor.GetVisualDescendants().OfType<TextBox>().Single(box => box.Name == "PairDestination").Text = "/Deliveries";
                pairEditor.GetVisualDescendants().OfType<CheckBox>()
                    .Single(check => Equals(check.Content, "Default Synchronize target")).IsChecked = true;
                Click(pairEditor, "Add pair");
                Click(pairEditor, "Apply");
                await window.LastCommand;
                Assert(new SettingsStore(state).Load().FolderPairs.Single().Destination == "/Deliveries",
                    "The saved pair lost its alias reference.");
                window.Dispatch("compare");
                var comparison = await FindDialogAsync(window, "Synchronize / Compare Folders");
                Assert(comparison.GetVisualDescendants().OfType<TextBox>().Any(box => box.Text == rightDesign),
                    "Synchronize did not use the expanded paired alias.");
                Click(comparison, "Close");
                await window.LastCommand;

                window.Dispatch("folder-pairs");
                pairEditor = await FindDialogAsync(window, "Paired Folders");
                pairEditor.GetVisualDescendants().OfType<ListBox>().Single(box => box.Name == "PairedFolders").SelectedIndex = 0;
                var pairControls = pairEditor.GetVisualDescendants().ToArray();
                pairControls.OfType<CheckBox>().Single(check => Equals(check.Content, "Regular expression")).IsChecked = true;
                pairControls.OfType<CheckBox>().Single(check => Equals(check.Content, "Expand folder aliases")).IsChecked = true;
                var separator = Path.DirectorySeparatorChar.ToString();
                pairControls.OfType<TextBox>().Single(box => box.Name == "PairSource").Text =
                    "^/WorkRoot" + System.Text.RegularExpressions.Regex.Escape(separator) + "(.*)$";
                pairControls.OfType<TextBox>().Single(box => box.Name == "PairDestination").Text =
                    "/Deliveries" + separator.Replace(@"\", @"\\", StringComparison.Ordinal) + @"\1";
                Click(pairEditor, "Test pair");
                var result = pairControls.OfType<TextBox>().Single(box => box.Name == "PairTestResult");
                await WaitAsync(() => result.Text == rightDesign);
                Click(pairEditor, "Update pair");
                Click(pairEditor, "Apply");
                await window.LastCommand;
                Assert(new SettingsStore(state).Load().FolderPairs.Single() is
                    { RegularExpression: true, ExpandRegexAliases: true },
                    "The regex alias expansion preference did not persist.");
                window.Dispatch("compare");
                comparison = await FindDialogAsync(window, "Synchronize / Compare Folders");
                Assert(comparison.GetVisualDescendants().OfType<TextBox>().Any(box => box.Text == rightDesign),
                    "The saved regex pair did not escape and expand both aliases.");
                Click(comparison, "Close");
                await window.LastCommand;

                var retargeted = Directory.CreateDirectory(Path.Combine(right, "Retargeted", "Design")).FullName;
                window.Dispatch("folder-aliases");
                manager = await FindDialogAsync(window, "Folder Aliases");
                aliases = manager.GetVisualDescendants().OfType<ListBox>().Single(list => list.Name == "FolderAliases");
                var filter = manager.GetVisualDescendants().OfType<TextBox>().Single(box => box.Name == "FolderAliasFilter");
                filter.Text = "staging";
                await WaitAsync(() => aliases.Items.OfType<FolderAlias>().Select(alias => alias.Name).SequenceEqual(new[] { "WorkRoot" }));
                filter.Text = "/deliver";
                await WaitAsync(() => aliases.Items.OfType<FolderAlias>().Select(alias => alias.Name).SequenceEqual(new[] { "Deliveries" }));
                aliases.SelectedItem = aliases.Items.OfType<FolderAlias>().Single();
                manager.GetVisualDescendants().OfType<TextBox>().Single(box => box.Name == "FolderAliasPath").Text =
                    Path.GetDirectoryName(retargeted);
                Click(manager, "Update alias");
                Click(manager, "Apply");
                await window.LastCommand;
                window.Dispatch("compare");
                comparison = await FindDialogAsync(window, "Synchronize / Compare Folders");
                Assert(new SettingsStore(state).Load().FolderAliases.Single(alias => alias.Name == "WorkRoot").Path == aliasSource,
                    "Editing a filtered alias changed an unrelated row.");
                Assert(comparison.GetVisualDescendants().OfType<TextBox>().Any(box => box.Text == retargeted),
                    "Retargeting an alias did not update the saved pair's destination.");
                Click(comparison, "Close");
                await window.LastCommand;
            });
            await CheckAsync("Volume aliases resolve labeled drives and fail closed when absent", async () =>
            {
                if (OperatingSystem.IsWindows())
                {
                    var labeled = new List<(string Label, string Root)>();
                    foreach (var drive in DriveInfo.GetDrives())
                    {
                        try
                        {
                            if (drive.IsReady && !string.IsNullOrWhiteSpace(drive.VolumeLabel))
                                labeled.Add((drive.VolumeLabel, drive.Name));
                        }
                        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
                    }
                    foreach (var group in labeled.GroupBy(item => item.Label, StringComparer.OrdinalIgnoreCase))
                    {
                        var input = "/$" + group.Key + "/Fixture";
                        var expected = group.Count() == 1 ? Path.Combine(group.Single().Root, "Fixture") : input;
                        var settings = new AppSettings();
                        Assert(settings.ExpandFolderAlias(input, state, resolveVolumeLabel: true) == expected,
                            "A mounted volume label resolved to the wrong root or ambiguous match.");
                        var regexMatch = settings.ExpandRegexFolderAlias("^/$" + group.Key + @"\\(.*)$", state, replacement: false);
                        var regexReplace = settings.ExpandRegexFolderAlias("/$" + group.Key + @"\\Delivery\\\1", state, replacement: true);
                        if (group.Count() == 1)
                        {
                            var root = group.Single().Root;
                            Assert(regexMatch is not null && regexReplace is not null &&
                                new FolderPair(regexMatch, regexReplace, false, false, false, true, false, RegularExpression: true)
                                    .Resolve(Path.Combine(root, "Fixture")) == Path.Combine(root, "Delivery", "Fixture"),
                                "The regex volume alias did not preserve the root boundary and captured suffix.");
                        }
                        else Assert(regexMatch is null && regexReplace is null, "A regex picked an ambiguous volume.");
                    }
                    Console.WriteLine($"METRIC mounted_volume_labels_checked={labeled.Count}");
                }
                var pane = window.ActivePane;
                var original = pane.CurrentDirectory;
                pane.FocusPath();
                pane.PathBox.Text = "/$OpusMac missing " + Guid.NewGuid().ToString("N");
                Assert(new AppSettings().ExpandRegexFolderAlias(pane.PathBox.Text, state, replacement: true) is null,
                    "A missing regex volume alias survived as a literal destination.");
                Press(window, PhysicalKey.Enter);
                await WaitAsync(() => window.StatusMessage.Contains("volume label is unavailable or ambiguous", StringComparison.Ordinal));
                Assert(pane.CurrentDirectory == original && pane.PathBox.IsVisible,
                    "A missing volume alias changed the folder or dismissed the editable location.");
            });
            await CheckAsync("Home returns to the initial tab folder or the configured destination", async () =>
            {
                var pane = window.ActivePane;
                var child = Path.Combine(left, "Design");
                Button HomeButton() => pane.GetVisualDescendants().OfType<Button>()
                    .Single(button => button.Name == "HomeFolderButton");
                await pane.NavigateAsync(child);
                HomeButton().RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await window.LastCommand;
                Assert(pane.CurrentDirectory == left, "Home did not return to the first folder opened in the tab.");
                await pane.AddTabAsync(child);
                await pane.NavigateAsync(left);
                pane.FocusList();
                window.KeyPressQwerty(PhysicalKey.Home, RawInputModifiers.Alt);
                window.KeyReleaseQwerty(PhysicalKey.Home, RawInputModifiers.Alt);
                await window.LastCommand;
                Assert(pane.CurrentDirectory == child, "Alt+Home did not use the new tab's own first folder.");

                window.Dispatch("home-settings");
                var settings = await FindDialogAsync(window, "Home Folder");
                settings.GetVisualDescendants().OfType<ComboBox>().Single().SelectedIndex = 1;
                settings.GetVisualDescendants().OfType<TextBox>().Single(box => box.Name == "HomeFolderPath").Text = right;
                Click(settings, "Apply");
                await window.LastCommand;
                Assert(new SettingsStore(state).Load().HomeFolder == right, "The specific Home folder did not persist.");
                HomeButton().RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await window.LastCommand;
                Assert(pane.CurrentDirectory == right, "Home did not open the configured folder.");
                await pane.NavigateAsync(left);
                window.UpdateLayout();
                var home = HomeButton();
                var homeCenter = home.TranslatePoint(new Point(home.Bounds.Width / 2, home.Bounds.Height / 2), window)
                    ?? throw new InvalidOperationException("The Home button is outside the arranged window.");
                window.MouseDown(homeCenter, MouseButton.Middle, RawInputModifiers.None);
                window.MouseUp(homeCenter, MouseButton.Middle, RawInputModifiers.None);
                await window.LastCommand;
                Assert(pane.CurrentDirectory == right && pane.Capture().Tabs.Count == 3,
                    "Middle-click did not open a new Home tab.");
                pane.CloseCurrentTab();
                await pane.LoadingTask;
                await pane.NavigateAsync(left);
                var dropped = Path.Combine(left, "Project notes.txt");
                await window.TransferDroppedPathsAsync(pane, [dropped], move: false, toHome: true);
                Assert(pane.CurrentDirectory == left && File.Exists(dropped) &&
                    (await File.ReadAllBytesAsync(dropped)).SequenceEqual(await File.ReadAllBytesAsync(Path.Combine(right, "Project notes.txt"))),
                    "The Home drop did not copy exact bytes to the configured folder while preserving the source.");
                pane.FocusPath();
                pane.PathBox.Text = "/homedir";
                Press(window, PhysicalKey.Enter);
                await pane.LoadingTask;
                Assert(pane.CurrentDirectory == right, "The homedir alias did not use the specific folder.");
                HomeButton().ContextMenu!.Items.OfType<MenuItem>().Single(item => Equals(item.Header, "Reset Home Folder"))
                    .RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                await window.LastCommand;
                Assert(new SettingsStore(state).Load().HomeFolder is null, "Reset Home Folder did not clear the override.");
                HomeButton().RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await window.LastCommand;
                Assert(pane.CurrentDirectory == child, "Reset Home Folder did not restore the tab's initial folder.");
            });
            await CheckAsync("Reference breadcrumb preserves editable folder navigation and focus", async () =>
            {
                var pane = window.ActivePane;
                await pane.NavigateAsync(Path.Combine(left, "Design"));
                Assert(pane.BreadcrumbButton.IsVisible && !pane.PathBox.IsVisible,
                    "The compact breadcrumb was not visible after navigation.");
                Assert(pane.BreadcrumbText.Contains("Design", StringComparison.Ordinal) &&
                       !pane.BreadcrumbText.Contains(left, StringComparison.Ordinal),
                    "The location still exposes a clipped full path rather than the leaf breadcrumb.");
                Assert(pane.PathBox.Text == Path.Combine(left, "Design"), "The editable exact path was lost.");
                var ancestor = pane.BreadcrumbAncestorButtons.LastOrDefault();
                Assert(ancestor is not null, "The location does not expose a navigable parent folder segment.");
                ancestor!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await pane.LoadingTask;
                Assert(pane.CurrentDirectory == left && !pane.PathBox.IsVisible,
                    "Clicking a breadcrumb ancestor did not navigate to the parent folder.");
                Assert(pane.VisibleEntries.Any(entry => entry.Name == "Project notes.txt"),
                    "Ancestor navigation did not enumerate the parent folder's real files.");
                await pane.NavigateAsync(Path.Combine(left, "Design"));
                pane.BreadcrumbButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await Task.Yield();
                Assert(pane.PathBox.IsVisible && pane.PathBox.IsFocused && !pane.BreadcrumbButton.IsVisible,
                    "Clicking the breadcrumb did not focus the existing full-path editor.");
                pane.FocusList();
                await Task.Yield();
                Assert(pane.BreadcrumbButton.IsVisible && !pane.PathBox.IsVisible,
                    "Leaving path edit mode did not restore the compact location.");
                await pane.NavigateAsync(left);
                Assert(pane.CurrentDirectory == left && pane.BreadcrumbButton.IsVisible &&
                       pane.BreadcrumbText.EndsWith("Work", StringComparison.Ordinal),
                    "Breadcrumb navigation did not restore the original folder.");
                await window.SaveSettingsAsync();
                var reloaded = new SettingsStore(state).Load();
                Assert(reloaded.Panes[0].Tabs[reloaded.Panes[0].ActiveTab].Path == left,
                    "Persisted folder location did not survive a fresh settings reload.");
                if (output is not null)
                    SaveFrame(window, Path.Combine(output, "opusmac-reference-breadcrumb-1080x847.png"));
            });
            await CheckAsync("Filtering and refresh retain the current filter", async () =>
            {
                var pane = window.ActivePane; pane.FilterText = "*.txt";
                await WaitAsync(() => pane.VisibleEntries.Count == 1 && pane.VisibleEntries[0].Name == "Project notes.txt");
                await pane.RefreshAsync();
                Assert(pane.FilterText == "*.txt", "Refresh lost the filter.");
                pane.FilterText = ""; await WaitAsync(() => pane.VisibleEntries.Count == 5);
            });
            await CheckAsync("Tabs open and close actual folders", async () =>
            {
                var pane = window.ActivePane; await pane.AddTabAsync(Path.Combine(left, "Design"));
                Assert(pane.TabCount == 2 && pane.VisibleEntries.Any(entry => entry.Name == "Layout.txt"), "New tab did not browse its folder.");
                pane.CloseCurrentTab(); await pane.LoadingTask;
                Assert(pane.TabCount == 1 && pane.CurrentDirectory == left, "Closing a tab did not restore the original folder.");
            });
            await CheckAsync("Copy Files button and Undo change real fixture files", async () =>
            {
                var path = Path.Combine(left, "Project notes.txt");
                window.ActivePane.SelectPaths([path]);
                Click(window, "Copy Files"); await window.LastCommand;
                Assert(File.Exists(Path.Combine(right, "Project notes.txt")), "Copy did not create the destination file.");
                Assert(await File.ReadAllTextAsync(path) == await File.ReadAllTextAsync(Path.Combine(right, "Project notes.txt")), "Copy contents differ.");
                Click(window, "Undo"); await window.LastCommand;
                Assert(!File.Exists(Path.Combine(right, "Project notes.txt")), "Undo did not remove the copied file.");
            });
            await CheckAsync("Local create refuses same-path destination replacement before journal mutation", async () =>
            {
                var originalFolder = Path.Combine(right, "Create Review – ünicode folder");
                var movedFolder = Path.Combine(right, "Create Review – original hold");
                Directory.CreateDirectory(originalFolder);
                var sentinel = Path.Combine(originalFolder, "original-bytes.txt");
                await File.WriteAllTextAsync(sentinel, "original folder identity must survive");
                await window.ActivePane.NavigateAsync(originalFolder);
                var before = window.OperationHistory.Count;
                window.Dispatch("new-file");
                var prompt = await FindDialogAsync(window, "New Text File");
                prompt.GetVisualDescendants().OfType<TextBox>().Single().Text = "never-created-über.txt";
                Directory.Move(originalFolder, movedFolder);
                Directory.CreateDirectory(originalFolder);
                Click(prompt, "Create"); await window.LastCommand;
                var createdInReplacement = File.Exists(Path.Combine(originalFolder, "never-created-über.txt"));
                var journalChanged = window.OperationHistory.Count != before;
                var errorText = window.StatusMessage;
                if ((createdInReplacement || journalChanged) && output is not null)
                    await File.WriteAllTextAsync(Path.Combine(output, "local-create-destination-drift.json"),
                        JsonSerializer.Serialize(new { createdInReplacement, journalChanged, errorText },
                            new JsonSerializerOptions { WriteIndented = true }));
                Assert(!createdInReplacement && !journalChanged &&
                    errorText.Contains("destination folder changed", StringComparison.OrdinalIgnoreCase) &&
                    await File.ReadAllTextAsync(Path.Combine(movedFolder, "original-bytes.txt")) ==
                    "original folder identity must survive",
                    "Local New Text File wrote into a replacement folder during its naming prompt.");
                await window.ActivePane.NavigateAsync(movedFolder);
                var validCreatedPath = Path.Combine(movedFolder, "valid – über.txt");
                var createClock = Stopwatch.StartNew();
                window.Dispatch("new-file");
                prompt = await FindDialogAsync(window, "New Text File");
                prompt.GetVisualDescendants().OfType<TextBox>().Single().Text = "valid – über.txt";
                Click(prompt, "Create"); await window.LastCommand;
                createClock.Stop();
                var createdOperation = window.LastOperation
                    ?? throw new InvalidOperationException("Valid Unicode create returned no operation receipt.");
                Assert(createdOperation.Succeeded && File.Exists(validCreatedPath) &&
                    window.OperationHistory.Single(item => item.Id == createdOperation.Id).CanUndo,
                    "Valid Unicode create failed after the rejected destination change.");
                var reopenedClock = Stopwatch.StartNew();
                await window.CloseForDiagnosticsAsync();
                window = new MainWindow(launch);
                window.Show(); await window.InitializeAsync();
                reopenedClock.Stop();
                Assert(window.OperationHistory.Single(item => item.Id == createdOperation.Id).CanUndo,
                    "The journal lost Unicode file creation across cold reopen.");
                var undoClock = Stopwatch.StartNew();
                window.Dispatch("undo"); await window.LastCommand;
                undoClock.Stop();
                Assert(window.LastOperation is { Succeeded: true } && !File.Exists(validCreatedPath) &&
                    window.OperationHistory.Single(item => item.Id == createdOperation.Id).Status == "Undone" &&
                    await File.ReadAllTextAsync(Path.Combine(movedFolder, "original-bytes.txt")) ==
                        "original folder identity must survive",
                    "Cold-reopened Undo did not remove only the generated Unicode file.");
                Console.WriteLine($"METRIC local_create_ms={createClock.Elapsed.TotalMilliseconds:F2} " +
                    $"local_create_reopen_ms={reopenedClock.Elapsed.TotalMilliseconds:F2} " +
                    $"local_create_undo_ms={undoClock.Elapsed.TotalMilliseconds:F2}");
            });
            await CheckAsync("Local Copy and Move retain exact Undo across cold reopen", async () =>
            {
                MenuItem UndoMenu() => window.GetVisualDescendants().OfType<Menu>()
                    .SelectMany(menu => menu.Items.OfType<MenuItem>())
                    .Single(item => Equals(item.Header, "_Edit")).Items.OfType<MenuItem>()
                    .Single(item => Equals(item.Header, "Undo"));
                Assert(UndoMenu().IsEnabled == window.CanUndoOperation,
                    "The visible Edit/Undo command was enabled without an undoable journal operation.");
                var copySource = Path.Combine(left, "cold-reopen-copy.txt");
                var moveSource = Path.Combine(left, "cold-reopen-move.txt");
                var copyDestination = Path.Combine(right, Path.GetFileName(copySource));
                var moveDestination = Path.Combine(right, Path.GetFileName(moveSource));
                var copyBytes = Encoding.UTF8.GetBytes("Original Copy content survives a reopened journal.");
                var moveBytes = Encoding.UTF8.GetBytes("Original Move content returns after a reopened Undo.");
                await File.WriteAllBytesAsync(copySource, copyBytes);
                await File.WriteAllBytesAsync(moveSource, moveBytes);
                await window.ActivePane.NavigateAsync(left);
                await window.OtherPane.NavigateAsync(right);

                async Task<(string OperationId, long ActionMs, long ReopenMs, long UndoMs)> RoundTripAsync(
                    string action, string source, string destination, byte[] expected, bool movesSource)
                {
                    await window.ActivePane.RefreshAsync();
                    await window.OtherPane.RefreshAsync();
                    window.ActivePane.SelectPaths([source]);
                    Assert(window.ActivePane.ActionEntries.Count == 1 &&
                           window.ActivePane.ActionEntries[0].FullPath == source,
                        $"The {action} fixture did not select its exact current source.");
                    var historyBefore = window.OperationHistory.Count;
                    var actionTime = Stopwatch.StartNew();
                    Click(window, action);
                    await window.LastCommand;
                    actionTime.Stop();
                    var operation = window.LastOperation
                        ?? throw new InvalidOperationException($"{action} produced no file-operation receipt.");
                    Assert(operation.Succeeded && !operation.Canceled && operation.Changes.Count == 1 &&
                           operation.Changes[0].Source == source &&
                           operation.Changes[0].Destination == destination &&
                           window.OperationHistory.Count == historyBefore + 1,
                        $"{action} did not commit the exact source/destination and durable journal: {window.StatusMessage}");
                    Assert(File.Exists(destination) && (await File.ReadAllBytesAsync(destination)).SequenceEqual(expected) &&
                           File.Exists(source) != movesSource,
                        $"{action} did not produce the expected source/destination bytes before reopen.");
                    await WaitAsync(() => UndoMenu().IsEnabled == window.CanUndoOperation);

                    var reopenTime = Stopwatch.StartNew();
                    await window.CloseForDiagnosticsAsync();
                    window = new MainWindow(launch);
                    window.Show();
                    await window.InitializeAsync();
                    reopenTime.Stop();
                    var reopened = window.OperationHistory.SingleOrDefault(item => item.Id == operation.Id);
                    Assert(reopened is { CanUndo: true, Status: "Succeeded" },
                        $"{action} lost exact Undo authority when the file manager reopened.");
                    Assert(UndoMenu().IsEnabled, "Reopened Edit/Undo was disabled for a safely undoable operation.");

                    var undoTime = Stopwatch.StartNew();
                    Click(window, "Undo");
                    await window.LastCommand;
                    undoTime.Stop();
                    Assert(window.LastOperation is { Succeeded: true, Canceled: false } &&
                           File.Exists(source) && (await File.ReadAllBytesAsync(source)).SequenceEqual(expected) &&
                           !File.Exists(destination),
                        $"{action} Undo after reopening did not restore the exact original bytes.");
                    var undone = window.OperationHistory.Single(item => item.Id == operation.Id);
                    Assert(!undone.CanUndo && undone.Status == "Undone",
                        $"{action} Undo did not persist in the live operation journal.");
                    await WaitAsync(() => UndoMenu().IsEnabled == window.CanUndoOperation);
                    return (operation.Id, actionTime.ElapsedMilliseconds, reopenTime.ElapsedMilliseconds,
                        undoTime.ElapsedMilliseconds);
                }

                var copied = await RoundTripAsync("Copy Files", copySource, copyDestination, copyBytes, false);
                var moved = await RoundTripAsync("Move Files", moveSource, moveDestination, moveBytes, true);
                await window.CloseForDiagnosticsAsync();
                window = new MainWindow(launch);
                window.Show();
                await window.InitializeAsync();
                foreach (var id in new[] { copied.OperationId, moved.OperationId })
                    Assert(window.OperationHistory.Single(item => item.Id == id) is { CanUndo: false, Status: "Undone" },
                        $"The second cold reopen lost the completed Undo state for {id}.");
                if (output is not null)
                    await File.WriteAllTextAsync(Path.Combine(output, "local-cold-reopen-undo.json"),
                        JsonSerializer.Serialize(new {
                            copy = new { copied.OperationId, copied.ActionMs, copied.ReopenMs, copied.UndoMs,
                                Sha256 = Convert.ToHexString(SHA256.HashData(copyBytes)) },
                            move = new { moved.OperationId, moved.ActionMs, moved.ReopenMs, moved.UndoMs,
                                Sha256 = Convert.ToHexString(SHA256.HashData(moveBytes)) },
                            sourceBytesUnchanged = File.ReadAllBytes(copySource).SequenceEqual(copyBytes) &&
                                File.ReadAllBytes(moveSource).SequenceEqual(moveBytes),
                            destinationsAbsent = !File.Exists(copyDestination) && !File.Exists(moveDestination),
                            secondColdReopen = true
                        }, new JsonSerializerOptions { WriteIndented = true }));
            });
            await CheckAsync("Checkbox Mode keeps checks separate from selection and uses them for Copy", async () =>
            {
                var source = Directory.CreateDirectory(Path.Combine(root, "Checkbox source")).FullName;
                var destination = Directory.CreateDirectory(Path.Combine(root, "Checkbox destination")).FullName;
                var first = Path.Combine(source, "first.txt");
                var second = Path.Combine(source, "second.txt");
                await File.WriteAllTextAsync(first, "checked source");
                await File.WriteAllTextAsync(second, "selected source");
                var pane = window.ActivePane;
                var other = window.OtherPane;
                try
                {
                    await pane.NavigateAsync(source);
                    await other.NavigateAsync(destination);
                    pane.SelectPaths([first]);
                    Click(window, "Selection to Checkboxes"); await window.LastCommand;
                    Assert(pane.CheckboxMode && pane.ActionEntries.Single().FullPath == first && !other.CheckboxMode,
                        "Selection to Checkboxes did not enable the mode for the source tab only.");
                    pane.SelectPaths([second]);
                    Click(window, "Copy Files"); await window.LastCommand;
                    Assert(File.ReadAllText(Path.Combine(destination, "first.txt")) == "checked source" &&
                        !File.Exists(Path.Combine(destination, "second.txt")) &&
                        pane.SelectedEntries.Single().FullPath == second, "Copy used ordinary selection instead of checks.");
                    pane.FocusList();
                    Press(window, PhysicalKey.Space); await window.LastCommand;
                    Assert(pane.ActionEntries.Count == 2, "Space did not check the selected row.");
                    window.UpdateLayout();
                    var checkbox = pane.FileGrid.GetVisualDescendants().OfType<CheckBox>().Single(control =>
                        control.IsVisible && AutomationProperties.GetName(control) == "Check first.txt");
                    var point = checkbox.TranslatePoint(new Point(checkbox.Bounds.Width / 2, checkbox.Bounds.Height / 2), window)
                        ?? throw new InvalidOperationException("The first checkbox is not arranged.");
                    window.MouseDown(point, MouseButton.Left, RawInputModifiers.None);
                    window.MouseUp(point, MouseButton.Left, RawInputModifiers.None);
                    Assert(pane.ActionEntries.Single().FullPath == second && pane.SelectedEntries.Single().FullPath == second,
                        "Clicking a checkbox did not clear its check independently from selection.");
                    pane.SelectPaths([first]);
                    Click(window, "Checkboxes to Selection"); await window.LastCommand;
                    Assert(pane.SelectedEntries.Single().FullPath == second && pane.CheckboxMode, "Checkboxes to Selection did not convert checks.");
                    Click(window, "Invert Selection"); await window.LastCommand;
                    Assert(pane.ActionEntries.Single().FullPath == first && pane.SelectedEntries.Single().FullPath == second,
                        "Invert changed ordinary selection instead of checks.");
                    // Checkbox Mode and the active-only lister binding must
                    // keep checked file operations separate from row selection
                    // while users switch among all three visual layouts.
                    pane.SetViewMode("Tiles");
                    window.UpdateLayout();
                    var tileChecks = pane.GetVisualDescendants().OfType<CheckBox>()
                        .Where(control => control.IsVisible && control.Name == "FileRowCheckbox").ToArray();
                    Assert(tileChecks.Length == 2 && pane.ActionEntries.Single().FullPath == first &&
                           pane.SelectedEntries.Single().FullPath == second &&
                           pane.FileGrid.ItemsSource is null,
                        "Switching to Tiles lost checked or selected files, or retained hidden Details rows.");
                    pane.SetViewMode("Filmstrip");
                    window.UpdateLayout();
                    Assert(pane.ActionEntries.Single().FullPath == first &&
                           pane.SelectedEntries.Single().FullPath == second,
                        "Filmstrip view changed checked files or selected rows.");
                    pane.SetViewMode("Details");
                    window.UpdateLayout();
                    Assert(pane.ActionEntries.Single().FullPath == first &&
                           pane.SelectedEntries.Single().FullPath == second,
                        "Returning to Details changed the checked-file action target.");
                    var third = Path.Combine(source, "third-after-view-change.txt");
                    await File.WriteAllTextAsync(third, "checked after switching layouts");
                    await pane.RefreshAsync();
                    pane.SelectActionPaths([third]);
                    pane.SelectPaths([second]);
                    pane.SetViewMode("Tiles");
                    Assert(pane.ActionEntries.Single().FullPath == third &&
                           pane.SelectedEntries.Single().FullPath == second,
                        "A fresh checked file replaced the selected row after switching views.");
                    Click(window, "Copy Files"); await window.LastCommand;
                    Assert(await File.ReadAllTextAsync(Path.Combine(destination, Path.GetFileName(third))) ==
                               "checked after switching layouts" &&
                           !File.Exists(Path.Combine(destination, Path.GetFileName(second))) &&
                           pane.SelectedEntries.Single().FullPath == second,
                        "Copy after switching views acted on the selected row rather than the checked file.");
                    pane.SetViewMode("Details");
                    // A filter must not silently promote hidden checked files
                    // into a Copy operation. The original checks should still
                    // be available when the user clears that filter.
                    pane.FilterText = "second";
                    await pane.FilteringTask;
                    Assert(pane.VisibleEntries.Single().FullPath == second &&
                           pane.ActionEntries.Count == 0,
                        "Filtering out checked files left an invisible Copy target.");
                    var beforeHiddenCopy = window.OperationHistory.Count;
                    Click(window, "Copy Files"); await window.LastCommand;
                    Assert(window.OperationHistory.Count == beforeHiddenCopy &&
                           !File.Exists(Path.Combine(destination, Path.GetFileName(second))) &&
                           File.Exists(third),
                        "Copy unexpectedly acted on hidden checked paths or the visible selected file.");
                    pane.FilterText = "";
                    await pane.FilteringTask;
                    Assert(pane.ActionEntries.Single().FullPath == third,
                        "Clearing a filter lost the previously checked file.");
                    await window.SaveSettingsAsync();
                    var saved = new SettingsStore(state).Load();
                    Assert(saved.Panes[0].Tabs.Any(tab => tab.Path == source && tab.CheckboxMode) &&
                        saved.Panes[1].Tabs.All(tab => !tab.CheckboxMode), "Checkbox Mode did not persist per tab.");
                    if (output is not null) SaveFrame(window, Path.Combine(output, "opusmac-checkbox-mode.png"), true);
                }
                finally
                {
                    pane.SetCheckboxMode(false);
                    await pane.NavigateAsync(left);
                    await other.NavigateAsync(right);
                }
            });
            await CheckAsync("Select Duplicates keeps a reviewed copy and refuses changed contents", async () =>
            {
                var folder = Directory.CreateDirectory(Path.Combine(root, "Duplicate selection")).FullName;
                var subfolder = Directory.CreateDirectory(Path.Combine(folder, "Sub")).FullName;
                var oldest = Path.Combine(folder, "older.txt");
                var newest = Path.Combine(subfolder, "newer.txt");
                await File.WriteAllTextAsync(oldest, "same");
                await File.WriteAllTextAsync(newest, "same");
                await File.WriteAllTextAsync(Path.Combine(folder, "unique.txt"), "else");
                var wav = Path.Combine(folder, "track.wav");
                var mp3 = Path.Combine(subfolder, "track.mp3");
                await File.WriteAllTextAsync(wav, "wave bytes");
                await File.WriteAllTextAsync(mp3, "different bytes");
                var stamp = DateTime.UtcNow.Date.AddDays(-1);
                File.SetLastWriteTimeUtc(oldest, stamp);
                File.SetLastWriteTimeUtc(newest, stamp.AddHours(1));
                var pane = window.ActivePane;
                var original = pane.CurrentDirectory;
                try
                {
                    await pane.NavigateAsync(folder);
                    Click(window, "Find Duplicate Files…");
                    var scan = window;
                    window.UpdateLayout();
                    Assert(window.GetVisualDescendants().OfType<Border>().Single(panel => panel.Name == "DuplicateUtilityPanel").IsVisible,
                        "Find Duplicate Files did not open inside the lister.");
                    if (output is not null) SaveFrame(window, Path.Combine(output, "opusmac-duplicate-utility-panel.png"));
                    Click(scan, "Scan");
                    await window.LastCommand;
                    Assert(pane.IsSearchResults && pane.VisibleEntries.Select(entry => entry.FullPath).ToHashSet().SetEquals([oldest, newest]),
                        "The duplicate results did not publish exactly the duplicate members.");
                    var checksumGrid = pane.GetVisualDescendants().OfType<DataGrid>().Single(control => control.Name == "FileList");
                    var checksumGroups = ((Avalonia.Collections.DataGridCollectionView)checksumGrid.ItemsSource!).Groups!;
                    var checksumLabel = checksumGroups.Cast<Avalonia.Collections.DataGridCollectionViewGroup>().Single().Key.ToString();
                    Assert(checksumLabel == "51037A4A37730F52C8732586D3AAA316",
                        "The duplicate group did not use the full MD5 checksum of the fixture.");
                    Assert(!window.OwnedWindows.Any(dialog => dialog.IsVisible), "Scanning left a separate result window open.");
                    if (output is not null) SaveFrame(window, Path.Combine(output, "opusmac-duplicate-lister-results.png"));
                    async Task ChooseAsync(int rule, bool fromPanel = false)
                    {
                        Click(window, fromPanel ? "Select…" : "Select Duplicates…");
                        var dialog = await FindDialogAsync(window, "Duplicates Selection");
                        dialog.GetVisualDescendants().OfType<ComboBox>().Single(control => control.Name == "DuplicateKeepRule").SelectedIndex = rule;
                        Click(dialog, "Select"); await window.LastCommand;
                    }
                    await ChooseAsync(0, fromPanel: true);
                    Assert(pane.SelectedEntries.Single().FullPath == newest, "Keep oldest selected the wrong duplicate.");
                    await ChooseAsync(1);
                    Assert(pane.SelectedEntries.Single().FullPath == oldest, "Keep newest selected the wrong duplicate.");
                    Click(window, "Select Duplicates…");
                    var branchDialog = await FindDialogAsync(window, "Duplicates Selection");
                    branchDialog.UpdateLayout();
                    var branchBoxes = branchDialog.GetVisualDescendants().OfType<CheckBox>().ToArray();
                    var branch = branchBoxes.Single(control => Equals(control.Tag, subfolder));
                    var parentBranch = branchBoxes.Single(control => Equals(control.Tag, folder));
                    branch.IsChecked = false;
                    Assert(parentBranch.IsChecked is null, "Excluding a subfolder did not mark its parent as partly checked.");
                    Click(branchDialog, "Select");
                    await window.LastCommand;
                    Assert(pane.SelectedEntries.Single().FullPath == oldest,
                        "A duplicate from the excluded branch was selected, or the included copy was not selectable.");
                    Click(window, "Select Duplicates…");
                    var preferredDialog = await FindDialogAsync(window, "Duplicates Selection");
                    preferredDialog.GetVisualDescendants().OfType<TextBox>().Single(control => control.Name == "DuplicatePreferredLocations").Text = subfolder;
                    preferredDialog.GetVisualDescendants().OfType<CheckBox>().Single(control => control.Name == "DuplicateRememberLocations").IsChecked = true;
                    Click(preferredDialog, "Select");
                    await window.LastCommand;
                    Assert(pane.SelectedEntries.Single().FullPath == oldest &&
                        new SettingsStore(state).Load().DuplicatePreferredLocations.SequenceEqual([subfolder]),
                        "The preferred folder did not override Keep oldest or persist.");
                    Click(window, "Select Duplicates…");
                    preferredDialog = await FindDialogAsync(window, "Duplicates Selection");
                    var preferred = preferredDialog.GetVisualDescendants().OfType<TextBox>().Single(control => control.Name == "DuplicatePreferredLocations");
                    Assert(preferred.Text == subfolder, "The selection dialog did not restore the preferred folder.");
                    preferred.Text = "";
                    preferredDialog.GetVisualDescendants().OfType<ComboBox>().Single(control => control.Name == "DuplicateKeepRule").SelectedIndex = 1;
                    preferredDialog.GetVisualDescendants().OfType<CheckBox>().Single(control => control.Name == "DuplicateRememberLocations").IsChecked = true;
                    Click(preferredDialog, "Select");
                    await window.LastCommand;
                    Assert(new SettingsStore(state).Load().DuplicatePreferredLocations.Count == 0, "The remembered folder could not be cleared.");
                    await File.WriteAllTextAsync(newest, "diff");
                    File.SetLastWriteTimeUtc(newest, stamp.AddHours(1));
                    await ChooseAsync(0);
                    Assert(pane.SelectedEntries.Single().FullPath == oldest &&
                        window.StatusMessage.Contains("duplicate result changed", StringComparison.OrdinalIgnoreCase),
                        "A changed duplicate was selected after its timestamp was restored.");
                    Click(window, "Find Duplicate Files…");
                    window.UpdateLayout();
                    scan.GetVisualDescendants().OfType<ComboBox>().Single(control => control.Name == "DuplicateComparisonMode").SelectedIndex = 2;
                    Click(scan, "Scan");
                    await window.LastCommand;
                    Assert(scan.GetVisualDescendants().OfType<TextBlock>().Any(block => block.Name == "DuplicateScanFeedback" &&
                        block.Text?.Contains("File contents may differ") == true),
                        "Metadata matches were not distinguished from content matches.");
                    Assert(pane.VisibleEntries.Select(entry => entry.FullPath).ToHashSet().SetEquals([wav, mp3]),
                        $"Filename stem mode did not group different extensions. {window.StatusMessage} Rows: {string.Join(", ", pane.VisibleEntries.Select(entry => entry.Name))}");
                    await ChooseAsync(2);
                    Assert(pane.SelectedEntries.Count == 1, "Metadata-based selection did not keep one reviewed member.");
                    var secondRoot = Directory.CreateDirectory(Path.Combine(root, "Another duplicate root")).FullName;
                    var external = Path.Combine(root, "reference.wav");
                    var anotherCopy = Path.Combine(secondRoot, "copy.wav");
                    await File.WriteAllTextAsync(external, "wave bytes");
                    await File.WriteAllTextAsync(anotherCopy, "wave bytes");
                    await File.WriteAllTextAsync(Path.Combine(secondRoot, "unrelated copy.txt"), "else");
                    File.SetLastWriteTimeUtc(external, stamp.AddDays(-1));
                    Click(window, "Find Duplicate Files…");
                    window.UpdateLayout();
                    scan.GetVisualDescendants().OfType<TextBox>().Single(control => control.Name == "DuplicateScanFolder").Text =
                        folder + Environment.NewLine + secondRoot;
                    scan.GetVisualDescendants().OfType<TextBox>().Single(control => control.Name == "DuplicateReferenceFiles").Text = external;
                    scan.GetVisualDescendants().OfType<CheckBox>().Single(control => control.Name == "DuplicateSearchSubfolders").IsChecked = false;
                    Click(scan, "Scan");
                    await window.LastCommand;
                    Assert(pane.VisibleEntries.Select(entry => entry.FullPath).ToHashSet().SetEquals([wav, anotherCopy]),
                        "The targeted scan did not use both roots or exposed an outside reference as a selectable row.");
                    await ChooseAsync(0);
                    Assert(pane.SelectedEntries.Select(entry => entry.FullPath).ToHashSet().SetEquals([wav, anotherCopy]),
                        "Keeping an older outside reference did not select both verified copies.");

                    var atMinimum = Path.Combine(secondRoot, "minimum.bin");
                    var minimumCopy = Path.Combine(secondRoot, "minimum-copy.bin");
                    await File.WriteAllBytesAsync(atMinimum, new byte[1024]);
                    await File.WriteAllBytesAsync(minimumCopy, new byte[1024]);
                    Click(window, "Find Duplicate Files…");
                    window.UpdateLayout();
                    scan.GetVisualDescendants().OfType<TextBox>().Single(control => control.Name == "DuplicateScanFolder").Text = secondRoot;
                    var minimum = scan.GetVisualDescendants().OfType<TextBox>().Single(control => control.Name == "DuplicateMinimumSize");
                    minimum.Text = "-1";
                    Click(scan, "Scan");
                    Assert(scan.IsVisible && scan.GetVisualDescendants().OfType<TextBlock>().Any(block =>
                        block.Text?.Contains("nonnegative whole number") == true), "A negative minimum size was accepted.");
                    minimum.Text = "1";
                    Click(scan, "Scan");
                    await window.LastCommand;
                    Assert(pane.VisibleEntries.Select(entry => entry.FullPath).ToHashSet().SetEquals([atMinimum, minimumCopy]),
                        "The 1 KB minimum did not include the exact boundary and exclude smaller duplicates.");
                    if (OperatingSystem.IsWindows())
                    {
                        var attributes = File.GetAttributes(atMinimum);
                        File.SetAttributes(atMinimum, attributes | FileAttributes.Offline);
                        try
                        {
                            foreach (var ignoreOffline in new[] { true, false })
                            {
                                Click(window, "Find Duplicate Files…");
                                window.UpdateLayout();
                                scan.GetVisualDescendants().OfType<TextBox>().Single(control => control.Name == "DuplicateScanFolder").Text = secondRoot;
                                scan.GetVisualDescendants().OfType<TextBox>().Single(control => control.Name == "DuplicateMinimumSize").Text = "1";
                                var checkbox = scan.GetVisualDescendants().OfType<CheckBox>().Single(control => control.Name == "DuplicateIgnoreOffline");
                                Assert(checkbox.IsChecked == true, "Ignore offline files did not start enabled.");
                                checkbox.IsChecked = ignoreOffline;
                                Click(scan, "Scan");
                                await window.LastCommand;
                                Assert(pane.IsSearchResults && pane.VisibleEntries.Count ==
                                    (ignoreOffline ? 0 : 2), "Ignore offline files did not control whether the offline copy matched.");
                            }
                        }
                        finally { File.SetAttributes(atMinimum, attributes); }
                    }
                    var hardLink = Path.Combine(secondRoot, "zz-linked-minimum.bin");
                    await new OpusMac.Core.Platform.DesktopFilePlatform().CreateHardLinkAsync(atMinimum, hardLink, CancellationToken.None);
                    Click(window, "Find Duplicate Files…");
                    window.UpdateLayout();
                    scan.GetVisualDescendants().OfType<TextBox>().Single(control => control.Name == "DuplicateScanFolder").Text = secondRoot;
                    scan.GetVisualDescendants().OfType<TextBox>().Single(control => control.Name == "DuplicateMinimumSize").Text = "1";
                    scan.GetVisualDescendants().OfType<CheckBox>().Single(control => control.Name == "DuplicateIgnoreHardLinks").IsChecked = true;
                    Click(scan, "Scan");
                    await window.LastCommand;
                    Assert(pane.VisibleEntries.Select(entry => entry.FullPath).ToHashSet().SetEquals([atMinimum, minimumCopy]),
                        "The hard-link toggle did not exclude the alias and retain both independent files.");
                    var pairA = Path.Combine(secondRoot, "large-a.bin");
                    var pairB = Path.Combine(secondRoot, "large-b.bin");
                    await File.WriteAllBytesAsync(pairA, new byte[2048]);
                    await File.WriteAllBytesAsync(pairB, new byte[2048]);
                    scan.GetVisualDescendants().OfType<ComboBox>().Single(control => control.Name == "DuplicateComparisonMode").SelectedIndex = 4;
                    var numberGroups = scan.GetVisualDescendants().OfType<CheckBox>().Single(control => control.Name == "DuplicateNumberGroups");
                    Assert(numberGroups.IsChecked == false, "Number groups was enabled by default.");
                    Click(scan, "Scan");
                    await window.LastCommand;
                    window.UpdateLayout();
                    var fileGrid = pane.GetVisualDescendants().OfType<DataGrid>().Single(control => control.Name == "FileList");
                    Assert(fileGrid.ItemsSource is Avalonia.Collections.DataGridCollectionView grouped && grouped.Groups?.Count == 2 &&
                        pane.VisibleEntries.Take(2).Select(entry => entry.FullPath).ToHashSet().SetEquals([pairA, pairB]),
                        "Two independently verified duplicate sets were not grouped in the lister.");
                    string?[] Labels() => ((Avalonia.Collections.DataGridCollectionView)fileGrid.ItemsSource!).Groups!
                        .Cast<Avalonia.Collections.DataGridCollectionViewGroup>().Select(group => group.Key.ToString()).ToArray();
                    Assert(Labels().SequenceEqual([FileEntry.FormatSize(2048), FileEntry.FormatSize(1024)]),
                        "Default duplicate group labels did not reflect the size comparison.");
                    var selectedDuplicate = pane.VisibleEntries.First().FullPath;
                    pane.SelectPaths([selectedDuplicate]);
                    pane.SetViewMode("Tiles");
                    var duplicateTiles = pane.GetVisualDescendants().OfType<ListBox>()
                        .Single(control => control.Name == "FileTiles");
                    Assert(duplicateTiles.IsVisible && fileGrid.ItemsSource is null &&
                           pane.SelectedEntries.Single().FullPath == selectedDuplicate,
                        "Grouped duplicate results lost selection or retained hidden Details rows in Tiles mode.");
                    pane.SetViewMode("Filmstrip");
                    Assert(duplicateTiles.IsVisible && fileGrid.ItemsSource is null &&
                           duplicateTiles.ItemsSource is IReadOnlyList<FileEntry> filmstripItems &&
                           filmstripItems.Count == pane.VisibleEntries.Count &&
                           pane.SelectedEntries.Single().FullPath == selectedDuplicate,
                        "Filmstrip transition lost grouped duplicate results or selection.");
                    pane.SetViewMode("Details");
                    Assert(!duplicateTiles.IsVisible && duplicateTiles.ItemsSource is null &&
                           pane.SelectedEntries.Single().FullPath == selectedDuplicate &&
                           Labels().SequenceEqual([FileEntry.FormatSize(2048), FileEntry.FormatSize(1024)]),
                        "Grouped duplicate results lost group headers or selection after restoring Details mode.");
                    numberGroups.IsChecked = true;
                    Click(scan, "Scan");
                    await window.LastCommand;
                    Assert(Labels().SequenceEqual(["1", "2"]), "Number groups did not switch the result headers.");
                    if (output is not null) SaveFrame(window, Path.Combine(output, "opusmac-duplicate-groups.png"));
                    var partialRoot = Directory.CreateDirectory(Path.Combine(root, "Partial checksum fixture")).FullName;
                    var partialA = Path.Combine(partialRoot, "a.bin");
                    var partialB = Path.Combine(partialRoot, "b.bin");
                    await File.WriteAllTextAsync(partialA, "ABCD");
                    await File.WriteAllTextAsync(partialB, "ABEF");
                    var partialText = Path.Combine(partialRoot, "c.txt");
                    await File.WriteAllTextAsync(partialText, "ABCD");
                    var accuracy = scan.GetVisualDescendants().OfType<Slider>().Single(control => control.Name == "DuplicateMd5Accuracy");
                    Assert(!accuracy.IsEffectivelyEnabled, "MD5 accuracy remained available in Size mode.");
                    scan.GetVisualDescendants().OfType<ComboBox>().Single(control => control.Name == "DuplicateComparisonMode").SelectedIndex = 0;
                    scan.GetVisualDescendants().OfType<TextBox>().Single(control => control.Name == "DuplicateScanFolder").Text = partialRoot;
                    scan.GetVisualDescendants().OfType<TextBox>().Single(control => control.Name == "DuplicateMinimumSize").Text = "";
                    var filenameFilter = scan.GetVisualDescendants().OfType<TextBox>().Single(control => control.Name == "DuplicateFilenameFilter");
                    filenameFilter.Text = "*.bin";
                    Assert(accuracy.IsEffectivelyEnabled && accuracy.Value == 100, "MD5 did not start at full accuracy.");
                    accuracy.Value = 50;
                    Click(scan, "Scan");
                    await window.LastCommand;
                    Assert(pane.VisibleEntries.Select(entry => entry.FullPath).ToHashSet().SetEquals([partialA, partialB]) &&
                        scan.GetVisualDescendants().OfType<TextBlock>().Any(block => block.Name == "DuplicateScanFeedback" &&
                            block.Text?.Contains("MD5 accuracy 50% · File contents may differ") == true),
                        "A partial match was missing or was presented as a full checksum match.");
                    await ChooseAsync(2, fromPanel: true);
                    Assert(pane.SelectedEntries.Single().FullPath == partialB, "Selection did not recheck the scanned prefix.");
                    accuracy.Value = 100;
                    Click(scan, "Scan");
                    await window.LastCommand;
                    Assert(pane.VisibleEntries.Count == 0, "Full MD5 matched files that only share a prefix.");
                    filenameFilter.Text = "*.bin;!b.bin";
                    scan.GetVisualDescendants().OfType<TextBox>().Single(control => control.Name == "DuplicateReferenceFiles").Text = partialText;
                    Click(scan, "Scan");
                    await window.LastCommand;
                    Assert(pane.VisibleEntries.Single().FullPath == partialA,
                        "The scan did not honor its exclusion pattern or explicit reference outside the filter.");
                    var wildcardExcluded = Directory.CreateDirectory(Path.Combine(partialRoot, "ignore-renders")).FullName;
                    var pathExcluded = Directory.CreateDirectory(Path.Combine(partialRoot, "path fixture")).FullName;
                    var wildcardCopy = Path.Combine(wildcardExcluded, "wildcard.bin");
                    var pathCopy = Path.Combine(pathExcluded, "absolute.bin");
                    File.Copy(partialA, wildcardCopy);
                    File.Copy(partialA, pathCopy);
                    var excludedLocations = scan.GetVisualDescendants().OfType<TextBox>().Single(control => control.Name == "DuplicateExcludedLocations");
                    Click(scan, "Scan");
                    await window.LastCommand;
                    Assert(pane.VisibleEntries.Select(entry => entry.FullPath).ToHashSet().SetEquals([partialA, wildcardCopy, pathCopy]),
                        "The baseline duplicate scan did not descend into both subfolders.");
                    excludedLocations.Text = "ignore-*" + Environment.NewLine + pathExcluded;
                    Click(scan, "Scan");
                    await window.LastCommand;
                    Assert(pane.VisibleEntries.Single().FullPath == partialA, "A duplicate in an excluded subtree was scanned.");
                    excludedLocations.Text = "regex:ignore-renders$" + Environment.NewLine + pathExcluded;
                    Click(scan, "Scan");
                    await window.LastCommand;
                    Assert(pane.VisibleEntries.Single().FullPath == partialA, "The regular expression did not exclude its folder.");
                    var destination = window.OtherPane;
                    var previousTabs = destination.TabCount;
                    var previousDirectory = destination.CurrentDirectory;
                    scan.GetVisualDescendants().OfType<ComboBox>().Single(control => control.Name == "DuplicateResultsPane").SelectedIndex = 1;
                    scan.GetVisualDescendants().OfType<CheckBox>().Single(control => control.Name == "DuplicateResultsNewTab").IsChecked = true;
                    excludedLocations.Text = "";
                    try
                    {
                        Click(scan, "Scan");
                        await window.LastCommand;
                        Assert(destination.TabCount == previousTabs + 1 && destination.IsSearchResults &&
                            destination.VisibleEntries.Select(entry => entry.FullPath).ToHashSet().SetEquals([partialA, wildcardCopy, pathCopy]) &&
                            pane.VisibleEntries.Single().FullPath == partialA,
                            "The duplicate scan did not publish into a new destination tab without replacing the source.");
                        destination.NextTab(-1);
                        await destination.LoadingTask;
                        Assert(!destination.IsSearchResults && destination.CurrentDirectory == previousDirectory,
                            "Switching away from the results did not restore the previous folder.");
                        destination.NextTab(1);
                        await destination.LoadingTask;
                        Assert(destination.CurrentDuplicateResult is not null &&
                            destination.VisibleEntries.Select(entry => entry.FullPath).ToHashSet().SetEquals([partialA, wildcardCopy, pathCopy]),
                            "Returning to a duplicate tab did not restore the reviewed results.");
                        await ChooseAsync(2, fromPanel: true);
                        Assert(destination.SelectedEntries.Count > 0 && pane.SelectedEntries.Count == 0,
                            "The duplicate panel selected the source instead of the result tab.");
                        destination.NextTab(-1);
                        await destination.LoadingTask;
                        File.Move(pathCopy, pathCopy + ".previous");
                        File.Copy(partialA, pathCopy);
                        destination.NextTab(1);
                        await destination.LoadingTask;
                        Assert(destination.IsSearchResults && destination.VisibleEntries.Count == 0 && destination.CurrentDuplicateResult is null,
                            "Returning to a duplicate tab exposed a replacement file without a fresh scan.");
                    }
                    finally
                    {
                        if (destination.TabCount > previousTabs) destination.CloseCurrentTab();
                        await destination.LoadingTask;
                    }
                    Assert(destination.TabCount == previousTabs && destination.CurrentDirectory == previousDirectory && !destination.IsSearchResults,
                        "Closing the result tab did not restore the previous destination tab.");
                    Click(window, "Save preset…");
                    var presetDialog = await FindDialogAsync(window, "Save Duplicate Search Preset");
                    presetDialog.GetVisualDescendants().OfType<TextBox>().Single().Text = "Fixture duplicate search";
                    Click(presetDialog, "Save");
                    await WaitAsync(() => new SettingsStore(state).Load().SavedDuplicateSearches.Any(item =>
                        item.Name == "Fixture duplicate search" && item.Destination && item.NewTab &&
                        item.Options.FilenameFilter == "*.bin;!b.bin" && item.Roots.SequenceEqual([partialRoot])));
                    await WaitAsync(() => FindButton(window, "Close panel")?.IsEffectivelyEnabled == true);
                    Click(window, "Close panel");
                    Click(window, "Find Duplicate Files…");
                    window.UpdateLayout();
                    var presets = scan.GetVisualDescendants().OfType<ComboBox>().Single(control => control.Name == "DuplicateSearchPresets");
                    presets.SelectedItem = presets.ItemsSource!.Cast<SavedDuplicateSearch>().Single(item => item.Name == "Fixture duplicate search");
                    Assert(scan.GetVisualDescendants().OfType<TextBox>().Single(control => control.Name == "DuplicateScanFolder").Text == partialRoot &&
                        scan.GetVisualDescendants().OfType<TextBox>().Single(control => control.Name == "DuplicateFilenameFilter").Text == "*.bin;!b.bin" &&
                        scan.GetVisualDescendants().OfType<CheckBox>().Single(control => control.Name == "DuplicateResultsNewTab").IsChecked == true,
                        "Loading the preset did not restore the recorded scan setup.");
                    Click(window, "Remove preset…");
                    presetDialog = await FindDialogAsync(window, "Remove Duplicate Search Preset");
                    Click(presetDialog, "Remove");
                    await WaitAsync(() => new SettingsStore(state).Load().SavedDuplicateSearches.Count == 0);
                    await WaitAsync(() => FindButton(window, "Close panel")?.IsEffectivelyEnabled == true);
                    Click(window, "Close panel");
                    Click(window, "Find Duplicate Files…");
                    window.UpdateLayout();
                    var rangeRoot = Directory.CreateDirectory(Path.Combine(root, "Duplicate range fixture")).FullName;
                    var branches = new[] { "A", "B", "C" }.Select(name => Directory.CreateDirectory(Path.Combine(rangeRoot, name)).FullName).ToArray();
                    foreach (var path in branches) await File.WriteAllTextAsync(Path.Combine(path, "copy.txt"), "range fixture");
                    scan.GetVisualDescendants().OfType<TextBox>().Single(control => control.Name == "DuplicateScanFolder").Text = rangeRoot;
                    Click(scan, "Scan");
                    await window.LastCommand;
                    Click(scan, "Select…");
                    var rangeDialog = await FindDialogAsync(window, "Duplicates Selection");
                    rangeDialog.UpdateLayout();
                    var rangeBoxes = branches.Select(path => rangeDialog.GetVisualDescendants().OfType<CheckBox>()
                        .Single(box => Equals(box.Tag, path))).ToArray();
                    void ClickBranch(CheckBox box, RawInputModifiers modifiers)
                    {
                        var point = box.TranslatePoint(new Point(8, box.Bounds.Height / 2), rangeDialog)
                            ?? throw new InvalidOperationException("The duplicate folder checkbox is not laid out.");
                        rangeDialog.MouseDown(point, MouseButton.Left, modifiers);
                        rangeDialog.MouseUp(point, MouseButton.Left, modifiers);
                    }
                    ClickBranch(rangeBoxes[0], RawInputModifiers.None);
                    ClickBranch(rangeBoxes[2], RawInputModifiers.Shift);
                    Assert(rangeBoxes.All(box => box.IsChecked == false), "Shift-click did not uncheck the intervening folder.");
                    Click(rangeDialog, "Check all folders");
                    Assert(rangeBoxes.All(box => box.IsChecked == true), "Check all did not restore the folder range.");
                    Click(rangeDialog, "Uncheck all folders");
                    Assert(rangeBoxes.All(box => box.IsChecked == false), "Uncheck all did not clear the folder range.");
                    Click(rangeDialog, "Select");
                    await window.LastCommand;
                    Assert(pane.SelectedEntries.Count == 0, "A fully unchecked tree still selected files.");
                    Click(window, "Close panel");
                    Assert(!window.GetVisualDescendants().OfType<Border>().Single(panel => panel.Name == "DuplicateUtilityPanel").IsVisible,
                        "The duplicate utility panel remained visible after closing.");
                }
                finally { await pane.NavigateAsync(original); }
            });
            await CheckAsync("Reselect Files recalls command inputs and rejects same-path replacements", async () =>
            {
                var folder = Directory.CreateDirectory(Path.Combine(root, "Reselect fixture")).FullName;
                var first = Path.Combine(folder, "first.txt");
                var second = Path.Combine(folder, "second.txt");
                await File.WriteAllTextAsync(first, "first");
                await File.WriteAllTextAsync(second, "second");
                var pane = window.ActivePane;
                var original = pane.CurrentDirectory;
                try
                {
                    await pane.NavigateAsync(folder);
                    pane.SelectPaths([first]);
                    Click(window, "Copy File Names"); await window.LastCommand;
                    pane.SelectPaths([second]);
                    Click(window, "Reselect Files"); await window.LastCommand;
                    Assert(pane.SelectedEntries.Select(entry => entry.FullPath).ToHashSet().SetEquals([first, second]),
                        "Reselect did not restore the clipboard input and preserve the current selection.");
                    File.Move(first, first + ".old");
                    await File.WriteAllTextAsync(first, "first");
                    await pane.RefreshAsync();
                    pane.SelectPaths([second]);
                    Click(window, "Reselect Files"); await window.LastCommand;
                    Assert(pane.SelectedEntries.Single().FullPath == second, "Reselect selected a different object at a previously used path.");

                    var renamed = Path.Combine(folder, "renamed.txt");
                    var rename = await window.RunOperationAsync(new FileOperationRequest(FileOperationKind.Rename, [second], NewName: "renamed.txt"));
                    Assert(rename.Succeeded, "Fixture rename failed.");
                    pane.SelectPaths([]);
                    Click(window, "Reselect Files"); await window.LastCommand;
                    Assert(pane.SelectedEntries.Single().FullPath == renamed, "Reselect lost the same file after renaming.");
                }
                finally { await pane.NavigateAsync(original); }
            });
            await CheckAsync("Select Other commands match names, extensions and real folder contents", async () =>
            {
                var source = Directory.CreateDirectory(Path.Combine(root, "Selection source")).FullName;
                var destination = Directory.CreateDirectory(Path.Combine(root, "Selection destination")).FullName;
                var empty = Directory.CreateDirectory(Path.Combine(source, "Empty")).FullName;
                var zero = Directory.CreateDirectory(Path.Combine(source, "Zero")).FullName;
                var nonzero = Directory.CreateDirectory(Path.Combine(source, "Nonzero")).FullName;
                await File.WriteAllBytesAsync(Path.Combine(zero, ".zero"), []);
                await File.WriteAllBytesAsync(Path.Combine(nonzero, ".real"), [1]);
                foreach (var name in new[] { "alpha.txt", "beta.TXT", "video.mov" })
                    await File.WriteAllTextAsync(Path.Combine(source, name), name);
                foreach (var name in new[] { "ALPHA.txt", "beta.md", "other.bin" })
                    await File.WriteAllTextAsync(Path.Combine(destination, name), name);
                var pane = window.ActivePane;
                var other = window.OtherPane;
                string[] Selected(PaneView view) => view.SelectedEntries.Select(entry => entry.Name).Order(StringComparer.Ordinal).ToArray();
                async Task SelectAsync(string label)
                {
                    Click(window, label);
                    await window.LastCommand;
                }
                try
                {
                    await pane.NavigateAsync(source);
                    await other.NavigateAsync(destination);
                    Assert(FindCommandMenuItem(window, "Select Same Extensions")?.Parent is MenuItem { Header: "Select Other" },
                        "Selection actions are not grouped under Select Other.");
                    pane.SelectPaths([Path.Combine(source, "alpha.txt")]);
                    await SelectAsync("Select Same Extensions");
                    Assert(Selected(pane).SequenceEqual(["alpha.txt", "beta.TXT"]), "Same Extensions did not ignore extension case.");
                    other.SelectPaths([Path.Combine(destination, "other.bin")]);
                    await SelectAsync("Select Source Selection to Destination");
                    Assert(Selected(other).SequenceEqual(["ALPHA.txt"]) && Selected(pane).Length == 2,
                        "Selected-name comparison did not replace only the destination selection.");
                    await SelectAsync("Select Source All to Destination");
                    Assert(Selected(other).SequenceEqual(["ALPHA.txt"]), "Exact comparison matched a different extension.");
                    await SelectAsync("Select Source All to Destination (ignore extensions)");
                    Assert(Selected(other).SequenceEqual(["ALPHA.txt", "beta.md"]), "Stem comparison did not match different extensions.");
                    await SelectAsync("Select Source-not-in-Destination");
                    Assert(Selected(pane).Contains("video.mov") && !Selected(pane).Contains("alpha.txt"),
                        "Source-only selection did not exclude matches.");
                    await SelectAsync("Select Empty Folders");
                    Assert(Selected(pane).SequenceEqual(["Empty"]), "An occupied folder was classified as empty.");
                    await SelectAsync("Select Zero-Byte Folders");
                    Assert(Selected(pane).SequenceEqual(["Empty", "Zero"]), "Zero-byte selection did not inspect hidden children.");
                    await SelectAsync("Select Random Item");
                    Assert(pane.SelectedEntries.Count == 1, "Random selection did not select exactly one row.");
                }
                finally { await pane.NavigateAsync(left); await other.NavigateAsync(right); }
            });
            await CheckAsync("Update commands copy only newer selected files and preserve unreviewed destinations", async () =>
            {
                var sourceRoot = Directory.CreateDirectory(Path.Combine(root, "Update source")).FullName;
                var destinationRoot = Directory.CreateDirectory(Path.Combine(root, "Update destination")).FullName;
                var source = Directory.CreateDirectory(Path.Combine(sourceRoot, "Updates")).FullName;
                var destination = Directory.CreateDirectory(Path.Combine(destinationRoot, "Updates")).FullName;
                var stamp = DateTime.UtcNow.Date.AddDays(-1);
                void Put(string folder, string file, string content, DateTime time)
                {
                    var path = Path.Combine(folder, file);
                    File.WriteAllText(path, content);
                    File.SetLastWriteTimeUtc(path, time);
                }
                Put(source, "newer.txt", "source newer", stamp.AddHours(2));
                Put(destination, "newer.txt", "destination older", stamp);
                Put(source, "older.txt", "source older", stamp);
                Put(destination, "older.txt", "destination newer", stamp.AddHours(2));
                Put(source, "equal.txt", "different source bytes", stamp);
                Put(destination, "equal.txt", "keep equal timestamp", stamp);
                Put(source, "missing.txt", "copy me", stamp);
                Put(destination, "only.txt", "keep destination only", stamp);
                Put(sourceRoot, "unselected.txt", "outside selection", stamp);
                var missingFolder = Directory.CreateDirectory(Path.Combine(source, "new folder")).FullName;
                Put(missingFolder, "child.txt", "copy child", stamp);
                var deeperFolder = Directory.CreateDirectory(Path.Combine(missingFolder, "deeper")).FullName;
                Put(deeperFolder, "leaf.txt", "copy leaf", stamp);
                try
                {
                    await window.ActivePane.NavigateAsync(sourceRoot);
                    await window.OtherPane.NavigateAsync(destinationRoot);
                    async Task<Window> OpenUpdateAsync(string mode)
                    {
                        await window.ActivePane.RefreshAsync();
                        await window.OtherPane.RefreshAsync();
                        window.ActivePane.SelectPaths([source]);
                        Click(window, mode);
                        await WaitAsync(() => window.LastCommand.IsCompleted || window.OwnedWindows.Any(item => item.Title == mode && item.IsVisible));
                        Assert(!window.LastCommand.IsCompleted, $"{mode} did not open review. {window.StatusMessage}");
                        return await FindDialogAsync(window, mode);
                    }
                    var dialog = await OpenUpdateAsync("Update Existing");
                    Click(dialog, "Apply copy plan"); await window.LastCommand;
                    Assert(File.ReadAllText(Path.Combine(destination, "newer.txt")) == "source newer" &&
                        File.ReadAllText(Path.Combine(destination, "older.txt")) == "destination newer" &&
                        File.ReadAllText(Path.Combine(destination, "equal.txt")) == "keep equal timestamp" &&
                        !File.Exists(Path.Combine(destination, "missing.txt")), "Update Existing applied the wrong timestamp or missing-file policy.");
                    dialog = await OpenUpdateAsync("Update All");
                    Click(dialog, "Apply copy plan"); await window.LastCommand;
                    Assert(File.ReadAllText(Path.Combine(destination, "missing.txt")) == "copy me" &&
                        File.ReadAllText(Path.Combine(destination, "new folder", "child.txt")) == "copy child" &&
                        File.ReadAllText(Path.Combine(destination, "new folder", "deeper", "leaf.txt")) == "copy leaf" &&
                        File.ReadAllText(Path.Combine(destination, "only.txt")) == "keep destination only" &&
                        !File.Exists(Path.Combine(destinationRoot, "unselected.txt")), "Update All did not preserve destination-only or unselected content.");

                    Put(source, "newer.txt", "source newest", stamp.AddHours(3));
                    dialog = await OpenUpdateAsync("Update Existing");
                    File.Move(Path.Combine(destination, "newer.txt"), Path.Combine(destination, "held.txt"));
                    Put(destination, "newer.txt", "source newer", stamp.AddHours(2));
                    Click(dialog, "Apply copy plan"); await window.LastCommand;
                    Assert(File.ReadAllText(Path.Combine(destination, "newer.txt")) == "source newer" &&
                        window.StatusMessage.Contains("reviewed file changed", StringComparison.OrdinalIgnoreCase),
                        "The update did not reject a same-content replacement made during review.");

                    Put(source, "add.txt", "new source file", stamp);
                    dialog = await OpenUpdateAsync("Update All");
                    var heldFolder = Path.Combine(destinationRoot, "Held folder");
                    Directory.Move(destination, heldFolder);
                    Directory.CreateDirectory(destination);
                    Put(destination, "sentinel.txt", "replacement folder", stamp);
                    Click(dialog, "Apply copy plan"); await window.LastCommand;
                    Assert(Directory.GetFileSystemEntries(destination).Select(Path.GetFileName).SequenceEqual(["sentinel.txt"]) &&
                        File.ReadAllText(Path.Combine(heldFolder, "only.txt")) == "keep destination only" &&
                        window.StatusMessage.Contains("reviewed destination folder changed", StringComparison.OrdinalIgnoreCase),
                        "The update entered a replacement destination folder.");
                }
                finally
                {
                    await window.ActivePane.NavigateAsync(left);
                    await window.OtherPane.NavigateAsync(right);
                }
            });
            await CheckAsync("Create Hard Links uses the same file identity and can undo", async () =>
            {
                var source = Path.Combine(left, "hard-link-source.bin");
                var linked = Path.Combine(right, "hard-link-source.bin");
                var menuItem = FindCommandMenuItem(window, "Create Hard Links");
                Assert(menuItem?.Parent is MenuItem { Header: "Create Advanced Links" },
                    "Hard links are not grouped under Create Advanced Links.");
                await File.WriteAllBytesAsync(source, [7, 8, 9]);
                await window.ActivePane.RefreshAsync();
                window.ActivePane.SelectPaths([source]);
                Click(window, "Create Hard Links"); await window.LastCommand;
                var nativeId = ListaryClient.ReadNativeItemIdentity(source);
                Assert(nativeId is not null && ListaryClient.ReadNativeItemIdentity(linked) == nativeId,
                    $"The destination does not share the native source identity. {window.StatusMessage}");
                Click(window, "Create Hard Links"); await window.LastCommand;
                Assert(ListaryClient.ReadNativeItemIdentity(linked) == nativeId, "A collision replaced the existing link.");
                Click(window, "Undo"); await window.LastCommand;
                Assert(!File.Exists(linked) && (await File.ReadAllBytesAsync(source)).SequenceEqual(new byte[] { 7, 8, 9 }),
                    "Undo did not remove only the linked destination.");
                window.ActivePane.SelectPaths([source]);
                Click(window, "Create Hard Links"); await window.LastCommand;
                Assert(File.Exists(linked), $"The link for the replacement check was not created. {window.StatusMessage}");
                var held = linked + ".held";
                File.Move(linked, held);
                await File.WriteAllBytesAsync(linked, [7, 8, 9]);
                File.SetLastWriteTimeUtc(linked, File.GetLastWriteTimeUtc(source));
                Click(window, "Undo"); await window.LastCommand;
                Assert(File.Exists(held) && File.Exists(linked) && ListaryClient.ReadNativeItemIdentity(linked) != nativeId,
                    "Undo removed or changed a same-content replacement.");
                Assert(window.StatusMessage.Contains("hard-link item changed", StringComparison.OrdinalIgnoreCase),
                    "Undo did not report the native identity mismatch.");
                File.Delete(linked);
                File.Delete(held);
                File.Delete(source);
                await window.ActivePane.RefreshAsync();
                await window.OtherPane.RefreshAsync();

                var reverse = Path.Combine(right, "hard-link-reverse.bin");
                var reverseLink = Path.Combine(left, "hard-link-reverse.bin");
                await File.WriteAllBytesAsync(reverse, [2, 4, 6]);
                window.Dispatch("switch-pane"); await window.LastCommand;
                try
                {
                    await window.ActivePane.RefreshAsync();
                    window.ActivePane.SelectPaths([reverse]);
                    Click(window, "Create Hard Links"); await window.LastCommand;
                    var reverseId = ListaryClient.ReadNativeItemIdentity(reverse);
                    Assert(reverseId is not null && ListaryClient.ReadNativeItemIdentity(reverseLink) == reverseId,
                        "The menu did not use the newly active source pane.");
                    Click(window, "Undo"); await window.LastCommand;
                    Assert(!File.Exists(reverseLink) && File.Exists(reverse), "Reverse-direction Undo changed the source.");
                    File.Delete(reverse);
                }
                finally { window.Dispatch("switch-pane"); await window.LastCommand; }
            });
            await CheckAsync("Folder tooltip shows its own size without changing selection", async () =>
            {
                var pane = window.ActivePane;
                var path = Path.Combine(left, "Design");
                var expected = new FileInfo(Path.Combine(path, "Layout.txt")).Length;
                var selected = Path.Combine(left, "Project notes.txt");
                pane.SelectPaths([selected]);
                var entry = pane.VisibleEntries.Single(item => item.FullPath == path);
                pane.FileGrid.ScrollIntoView(entry, pane.FileGrid.Columns.First());
                window.UpdateLayout();
                var cell = pane.FileGrid.GetVisualDescendants().OfType<ContentControl>()
                    .Single(control => Equals(control.Tag, path) && ToolTip.GetTip(control) is StackPanel);
                ToolTip.SetIsOpen(cell, true);
                var tooltip = (StackPanel)ToolTip.GetTip(cell)!;
                await WaitAsync(() => tooltip.Children.OfType<TextBlock>().Any(text =>
                    text.Text == $"Size: {FileEntry.FormatSize(expected)}"));
                ToolTip.SetIsOpen(cell, false);
                Assert(!ToolTip.GetIsOpen(cell) && pane.SelectedEntries.Single().FullPath == selected,
                    "The tooltip changed the current selection or remained open.");
            });
            await CheckAsync("Folder sizing follows junction preferences without counting loops", async () =>
            {
                var pane = window.ActivePane;
                var design = Path.Combine(left, "Design");
                var archive = Path.Combine(right, "Archive");
                var nested = Path.Combine(design, "External");
                var loop = Path.Combine(archive, "Loop");
                var linked = Path.Combine(left, "Linked");
                await pane.NavigateAsync(right);
                await File.WriteAllBytesAsync(Path.Combine(archive, "Extra.bin"), new byte[4096]);
                var original = new FileInfo(Path.Combine(design, "Layout.txt")).Length;
                var links = new List<string>();
                void CreateLink(string link, string target)
                {
                    if (OperatingSystem.IsWindows())
                    {
                        using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                        {
                            FileName = "cmd.exe", Arguments = $"/d /c mklink /J \"{link}\" \"{target}\"",
                            UseShellExecute = false, CreateNoWindow = true
                        }) ?? throw new InvalidOperationException("Could not start the Windows junction fixture.");
                        if (!process.WaitForExit(5000) || process.ExitCode != 0)
                            throw new InvalidOperationException("Could not create the Windows junction fixture.");
                    }
                    else Directory.CreateSymbolicLink(link, target);
                    links.Add(link);
                }
                try
                {
                    CreateLink(nested, archive);
                    CreateLink(loop, design);
                    CreateLink(linked, archive);
                    await pane.NavigateAsync(left);
                    long? Size(string path) => pane.VisibleEntries.Single(entry => entry.FullPath == path).CalculatedFolderLength;
                    async Task MeasureAsync(string path)
                    {
                        pane.SelectPaths([path]);
                        window.Dispatch("folder-sizes");
                        await window.LastCommand;
                    }
                    await MeasureAsync(design);
                    Assert(Size(design) == original, "The default calculation followed a nested junction.");
                    await MeasureAsync(linked);
                    Assert(Size(linked) == 4096, "Manual sizing refused a selected linked folder.");

                    window.Dispatch("folder-size-settings");
                    var dialog = await FindDialogAsync(window, "Folder Sizes");
                    dialog.GetVisualDescendants().OfType<ComboBox>()
                        .Single(box => box.Name == "AutomaticFolderSizeMode").SelectedIndex = (int)FolderSizeMode.All;
                    dialog.GetVisualDescendants().OfType<CheckBox>()
                        .Single(check => Equals(check.Content, "Ignore junctions below a folder when calculating its size")).IsChecked = false;
                    Click(dialog, "Apply");
                    await window.LastCommand;
                    await WaitAsync(() => Size(design) == original + 4096);
                    Assert(Size(linked) is null, "Automatic sizing ignored the linked-folder exclusion.");
                    window.Dispatch("folder-size-settings");
                    dialog = await FindDialogAsync(window, "Folder Sizes");
                    dialog.GetVisualDescendants().OfType<CheckBox>()
                        .Single(check => Equals(check.Content, "Skip automatic sizing of linked folders")).IsChecked = false;
                    Click(dialog, "Apply");
                    await window.LastCommand;
                    await WaitAsync(() => Size(linked) == original + 4096);
                }
                finally
                {
                    foreach (var link in links.AsEnumerable().Reverse()) Directory.Delete(link);
                }
            });
            await CheckAsync("Calculate Folder Sizes totals selected and visible folders", async () =>
            {
                var pane = window.ActivePane;
                var design = Path.Combine(left, "Design");
                var documents = Path.Combine(left, "Documents");
                await pane.NavigateAsync(right);
                var originalSize = new FileInfo(Path.Combine(design, "Layout.txt")).Length;
                var nested = Directory.CreateDirectory(Path.Combine(design, "Nested")).FullName;
                await File.WriteAllBytesAsync(Path.Combine(nested, ".hidden-size-fixture"), new byte[1024]);
                await File.WriteAllBytesAsync(Path.Combine(documents, "Larger.bin"), new byte[4096]);
                await pane.NavigateAsync(left);
                async Task CalculateAsync()
                {
                    pane.FocusList();
                    window.KeyPressQwerty(PhysicalKey.K, RawInputModifiers.Control);
                    window.KeyReleaseQwerty(PhysicalKey.K, RawInputModifiers.Control);
                    await window.LastCommand;
                }
                pane.SelectPaths([design]);
                await CalculateAsync();
                FileEntry Entry(string path) => pane.VisibleEntries.Single(entry => entry.FullPath == path);
                Assert(Entry(design).CalculatedFolderLength == originalSize + 1024 &&
                    Entry(design).SizeText == FileEntry.FormatSize(originalSize + 1024) &&
                    Entry(documents).CalculatedFolderLength is null && pane.SelectedEntries.Single().FullPath == design,
                    "Ctrl+K did not calculate only the selected folder and preserve its selection.");
                pane.SelectPaths([Path.Combine(left, "Project notes.txt")]);
                await CalculateAsync();
                Assert(Entry(documents).CalculatedFolderLength == 4096,
                    "With no folder selected, Ctrl+K did not calculate the visible folders.");
                pane.ApplySort("Size", descending: true);
                await pane.FilteringTask;
                Assert(pane.VisibleEntries.First(entry => entry.IsDirectory).FullPath == documents,
                    "Details sorting did not use calculated sizes.");

                async Task SetAutomaticModeAsync(FolderSizeMode mode)
                {
                    window.Dispatch("folder-size-settings");
                    var settings = await FindDialogAsync(window, "Folder Sizes");
                    settings.GetVisualDescendants().OfType<ComboBox>()
                        .Single(box => box.Name == "AutomaticFolderSizeMode").SelectedIndex = (int)mode;
                    Click(settings, "Apply");
                    await window.LastCommand;
                    Assert(new SettingsStore(state).Load().AutomaticFolderSizes == mode, "The automatic size mode did not persist.");
                }
                await pane.NavigateAsync(right);
                await pane.NavigateAsync(left);
                Assert(Entry(documents).CalculatedFolderLength is null, "The default setting calculated sizes on navigation.");
                await SetAutomaticModeAsync(FolderSizeMode.All);
                await pane.NavigateAsync(right);
                await pane.NavigateAsync(left);
                await WaitAsync(() => Entry(documents).CalculatedFolderLength == 4096 &&
                    Entry(design).CalculatedFolderLength == originalSize + 1024);
                if (OperatingSystem.IsWindows() && new DriveInfo(left).DriveType == DriveType.Fixed)
                {
                    await SetAutomaticModeAsync(FolderSizeMode.LocalAndRemovable);
                    await WaitAsync(() => Entry(documents).CalculatedFolderLength == 4096);
                }
                await SetAutomaticModeAsync(FolderSizeMode.Off);
                Assert(Entry(documents).CalculatedFolderLength is null, "Disabling automatic sizes left a stale displayed total.");
                async Task AddOverrideAsync(string target, bool enabled)
                {
                    window.Dispatch("folder-size-settings");
                    var dialog = await FindDialogAsync(window, "Folder Sizes");
                    dialog.GetVisualDescendants().OfType<TextBox>().Single(box => box.Name == "FolderSizeOverridePath").Text = target;
                    dialog.GetVisualDescendants().OfType<ComboBox>()
                        .Single(box => box.Name == "FolderSizeOverrideAction").SelectedIndex = enabled ? 0 : 1;
                    Click(dialog, "Add override");
                    Click(dialog, "Apply");
                    await window.LastCommand;
                }
                await AddOverrideAsync(Path.Combine(Path.GetDirectoryName(left)!, "W*"), enabled: true);
                await WaitAsync(() => Entry(documents).CalculatedFolderLength == 4096);
                await AddOverrideAsync(left, enabled: false);
                await SetAutomaticModeAsync(FolderSizeMode.All);
                var overrides = new SettingsStore(state).Load();
                Assert(overrides.FolderSizeOverrides.Count == 2 &&
                    overrides.ResolveFolderSizeMode(left) == FolderSizeMode.Off &&
                    overrides.ResolveFolderSizeMode(right) == FolderSizeMode.All &&
                    Entry(documents).CalculatedFolderLength is null,
                    "A saved exact override did not take precedence over the wildcard and global mode.");
                pane.SelectPaths([documents]);
                await CalculateAsync();
                Assert(Entry(documents).CalculatedFolderLength == 4096,
                    "An automatic override blocked the explicit Ctrl+K command.");

                window.Dispatch("folder-size-settings");
                var historySettings = await FindDialogAsync(window, "Folder Sizes");
                historySettings.GetVisualDescendants().OfType<CheckBox>()
                    .Single(check => Equals(check.Content, "Cache folder sizes when going back and forward in the history"))
                    .IsChecked = true;
                Click(historySettings, "Apply");
                await window.LastCommand;
                Assert(new SettingsStore(state).Load().CacheFolderSizesInHistory, "The size-history preference did not persist.");
                pane.SelectPaths([documents]);
                await CalculateAsync();
                await pane.NavigateAsync(right);
                await File.WriteAllBytesAsync(Path.Combine(documents, "Larger.bin"), new byte[8192]);
                await pane.HistoryAsync(-1);
                Assert(Entry(documents).CalculatedFolderLength == 4096,
                    "Back did not restore the cached folder size.");
                await pane.HistoryAsync(1);
                Assert(pane.CurrentDirectory == right, "Forward did not restore the other history location.");
                await pane.HistoryAsync(-1);
                Assert(Entry(documents).CalculatedFolderLength == 4096, "Back and Forward discarded the cache.");
                await pane.RefreshAsync();
                Assert(Entry(documents).CalculatedFolderLength is null, "Refresh reused the cached total.");
                pane.SelectPaths([documents]);
                await CalculateAsync();
                Assert(Entry(documents).CalculatedFolderLength == 8192, "Manual sizing did not read the new bytes.");
                if (OperatingSystem.IsWindows() && new DriveInfo(left).DriveType == DriveType.Fixed)
                {
                    window.Dispatch("folder-size-settings");
                    var updateSettings = await FindDialogAsync(window, "Folder Sizes");
                    updateSettings.GetVisualDescendants().OfType<CheckBox>()
                        .Single(check => Equals(check.Content, "Update folder sizes after file operations where possible"))
                        .IsChecked = true;
                    Click(updateSettings, "Apply");
                    await window.LastCommand;
                    Assert(new SettingsStore(state).Load().UpdateFolderSizesAfterOperations,
                        "The update-after-operations choice did not persist.");
                    pane.SelectPaths([documents]);
                    await CalculateAsync();
                    var moved = Path.Combine(left, "Project notes.txt");
                    var added = new FileInfo(moved).Length;
                    var result = await window.RunOperationAsync(new FileOperationRequest(FileOperationKind.Move, [moved], documents));
                    Assert(result.Succeeded, "The folder-size move fixture failed.");
                    await WaitAsync(() => Entry(documents).CalculatedFolderLength == 8192 + added);
                    window.Dispatch("undo");
                    await window.LastCommand;
                    Assert(window.StatusMessage.StartsWith("Undo completed", StringComparison.Ordinal), window.StatusMessage);
                    await WaitAsync(() => File.Exists(moved) && !File.Exists(Path.Combine(documents, "Project notes.txt")) &&
                        Entry(documents).CalculatedFolderLength == 8192);
                    Assert(Entry(design).CalculatedFolderLength is null,
                        "A file operation calculated an unmeasured folder while automatic sizing was excluded.");
                }
            });
            await CheckAsync("Reference shortcuts copy names and open properties", async () =>
            {
                var pane = window.ActivePane;
                pane.SelectPaths([Path.Combine(left, "Project notes.txt")]);
                pane.FocusList();
                var copyModifiers = RawInputModifiers.Control | RawInputModifiers.Shift;
                window.KeyPressQwerty(PhysicalKey.N, copyModifiers);
                window.KeyReleaseQwerty(PhysicalKey.N, copyModifiers);
                await window.LastCommand;
                Assert(await window.Clipboard!.TryGetTextAsync() == "Project notes.txt",
                    "Ctrl+Shift+N did not copy the selected name without its path.");
                window.KeyPressQwerty(PhysicalKey.C, copyModifiers);
                window.KeyReleaseQwerty(PhysicalKey.C, copyModifiers);
                await window.LastCommand;
                Assert(await window.Clipboard!.TryGetTextAsync() == Path.Combine(left, "Project notes.txt"),
                    "Ctrl+Shift+C did not publish the selected absolute path as text.");
                window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.Alt);
                window.KeyReleaseQwerty(PhysicalKey.Enter, RawInputModifiers.Alt);
                var properties = await FindDialogAsync(window, "Properties");
                Assert(properties.GetVisualDescendants().OfType<TextBox>().Any(text =>
                    text.Text?.Contains("Project notes.txt", StringComparison.Ordinal) == true),
                    "Alt+Enter did not open properties for the selected file.");
                Click(properties, "Close");
                await window.LastCommand;
                pane.FocusList();
                var duplicateRoot = Directory.CreateDirectory(Path.Combine(state, "shortcut duplicates")).FullName;
                await File.WriteAllTextAsync(Path.Combine(duplicateRoot, "original.txt"), "Matching fixture bytes");
                await File.WriteAllTextAsync(Path.Combine(duplicateRoot, "duplicate.txt"), "Matching fixture bytes");
                await File.WriteAllTextAsync(Path.Combine(duplicateRoot, "different.txt"), "Different fixture text");
                window.KeyPressQwerty(PhysicalKey.U, RawInputModifiers.Control);
                window.KeyReleaseQwerty(PhysicalKey.U, RawInputModifiers.Control);
                var scan = window;
                window.UpdateLayout();
                scan.GetVisualDescendants().OfType<TextBox>().Single(input => input.Name == "DuplicateScanFolder").Text = duplicateRoot;
                Click(scan, "Scan");
                await window.LastCommand;
                Assert(pane.IsSearchResults && pane.VisibleEntries.Count == 2 &&
                    scan.GetVisualDescendants().OfType<TextBlock>().Any(text => text.Name == "DuplicateScanFeedback" &&
                        text.Text?.StartsWith("1 duplicate groups", StringComparison.Ordinal) == true),
                    "Ctrl+U scan did not identify exactly the two identical files.");
                Click(window, "Close panel");
                await pane.NavigateAsync(left);
                pane.SelectPaths([Path.Combine(left, "Project notes.txt")]);
                pane.FocusList();
                window.KeyPressQwerty(PhysicalKey.I, RawInputModifiers.Control);
                window.KeyReleaseQwerty(PhysicalKey.I, RawInputModifiers.Control);
                await window.LastCommand;
                Assert(pane.SelectedEntries.Count == pane.VisibleEntries.Count - 1 &&
                    pane.SelectedEntries.All(entry => entry.Name != "Project notes.txt"),
                    "Ctrl+I did not invert the file selection.");
                var viewModifiers = RawInputModifiers.Control | RawInputModifiers.Shift;
                window.KeyPressQwerty(PhysicalKey.M, viewModifiers);
                window.KeyReleaseQwerty(PhysicalKey.M, viewModifiers);
                await window.LastCommand;
                Assert(pane.CurrentViewMode == "Tiles" && !pane.FileGrid.IsVisible, "Ctrl+Shift+M did not switch view.");
                pane.FocusList();
                window.KeyPressQwerty(PhysicalKey.H, viewModifiers);
                window.KeyReleaseQwerty(PhysicalKey.H, viewModifiers);
                await window.LastCommand;
                Assert(pane.CurrentViewMode == "Details" && pane.FileGrid.IsVisible &&
                    pane.VisibleColumnIds.First() == "Thumbnail" && pane.FileGrid.RowHeight == 96,
                    "Ctrl+Shift+H did not enable the leading Details thumbnail column.");
                var imagePath = Path.Combine(left, "Details thumbnail.png");
                using (var bitmap = new SKBitmap(48, 32))
                {
                    bitmap.Erase(SKColors.CornflowerBlue);
                    using var encoded = bitmap.Encode(SKEncodedImageFormat.Png, 100);
                    await File.WriteAllBytesAsync(imagePath, encoded.ToArray());
                }
                await pane.RefreshAsync();
                var imageEntry = pane.VisibleEntries.Single(entry => entry.FullPath == imagePath);
                pane.FileGrid.ScrollIntoView(imageEntry, pane.FileGrid.Columns.First());
                await WaitAsync(() => pane.FileGrid.GetVisualDescendants().OfType<FileThumbnail>()
                    .Any(control => control.Content is Image { Source: not null }));
                await window.SaveSettingsAsync();
                var detailsProfile = new SettingsStore(state).Load();
                Assert(detailsProfile.Panes[detailsProfile.ActivePane].Tabs[0].Columns.First() is
                    { Id: "Thumbnail", Visible: true, Width: 96 }, "The thumbnail column did not persist.");
                pane.SelectPaths([imagePath]);
                pane.FocusList();
                window.KeyPressQwerty(PhysicalKey.D, viewModifiers);
                window.KeyReleaseQwerty(PhysicalKey.D, viewModifiers);
                await window.LastCommand;
                Assert(pane.CurrentViewMode == "Details" && !pane.VisibleColumnIds.Contains("Thumbnail") &&
                    pane.FileGrid.RowHeight < 96 && pane.SelectedEntries.Single().FullPath == imagePath,
                    "Ctrl+Shift+D did not restore compact Details and retain selection.");
                var treeWasVisible = window.TreeVisible;
                window.KeyPressQwerty(PhysicalKey.F8, RawInputModifiers.None);
                window.KeyReleaseQwerty(PhysicalKey.F8, RawInputModifiers.None);
                await window.LastCommand;
                Assert(window.TreeVisible != treeWasVisible, "F8 did not toggle the tree.");
                pane.FocusList();
                window.KeyPressQwerty(PhysicalKey.S, RawInputModifiers.Control);
                window.KeyReleaseQwerty(PhysicalKey.S, RawInputModifiers.Control);
                var patternDialog = await FindDialogAsync(window, "Select by Pattern");
                var pattern = patternDialog.GetVisualDescendants().OfType<TextBox>()
                    .Single(control => control.Name == "SelectionPattern");
                pattern.Text = "*.(txt|csv)";
                Click(patternDialog, "Select");
                await window.LastCommand;
                var wildcardSelection = pane.SelectedEntries.Select(entry => entry.Name).OrderBy(name => name, StringComparer.Ordinal).ToArray();
                var wildcardActions = pane.ActionEntries.Select(entry => entry.Name).OrderBy(name => name, StringComparer.Ordinal).ToArray();
                var wildcardVisible = pane.VisibleEntries.Select(entry => entry.Name).OrderBy(name => name, StringComparer.Ordinal).ToArray();
                Assert(wildcardSelection.ToHashSet(StringComparer.Ordinal).SetEquals(new[] { "Project notes.txt", "Budget.csv" }),
                    $"Ctrl+S did not select the visible wildcard matches. selected=[{string.Join(" | ", wildcardSelection)}]; action=[{string.Join(" | ", wildcardActions)}]; visible=[{string.Join(" | ", wildcardVisible)}]; checkboxMode={pane.CheckboxMode}; searchResults={pane.IsSearchResults}; folder={pane.CurrentDirectory}; filter={pane.FilterText}");
                window.Dispatch("select-pattern");
                patternDialog = await FindDialogAsync(window, "Select by Pattern");
                patternDialog.GetVisualDescendants().OfType<ComboBox>()
                    .Single(control => control.Name == "PatternSelectionAction").SelectedIndex = 1;
                patternDialog.GetVisualDescendants().OfType<ComboBox>()
                    .Single(control => control.Name == "PatternSelectionKind").SelectedIndex = 2;
                patternDialog.GetVisualDescendants().OfType<TextBox>()
                    .Single(control => control.Name == "SelectionPattern").Text = "*";
                Click(patternDialog, "Select");
                await window.LastCommand;
                Assert(pane.SelectedEntries.Count == 2, "Deselecting folders changed the file selection.");
                window.Dispatch("select-pattern");
                patternDialog = await FindDialogAsync(window, "Select by Pattern");
                patternDialog.GetVisualDescendants().OfType<ComboBox>()
                    .Single(control => control.Name == "PatternSelectionAction").SelectedIndex = 1;
                patternDialog.GetVisualDescendants().OfType<TextBox>()
                    .Single(control => control.Name == "SelectionPattern").Text = "*.csv";
                Click(patternDialog, "Select");
                await window.LastCommand;
                Assert(pane.SelectedEntries.Single().Name == "Project notes.txt",
                    "Deselect matching did not retain the unmatched item.");
                await pane.NavigateAsync(left);
                pane.SelectPaths([Path.Combine(left, "Project notes.txt")]);
                pane.FocusList();
                var oldLarge = Path.Combine(left, "Old large.txt");
                var recentLarge = Path.Combine(left, "Recent large.txt");
                await File.WriteAllBytesAsync(oldLarge, new byte[2048]);
                await File.WriteAllBytesAsync(recentLarge, new byte[2048]);
                File.SetLastWriteTimeUtc(oldLarge, DateTime.UtcNow.AddDays(-10));
                await pane.RefreshAsync();
                window.Dispatch("select-pattern");
                patternDialog = await FindDialogAsync(window, "Select by Pattern");
                patternDialog.GetVisualDescendants().OfType<NumericUpDown>()
                    .Single(control => control.Name == "PatternSelectionMinimumKiB").Value = 1;
                patternDialog.GetVisualDescendants().OfType<NumericUpDown>()
                    .Single(control => control.Name == "PatternSelectionModifiedDays").Value = 2;
                Click(patternDialog, "Select");
                await window.LastCommand;
                Assert(pane.SelectedEntries.Single().FullPath == recentLarge,
                    "Metadata selection did not combine minimum file size with modification age.");
            });
            await CheckAsync("External clipboard rejects incomplete file batches before Paste", async () =>
            {
                // Only this diagnostic substitutes the intent reader. All
                // real application windows use the native Windows provider.
                var nativeIntentFixture = new ScriptedWindowsClipboardIntentReader();
                using var isolatedClipboardIntent = window.UseClipboardIntentReaderForDiagnostics(nativeIntentFixture);
                var first = Path.Combine(left, "Project notes.txt");
                var second = Path.Combine(left, "Research.md");
                var accepted = MainWindow.RequireCompleteClipboardPaths([first, second]);
                Assert(accepted.SequenceEqual([Path.GetFullPath(first), Path.GetFullPath(second)]),
                    "Valid external clipboard items did not retain their order.");
                var duplicateRejected = false;
                try { MainWindow.RequireCompleteClipboardPaths([first, Path.Combine(left, ".", "Project notes.txt")]); }
                catch (IOException error) when (error.Message.Contains("duplicate", StringComparison.OrdinalIgnoreCase))
                { duplicateRejected = true; }
                Assert(duplicateRejected, "Equivalent external clipboard paths could be pasted twice as one batch.");
                foreach (var incomplete in new string?[][] { [first, null], [null, second], [first, ""], [first, "   "] })
                {
                    var refused = false;
                    try { MainWindow.RequireCompleteClipboardPaths(incomplete); }
                    catch (IOException error) when (error.Message.Contains("No items were pasted", StringComparison.Ordinal))
                    { refused = true; }
                    Assert(refused, "An incomplete clipboard selection was silently reduced.");
                }
                Assert(!File.Exists(Path.Combine(right, "Project notes.txt")) &&
                    !File.Exists(Path.Combine(right, "Research.md")),
                    "The clipboard validation fixture unexpectedly changed the destination.");
                // Exercise the actual Paste command using external clipboard file
                // data rather than the app's own captured cut/copy transfer.
                var sourceFolder = Path.Combine(left, "Documents");
                await File.WriteAllTextAsync(Path.Combine(sourceFolder, "Clipboard nested.txt"), "nested copy fixture");
                var nestedRejected = false;
                try { MainWindow.RequireCompleteClipboardPaths([Path.Combine(sourceFolder, "Clipboard nested.txt"), sourceFolder]); }
                catch (IOException error) when (error.Message.Contains("overlap", StringComparison.OrdinalIgnoreCase))
                { nestedRejected = true; }
                Assert(nestedRejected, "A folder and one of its children were admitted as two independent Paste sources.");
                using (var overlappingTransfer = new DataTransfer())
                {
                    foreach (var path in new[] { sourceFolder, Path.Combine(sourceFolder, "Clipboard nested.txt") })
                    {
                        var item = Directory.Exists(path)
                            ? (IStorageItem?)await window.StorageProvider.TryGetFolderFromPathAsync(new Uri(path))
                            : await window.StorageProvider.TryGetFileFromPathAsync(new Uri(path));
                        Assert(item is not null, "The overlapping file fixture was unavailable.");
                        overlappingTransfer.Add(DataTransferItem.CreateFile(item!));
                    }
                    await window.Clipboard!.SetDataAsync(overlappingTransfer);
                    window.Dispatch("switch-pane"); await window.LastCommand;
                    var historyBeforeOverlap = window.OperationHistory.Count;
                    window.Dispatch("paste"); await window.LastCommand;
                    Assert(window.OperationHistory.Count == historyBeforeOverlap &&
                        !Directory.Exists(Path.Combine(right, "Documents")) &&
                        window.StatusMessage.Contains("overlapping", StringComparison.OrdinalIgnoreCase),
                        "The live Paste command accepted an overlapping folder/child batch.");
                    var dropRejected = false;
                    try
                    {
                        await window.TransferDroppedPathsAsync(window.ActivePane,
                            [sourceFolder, Path.Combine(sourceFolder, "Clipboard nested.txt")], move: true, toHome: false);
                    }
                    catch (IOException error) when (error.Message.Contains("overlapping", StringComparison.OrdinalIgnoreCase))
                    { dropRejected = true; }
                    Assert(dropRejected && window.OperationHistory.Count == historyBeforeOverlap &&
                        Directory.Exists(sourceFolder) && !Directory.Exists(Path.Combine(right, "Documents")),
                        "An overlapping drag/drop Move bypassed the shared selection guard.");
                    window.Dispatch("switch-pane"); await window.LastCommand;
                }
                using var transfer = new DataTransfer();
                foreach (var path in new[] { first, second, sourceFolder })
                {
                    var item = Directory.Exists(path)
                        ? (IStorageItem?)await window.StorageProvider.TryGetFolderFromPathAsync(new Uri(path))
                        : await window.StorageProvider.TryGetFileFromPathAsync(new Uri(path));
                    Assert(item is not null, "The external clipboard fixture could not resolve a generated file.");
                    transfer.Add(DataTransferItem.CreateFile(item!));
                }
                await window.Clipboard!.SetDataAsync(transfer);
                nativeIntentFixture.SetCopy([first, second, sourceFolder]);
                window.Dispatch("switch-pane"); await window.LastCommand;
                Assert(window.ActivePane.CurrentDirectory == right, "Paste fixture did not select its destination pane.");
                var baseline = window.OperationHistory.Count;
                var timer = Stopwatch.StartNew();
                window.Dispatch("paste"); await window.LastCommand;
                timer.Stop();
                Assert(window.OperationHistory.Count == baseline + 1,
                    $"External Paste did not use the operation journal. UI status: {window.StatusMessage}");
                Assert(await File.ReadAllTextAsync(Path.Combine(right, "Project notes.txt")) ==
                       await File.ReadAllTextAsync(first) &&
                    await File.ReadAllTextAsync(Path.Combine(right, "Research.md")) ==
                       await File.ReadAllTextAsync(second) &&
                    await File.ReadAllTextAsync(Path.Combine(right, "Documents", "Clipboard nested.txt")) ==
                       "nested copy fixture", "The external Paste batch lost a file or folder.");
                window.Dispatch("undo"); await window.LastCommand;
                Assert(!File.Exists(Path.Combine(right, "Project notes.txt")) &&
                    !File.Exists(Path.Combine(right, "Research.md")) &&
                    !Directory.Exists(Path.Combine(right, "Documents")) &&
                    File.Exists(first) && File.Exists(second) &&
                    File.Exists(Path.Combine(sourceFolder, "Clipboard nested.txt")),
                    "External Paste Undo did not restore the generated original and destination state.");
                var collisionTarget = Path.Combine(right, "Project notes.txt");
                await File.WriteAllTextAsync(collisionTarget, "keep existing target bytes");
                var beforeCanceledPaste = window.OperationHistory.Count;
                window.Dispatch("paste");
                var collision = await FindDialogAsync(window, "Existing files");
                Click(collision, "Cancel"); await window.LastCommand;
                Assert(window.OperationHistory.Count == beforeCanceledPaste &&
                    await File.ReadAllTextAsync(collisionTarget) == "keep existing target bytes",
                    "Canceled external Paste changed the target or operation journal.");
                window.Dispatch("paste");
                collision = await FindDialogAsync(window, "Existing files");
                await File.WriteAllTextAsync(collisionTarget, "a different destination revision after review started");
                collision.GetVisualDescendants().OfType<ComboBox>().Single().SelectedIndex = 2;
                Click(collision, "Continue"); await window.LastCommand;
                Assert(window.OperationHistory.Count == beforeCanceledPaste &&
                    await File.ReadAllTextAsync(collisionTarget) == "a different destination revision after review started" &&
                    window.StatusMessage.Contains("collision target changed", StringComparison.OrdinalIgnoreCase),
                    "Paste replaced a destination that changed during collision review.");
                await File.WriteAllTextAsync(collisionTarget, "keep existing target bytes");
                window.Dispatch("paste");
                collision = await FindDialogAsync(window, "Existing files");
                using (var laterClipboard = new DataTransfer())
                {
                    var laterFile = await window.StorageProvider.TryGetFileFromPathAsync(new Uri(second));
                    Assert(laterFile is not null, "The changed clipboard fixture file is unavailable.");
                    laterClipboard.Add(DataTransferItem.CreateFile(laterFile));
                    await window.Clipboard!.SetDataAsync(laterClipboard);
                    nativeIntentFixture.SetCopy([second]);
                    collision.GetVisualDescendants().OfType<ComboBox>().Single().SelectedIndex = 2;
                    Click(collision, "Continue"); await window.LastCommand;
                    Assert(window.OperationHistory.Count == beforeCanceledPaste &&
                        await File.ReadAllTextAsync(collisionTarget) == "keep existing target bytes" &&
                        window.StatusMessage.Contains("clipboard selection changed", StringComparison.OrdinalIgnoreCase),
                        "Paste used an obsolete external selection after the system clipboard changed.");
                }
                await window.Clipboard!.SetDataAsync(transfer);
                nativeIntentFixture.SetCopy([first, second, sourceFolder]);
                window.Dispatch("paste");
                collision = await FindDialogAsync(window, "Existing files");
                using (var textOnly = new DataTransfer())
                {
                    textOnly.Add(DataTransferItem.CreateText("clipboard was replaced with ordinary text"));
                    await window.Clipboard!.SetDataAsync(textOnly);
                    nativeIntentFixture.Clear();
                    collision.GetVisualDescendants().OfType<ComboBox>().Single().SelectedIndex = 2;
                    Click(collision, "Continue"); await window.LastCommand;
                    Assert(window.OperationHistory.Count == beforeCanceledPaste &&
                        await File.ReadAllTextAsync(collisionTarget) == "keep existing target bytes" &&
                        window.StatusMessage.Contains("clipboard selection changed", StringComparison.OrdinalIgnoreCase),
                        "Paste continued after a text-only clipboard replacement.");
                }
                await window.Clipboard!.SetDataAsync(transfer);
                nativeIntentFixture.SetCopy([first, second, sourceFolder]);
                window.Dispatch("paste");
                collision = await FindDialogAsync(window, "Existing files");
                var movedSource = first + ".before-replacement";
                File.Move(first, movedSource);
                await File.WriteAllTextAsync(first, "unapproved replacement");
                collision.GetVisualDescendants().OfType<ComboBox>().Single().SelectedIndex = 2;
                Click(collision, "Continue"); await window.LastCommand;
                Assert(window.OperationHistory.Count == beforeCanceledPaste &&
                    await File.ReadAllTextAsync(collisionTarget) == "keep existing target bytes" &&
                    window.StatusMessage.Contains("clipboard file changed", StringComparison.OrdinalIgnoreCase),
                    "A replaced source was accepted after external Paste collision review.");
                File.Delete(first); File.Move(movedSource, first);
                var originalSourceBytes = await File.ReadAllBytesAsync(first);
                var originalSourceTime = File.GetLastWriteTimeUtc(first);
                window.Dispatch("paste");
                collision = await FindDialogAsync(window, "Existing files");
                await File.WriteAllTextAsync(first, "edited in place while the Paste collision decision was open");
                File.SetLastWriteTimeUtc(first, originalSourceTime.AddSeconds(5));
                collision.GetVisualDescendants().OfType<ComboBox>().Single().SelectedIndex = 2;
                Click(collision, "Continue"); await window.LastCommand;
                Assert(window.OperationHistory.Count == beforeCanceledPaste &&
                    await File.ReadAllTextAsync(collisionTarget) == "keep existing target bytes" &&
                    window.StatusMessage.Contains("clipboard file changed", StringComparison.OrdinalIgnoreCase),
                    "An in-place modified clipboard source was accepted after collision review.");
                await File.WriteAllBytesAsync(first, originalSourceBytes);
                File.SetLastWriteTimeUtc(first, originalSourceTime);
                var beforeChangedDrop = window.OperationHistory.Count;
                var dropTask = window.TransferDroppedPathsAsync(window.ActivePane, [first], move: true, toHome: false);
                var dropCollision = await FindDialogAsync(window, "Existing files");
                await File.WriteAllTextAsync(first, "edited during the drag/drop Move collision review");
                File.SetLastWriteTimeUtc(first, originalSourceTime.AddSeconds(7));
                dropCollision.GetVisualDescendants().OfType<ComboBox>().Single().SelectedIndex = 2;
                Click(dropCollision, "Continue");
                var dropRefused = false;
                try { await dropTask; }
                catch (IOException error) when (error.Message.Contains("dropped item changed", StringComparison.OrdinalIgnoreCase))
                { dropRefused = true; }
                Assert(dropRefused && window.OperationHistory.Count == beforeChangedDrop &&
                    await File.ReadAllTextAsync(collisionTarget) == "keep existing target bytes" &&
                    File.Exists(first), "An in-place modified dragged source was moved after collision review.");
                await File.WriteAllBytesAsync(first, originalSourceBytes);
                File.SetLastWriteTimeUtc(first, originalSourceTime);
                File.Delete(collisionTarget);
                var cutSource = Path.Combine(left, "Clipboard cut – ünicode.txt");
                var cutTarget = Path.Combine(right, "Clipboard cut – ünicode.txt");
                await File.WriteAllTextAsync(cutSource, "keep the original Unicode cut source");
                await File.WriteAllTextAsync(cutTarget, "keep the existing Unicode destination");
                window.Dispatch("switch-pane"); await window.LastCommand;
                await window.ActivePane.RefreshAsync();
                window.ActivePane.SelectPaths([cutSource]);
                window.Dispatch("cut"); await window.LastCommand;
                window.Dispatch("switch-pane"); await window.LastCommand;
                var beforeCutDrift = window.OperationHistory.Count;
                window.Dispatch("paste");
                var cutCollision = await FindDialogAsync(window, "Existing files");
                using (var newerTransfer = new DataTransfer())
                {
                    newerTransfer.Add(DataTransferItem.CreateText("new selection replaces an in-app cut"));
                    await window.Clipboard!.SetDataAsync(newerTransfer);
                    cutCollision.GetVisualDescendants().OfType<ComboBox>().Single().SelectedIndex = 2;
                    Click(cutCollision, "Continue"); await window.LastCommand;
                    Assert(window.OperationHistory.Count == beforeCutDrift &&
                        await File.ReadAllTextAsync(cutSource) == "keep the original Unicode cut source" &&
                        await File.ReadAllTextAsync(cutTarget) == "keep the existing Unicode destination" &&
                        window.StatusMessage.Contains("clipboard selection changed", StringComparison.OrdinalIgnoreCase),
                        "In-app Cut moved a Unicode source after a newer clipboard selection replaced it.");
                }

                // External Windows Cut should reach the actual Paste handler as
                // Move, while a changed native Copy/Cut decision must be refused
                // even when Avalonia's private file list did not change.
                var downgradeSource = Path.Combine(left, "Explorer downgraded Cut – 東京.txt");
                var downgradeTarget = Path.Combine(right, Path.GetFileName(downgradeSource));
                await File.WriteAllTextAsync(downgradeSource, "retain the external Cut source");
                await File.WriteAllTextAsync(downgradeTarget, "retain the existing Cut destination");
                using (var downgradeTransfer = new DataTransfer())
                {
                    var downgradeItem = await window.StorageProvider.TryGetFileFromPathAsync(new Uri(downgradeSource));
                    Assert(downgradeItem is not null, "The Cut intent-downgrade fixture file was unavailable.");
                    downgradeTransfer.Add(DataTransferItem.CreateFile(downgradeItem!));
                    await window.Clipboard!.SetDataAsync(downgradeTransfer);
                    nativeIntentFixture.SetMove([downgradeSource]);
                    var beforeDowngrade = window.OperationHistory.Count;
                    window.Dispatch("paste");
                    var downgradeDialog = await FindDialogAsync(window, "Existing files");
                    nativeIntentFixture.SetCopy([downgradeSource]);
                    downgradeDialog.GetVisualDescendants().OfType<ComboBox>().Single().SelectedIndex = 2;
                    Click(downgradeDialog, "Continue"); await window.LastCommand;
                    Assert(window.OperationHistory.Count == beforeDowngrade &&
                           await File.ReadAllTextAsync(downgradeSource) == "retain the external Cut source" &&
                           await File.ReadAllTextAsync(downgradeTarget) == "retain the existing Cut destination" &&
                           window.StatusMessage.Contains("clipboard Copy/Cut intent", StringComparison.OrdinalIgnoreCase),
                        "An external Cut silently changed into Copy after the collision review started.");
                }

                var externalMoveSource = Path.Combine(left, "Explorer Cut – 東京.txt");
                var externalMoveTarget = Path.Combine(right, Path.GetFileName(externalMoveSource));
                var externalMoveBytes = Encoding.UTF8.GetBytes("external Windows Cut through actual Paste, Undo and cold reopen\n");
                await File.WriteAllBytesAsync(externalMoveSource, externalMoveBytes);
                using (var externalMoveTransfer = new DataTransfer())
                {
                    var moveItem = await window.StorageProvider.TryGetFileFromPathAsync(new Uri(externalMoveSource));
                    Assert(moveItem is not null, "The external Move fixture file was unavailable.");
                    externalMoveTransfer.Add(DataTransferItem.CreateFile(moveItem!));
                    await window.Clipboard!.SetDataAsync(externalMoveTransfer);
                    nativeIntentFixture.SetMove([externalMoveSource]);
                    var beforeExternalMove = window.OperationHistory.Count;
                    window.Dispatch("paste"); await window.LastCommand;
                    var moveReceipt = window.LastOperation
                        ?? throw new InvalidOperationException("External Cut Paste produced no operation receipt.");
                    Assert(moveReceipt.Succeeded && !moveReceipt.Canceled &&
                           window.OperationHistory.Count == beforeExternalMove + 1 &&
                           moveReceipt.Changes.Single().Action == "Move" &&
                           moveReceipt.Changes[0].Source == externalMoveSource &&
                           moveReceipt.Changes[0].Destination == externalMoveTarget &&
                           !File.Exists(externalMoveSource) &&
                           (await File.ReadAllBytesAsync(externalMoveTarget)).SequenceEqual(externalMoveBytes),
                        $"External Cut did not commit exactly one journaled Move: {window.StatusMessage}");

                    await window.CloseForDiagnosticsAsync();
                    window = new MainWindow(launch); window.Show(); await window.InitializeAsync();
                    Assert(window.OperationHistory.SingleOrDefault(item => item.Id == moveReceipt.Id) is
                           { CanUndo: true, Status: "Succeeded" },
                        "External Cut Move lost its durable Undo authority on cold reopen.");
                    window.Dispatch("undo"); await window.LastCommand;
                    Assert(window.LastOperation is { Succeeded: true, Canceled: false } &&
                           File.Exists(externalMoveSource) &&
                           (await File.ReadAllBytesAsync(externalMoveSource)).SequenceEqual(externalMoveBytes) &&
                           !File.Exists(externalMoveTarget) &&
                           window.OperationHistory.Single(item => item.Id == moveReceipt.Id) is
                               { CanUndo: false, Status: "Undone" },
                        "External Cut Move Undo after cold reopen did not restore original bytes and journal state.");
                    if (output is not null)
                        await File.WriteAllTextAsync(Path.Combine(output, "external-cut-cold-reopen-undo-receipt.json"),
                            JsonSerializer.Serialize(new
                            {
                                schema = "opusmac_external_cut_ui_cold_undo_v1",
                                move_id = moveReceipt.Id,
                                source_sha256 = Convert.ToHexString(SHA256.HashData(externalMoveBytes)),
                                moved_via = "PasteAsync Windows-intent scripted Move snapshot",
                                reopened_can_undo = true,
                                undo_status = "Undone",
                                source_restored = true,
                                destination_absent = true,
                                native_explorer_foreground_tested = false
                            }, new JsonSerializerOptions { WriteIndented = true }));
                }
                Console.WriteLine($"METRIC external_paste_three_items_ms={timer.Elapsed.TotalMilliseconds:F2}");
            });
            await CheckAsync("External multi-item Cut moves file and folder with cold-reopened Undo", async () =>
            {
                // A mixed Windows Explorer selection must be committed as one
                // operation. Its Undo must restore both the file and nested
                // folder after a process-style close/reopen, not just one item.
                var sourceFile = Path.Combine(left, "External Cut file – ünicode.txt");
                var sourceFolder = Path.Combine(left, "External Cut folder – 東京");
                var nestedFile = Path.Combine(sourceFolder, "nested – café.txt");
                var movedFile = Path.Combine(right, Path.GetFileName(sourceFile));
                var movedFolder = Path.Combine(right, Path.GetFileName(sourceFolder));
                var fileBytes = Encoding.UTF8.GetBytes("mixed external Cut source file\n");
                var nestedBytes = Encoding.UTF8.GetBytes("mixed external Cut nested folder content\n");
                Directory.CreateDirectory(sourceFolder);
                await File.WriteAllBytesAsync(sourceFile, fileBytes);
                await File.WriteAllBytesAsync(nestedFile, nestedBytes);
                var fixture = new ScriptedWindowsClipboardIntentReader();
                using (window.UseClipboardIntentReaderForDiagnostics(fixture))
                using (var transfer = new DataTransfer())
                {
                    var first = await window.StorageProvider.TryGetFileFromPathAsync(new Uri(sourceFile));
                    var folder = await window.StorageProvider.TryGetFolderFromPathAsync(new Uri(sourceFolder));
                    Assert(first is not null && folder is not null,
                        "The mixed external Cut fixture did not resolve its file and folder.");
                    transfer.Add(DataTransferItem.CreateFile(first!));
                    transfer.Add(DataTransferItem.CreateFile(folder!));
                    await window.Clipboard!.SetDataAsync(transfer);
                    fixture.SetMove([sourceFile, sourceFolder]);
                    await window.ActivePane.NavigateAsync(right);
                    await WaitAsync(() => !window.ActivePane.IsLoading);
                    var before = window.OperationHistory.Count;
                    window.Dispatch("paste"); await window.LastCommand;
                    var operation = window.LastOperation
                        ?? throw new InvalidOperationException("Mixed external Cut produced no journaled result.");
                    Assert(operation.Succeeded && !operation.Canceled &&
                           window.OperationHistory.Count == before + 1 &&
                           operation.Changes.Count == 2 &&
                           operation.Changes.All(change => change.Action == "Move") &&
                           operation.Changes.Select(change => change.Source).ToHashSet(StringComparer.Ordinal)
                               .SetEquals([sourceFile, sourceFolder]) &&
                           !File.Exists(sourceFile) && !Directory.Exists(sourceFolder) &&
                           (await File.ReadAllBytesAsync(movedFile)).SequenceEqual(fileBytes) &&
                           (await File.ReadAllBytesAsync(Path.Combine(movedFolder, Path.GetFileName(nestedFile)))).SequenceEqual(nestedBytes),
                        $"Mixed external Cut did not commit both selected paths exactly once: {window.StatusMessage}");

                    await window.CloseForDiagnosticsAsync();
                    window = new MainWindow(launch); window.Show(); await window.InitializeAsync();
                    Assert(window.OperationHistory.SingleOrDefault(item => item.Id == operation.Id) is
                           { CanUndo: true, Status: "Succeeded" },
                        "Mixed external Cut lost the exact journal's Undo authority after cold reopen.");
                    window.Dispatch("undo"); await window.LastCommand;
                    Assert(window.LastOperation is { Succeeded: true, Canceled: false } &&
                           (await File.ReadAllBytesAsync(sourceFile)).SequenceEqual(fileBytes) &&
                           (await File.ReadAllBytesAsync(nestedFile)).SequenceEqual(nestedBytes) &&
                           !File.Exists(movedFile) && !Directory.Exists(movedFolder) &&
                           window.OperationHistory.Single(item => item.Id == operation.Id) is
                               { CanUndo: false, Status: "Undone" },
                        "Mixed external Cut Undo did not restore the file, nested folder and journal together.");
                    await window.CloseForDiagnosticsAsync();
                    window = new MainWindow(launch); window.Show(); await window.InitializeAsync();
                    Assert(window.OperationHistory.SingleOrDefault(item => item.Id == operation.Id) is
                           { CanUndo: false, Status: "Undone" },
                        "The second cold reopen lost the mixed Cut's completed Undo state.");
                    if (output is not null)
                        await File.WriteAllTextAsync(Path.Combine(output, "external-mixed-cut-undo-receipt.json"),
                            JsonSerializer.Serialize(new
                            {
                                schema = "opusmac_external_mixed_cut_undo_v1",
                                operation_id = operation.Id,
                                move_count = operation.Changes.Count,
                                source_file_sha256 = Convert.ToHexString(SHA256.HashData(fileBytes)),
                                nested_file_sha256 = Convert.ToHexString(SHA256.HashData(nestedBytes)),
                                reopened_can_undo = true,
                                undo_status_after_second_reopen = "Undone",
                                native_explorer_foreground_tested = false
                            }, new JsonSerializerOptions { WriteIndented = true }));
                }
            });
            await CheckAsync("File watcher ignores retired-folder events after navigation", async () =>
            {
                var firstFolder = Directory.CreateDirectory(Path.Combine(root, "retired-watcher-folder")).FullName;
                var currentFolder = Directory.CreateDirectory(Path.Combine(root, "current-watcher-folder")).FullName;
                var pane = window.ActivePane;
                await pane.NavigateAsync(firstFolder);
                await WaitAsync(() => !pane.IsLoading);
                // Synchronously queue filesystem notifications while the UI
                // dispatcher is occupied, then switch folders before delivery.
                for (var index = 0; index < 8; index++)
                    File.WriteAllText(Path.Combine(firstFolder, $"old-{index}.txt"), "old watcher event");
                await pane.NavigateAsync(currentFolder);
                await WaitAsync(() => !pane.IsLoading);
                var revision = pane.ViewRevision.Navigation;
                await Task.Delay(1000);
                Assert(pane.ViewRevision.Navigation == revision &&
                       string.Equals(pane.CurrentDirectory, currentFolder, StringComparison.OrdinalIgnoreCase),
                    "A watcher event for the retired folder triggered an unnecessary refresh of the current folder.");

                var currentFile = Path.Combine(currentFolder, "watcher-live-ü.txt");
                await File.WriteAllTextAsync(currentFile, "current watcher event");
                await WaitAsync(() => pane.ViewRevision.Navigation > revision &&
                    pane.VisibleEntries.Any(entry => entry.FullPath == currentFile) && !pane.IsLoading);
                // A rejected navigation leaves the previous folder committed.
                // Its live watcher must still refresh new files afterward.
                var unavailable = Path.Combine(root, "missing-watcher-destination-" + Guid.NewGuid().ToString("N"));
                await pane.NavigateAsync(unavailable);
                Assert(!pane.LastNavigationSucceeded && pane.CurrentDirectory == currentFolder &&
                       window.StatusMessage.Contains("Could not read folder", StringComparison.Ordinal),
                    "A rejected destination replaced the committed folder or hid its read error.");
                var revisionAfterFailure = pane.ViewRevision.Navigation;
                var recoveryFile = Path.Combine(currentFolder, "watcher-after-failed-navigation.txt");
                await File.WriteAllTextAsync(recoveryFile, "live watcher recovery after failed navigation");
                await WaitAsync(() => pane.ViewRevision.Navigation > revisionAfterFailure &&
                    pane.VisibleEntries.Any(entry => entry.FullPath == recoveryFile) && !pane.IsLoading);
                // A burst of filesystem notifications should produce a bounded
                // number of refreshes and keep the pane usable, even after the
                // watcher was replaced during failed-navigation recovery.
                var burstBaseline = pane.ViewRevision.Navigation;
                var postedBaseline = pane.WatcherUiDispatches;
                for (var index = 0; index < 128; index++)
                    File.WriteAllText(Path.Combine(currentFolder, $"burst-{index:D3}.txt"), "watcher burst");
                await WaitAsync(() => pane.VisibleEntries.Count(entry => entry.Name.StartsWith("burst-", StringComparison.Ordinal)) == 128 && !pane.IsLoading);
                await Task.Delay(900);
                var burstRefreshes = pane.ViewRevision.Navigation - burstBaseline;
                var watcherPosts = pane.WatcherUiDispatches - postedBaseline;
                Assert(burstRefreshes >= 1 && burstRefreshes <= 2,
                    $"Filesystem burst scheduled {burstRefreshes} refreshes instead of coalescing notifications.");
                Assert(watcherPosts <= 8,
                    $"Filesystem burst scheduled {watcherPosts} UI dispatcher callbacks even though refreshes were debounced.");
                Console.WriteLine($"METRIC watcher_burst_files=128 watcher_burst_refreshes={burstRefreshes} watcher_ui_posts={watcherPosts}");
                // The pending-post gate must reopen after dispatch: a later
                // independent burst needs a fresh callback and refresh.
                var nextBurstRevision = pane.ViewRevision.Navigation;
                var nextBurstPosts = pane.WatcherUiDispatches;
                for (var index = 0; index < 64; index++)
                    File.WriteAllText(Path.Combine(currentFolder, $"second-burst-{index:D3}.txt"), "second burst");
                await WaitAsync(() => pane.VisibleEntries.Count(entry => entry.Name.StartsWith("second-burst-", StringComparison.Ordinal)) == 64 && !pane.IsLoading);
                await Task.Delay(900);
                var nextRefreshes = pane.ViewRevision.Navigation - nextBurstRevision;
                var nextPosts = pane.WatcherUiDispatches - nextBurstPosts;
                Assert(nextRefreshes >= 1 && nextRefreshes <= 2 && nextPosts >= 1 && nextPosts <= 8,
                    $"The watcher gate did not reopen for later changes: refreshes={nextRefreshes}, UI posts={nextPosts}.");
                Console.WriteLine($"METRIC watcher_second_burst_files=64 watcher_second_refreshes={nextRefreshes} watcher_second_ui_posts={nextPosts}");
            });
            await CheckAsync("Bounded directory identities keep collision review responsive", async () =>
            {
                var source = Path.Combine(left, "Bounded identity performance folder");
                var destination = Path.Combine(right, Path.GetFileName(source));
                Directory.CreateDirectory(source);
                Directory.CreateDirectory(destination);
                for (var index = 0; index < 256; index++)
                    Directory.CreateDirectory(Path.Combine(source, $"child-{index:D3}"));
                var held = Path.Combine(destination, "retained.txt");
                await File.WriteAllTextAsync(held, "retain on canceled review");
                await window.ActivePane.NavigateAsync(right);
                await WaitAsync(() => !window.ActivePane.IsLoading);
                var reader = new ScriptedWindowsClipboardIntentReader();
                using (window.UseClipboardIntentReaderForDiagnostics(reader))
                using (var transfer = new DataTransfer())
                {
                    var folder = await window.StorageProvider.TryGetFolderFromPathAsync(new Uri(source));
                    Assert(folder is not null, "The bounded directory fixture was unavailable.");
                    transfer.Add(DataTransferItem.CreateFile(folder!));
                    await window.Clipboard!.SetDataAsync(transfer);
                    reader.SetCopy([source]);
                    var priorHistory = window.OperationHistory.Count;
                    var clock = Stopwatch.StartNew();
                    window.Dispatch("paste");
                    var collision = await FindDialogAsync(window, "Existing files");
                    var reviewMs = clock.ElapsedMilliseconds;
                    Click(collision, "Cancel"); await window.LastCommand;
                    var totalMs = clock.ElapsedMilliseconds;
                    Assert(priorHistory == window.OperationHistory.Count &&
                           Directory.Exists(source) &&
                           await File.ReadAllTextAsync(held) == "retain on canceled review",
                        "The bounded directory review or cancellation modified a source/destination.");
                    Assert(reviewMs < 3000 && totalMs < 4000,
                        $"Bounded directory collision review stalled ({reviewMs}ms review, {totalMs}ms canceled).");
                    Console.WriteLine($"METRIC bounded_directory_256_collision_review_ms={reviewMs}");
                    Console.WriteLine($"METRIC bounded_directory_256_cancel_total_ms={totalMs}");
                }
            });
            await CheckAsync("External Cut refuses replaced nested directory during collision review", async () =>
            {
                var source = Path.Combine(left, "Reviewed folder identity - 東京");
                var child = Path.Combine(source, "Empty nested folder");
                var destination = Path.Combine(right, Path.GetFileName(source));
                var displaced = Path.Combine(root, "Displaced original nested folder");
                Directory.CreateDirectory(child);
                Directory.CreateDirectory(destination);
                var sentinel = Path.Combine(destination, "keep-destination.txt");
                await File.WriteAllTextAsync(sentinel, "do not replace after nested identity changes");
                var originalId = ListaryClient.ReadNativeItemIdentity(child);
                Assert(!string.IsNullOrWhiteSpace(originalId),
                    "The disposable nested directory lacks a native identity.");
                await window.ActivePane.NavigateAsync(right);
                await WaitAsync(() => !window.ActivePane.IsLoading);
                var intent = new ScriptedWindowsClipboardIntentReader();
                using (window.UseClipboardIntentReaderForDiagnostics(intent))
                using (var transfer = new DataTransfer())
                {
                    var folder = await window.StorageProvider.TryGetFolderFromPathAsync(new Uri(source));
                    Assert(folder is not null, "The nested directory replacement fixture is unavailable.");
                    transfer.Add(DataTransferItem.CreateFile(folder!));
                    await window.Clipboard!.SetDataAsync(transfer);
                    intent.SetMove([source]);
                    var priorHistory = window.OperationHistory.Count;
                    window.Dispatch("paste");
                    var collision = await FindDialogAsync(window, "Existing files");
                    Directory.Move(child, displaced);
                    Directory.CreateDirectory(child);
                    var replacementId = ListaryClient.ReadNativeItemIdentity(child);
                    Assert(!string.IsNullOrWhiteSpace(replacementId) && replacementId != originalId,
                        "The nested directory fixture did not change native identity.");
                    collision.GetVisualDescendants().OfType<ComboBox>().Single().SelectedIndex = 2;
                    Click(collision, "Continue"); await window.LastCommand;
                    Assert(window.OperationHistory.Count == priorHistory &&
                           Directory.Exists(source) && Directory.Exists(child) &&
                           Directory.Exists(displaced) &&
                           await File.ReadAllTextAsync(sentinel) == "do not replace after nested identity changes" &&
                           window.StatusMessage.Contains("clipboard file changed", StringComparison.OrdinalIgnoreCase),
                        "A reviewed external Cut accepted a same-path replacement of its nested directory.");
                }
            });
            await CheckAsync("External mixed Cut rejects one stale item without partially moving batch", async () =>
            {
                var file = Path.Combine(left, "Batch stale file – café.txt");
                var folder = Path.Combine(left, "Batch stale folder – 東京");
                var nested = Path.Combine(folder, "nested.txt");
                var destinationFile = Path.Combine(right, Path.GetFileName(file));
                var destinationFolder = Path.Combine(right, Path.GetFileName(folder));
                var originalFileBytes = Encoding.UTF8.GetBytes("file should stay at its original location");
                var originalNestedBytes = Encoding.UTF8.GetBytes("nested file before reviewed Cut");
                var changedNestedBytes = Enumerable.Repeat((byte)'X', originalNestedBytes.Length).ToArray();
                var heldTargetBytes = Encoding.UTF8.GetBytes("existing collision must remain unchanged");
                Directory.CreateDirectory(folder);
                await File.WriteAllBytesAsync(file, originalFileBytes);
                await File.WriteAllBytesAsync(nested, originalNestedBytes);
                await File.WriteAllBytesAsync(destinationFile, heldTargetBytes);
                await window.ActivePane.NavigateAsync(right);
                await WaitAsync(() => !window.ActivePane.IsLoading);
                var reader = new ScriptedWindowsClipboardIntentReader();
                using (window.UseClipboardIntentReaderForDiagnostics(reader))
                using (var transfer = new DataTransfer())
                {
                    var item = await window.StorageProvider.TryGetFileFromPathAsync(new Uri(file));
                    var directory = await window.StorageProvider.TryGetFolderFromPathAsync(new Uri(folder));
                    Assert(item is not null && directory is not null, "The mixed stale Cut fixture is unavailable.");
                    transfer.Add(DataTransferItem.CreateFile(item!));
                    transfer.Add(DataTransferItem.CreateFile(directory!));
                    await window.Clipboard!.SetDataAsync(transfer);
                    reader.SetMove([file, folder]);
                    var baseline = window.OperationHistory.Count;
                    window.Dispatch("paste");
                    var collision = await FindDialogAsync(window, "Existing files");
                    var originalTime = File.GetLastWriteTimeUtc(nested);
                    await File.WriteAllBytesAsync(nested, changedNestedBytes);
                    File.SetLastWriteTimeUtc(nested, originalTime);
                    collision.GetVisualDescendants().OfType<ComboBox>().Single().SelectedIndex = 2;
                    Click(collision, "Continue"); await window.LastCommand;
                    Assert(window.OperationHistory.Count == baseline &&
                           (await File.ReadAllBytesAsync(file)).SequenceEqual(originalFileBytes) &&
                           (await File.ReadAllBytesAsync(nested)).SequenceEqual(changedNestedBytes) &&
                           (await File.ReadAllBytesAsync(destinationFile)).SequenceEqual(heldTargetBytes) &&
                           !Directory.Exists(destinationFolder) &&
                           window.StatusMessage.Contains("clipboard file changed", StringComparison.OrdinalIgnoreCase),
                        "Mixed external Cut moved part of a batch after a nested file changed during review.");
                }
            });
            await CheckAsync("Bounded folder review covers shallow siblings before deep large subtrees", async () =>
            {
                var source = Path.Combine(left, "Sibling budget Cut - 東京");
                var destination = Path.Combine(right, Path.GetFileName(source));
                var small = Path.Combine(source, "A-small");
                var large = Path.Combine(source, "Z-large");
                Directory.CreateDirectory(small);
                Directory.CreateDirectory(large);
                Directory.CreateDirectory(destination);
                var sibling = Path.Combine(small, "shallow.txt");
                var original = Encoding.UTF8.GetBytes("shallow content before review");
                var updated = Enumerable.Repeat((byte)'Q', original.Length).ToArray();
                await File.WriteAllBytesAsync(sibling, original);
                for (var index = 0; index < 260; index++)
                    await File.WriteAllTextAsync(Path.Combine(large, $"nested-{index:D3}.txt"), "deep fixture");
                var sentinel = Path.Combine(destination, "keep.txt");
                await File.WriteAllTextAsync(sentinel, "existing destination");
                await window.ActivePane.NavigateAsync(right);
                await WaitAsync(() => !window.ActivePane.IsLoading);
                var intent = new ScriptedWindowsClipboardIntentReader();
                using (window.UseClipboardIntentReaderForDiagnostics(intent))
                using (var transfer = new DataTransfer())
                {
                    var folder = await window.StorageProvider.TryGetFolderFromPathAsync(new Uri(source));
                    Assert(folder is not null, "The shallow sibling fixture is unavailable.");
                    transfer.Add(DataTransferItem.CreateFile(folder!));
                    await window.Clipboard!.SetDataAsync(transfer);
                    intent.SetMove([source]);
                    var history = window.OperationHistory.Count;
                    window.Dispatch("paste");
                    var collision = await FindDialogAsync(window, "Existing files");
                    var stamp = File.GetLastWriteTimeUtc(sibling);
                    var identity = ListaryClient.ReadNativeItemIdentity(sibling);
                    await File.WriteAllBytesAsync(sibling, updated);
                    File.SetLastWriteTimeUtc(sibling, stamp);
                    Assert(File.GetLastWriteTimeUtc(sibling) == stamp &&
                           string.Equals(ListaryClient.ReadNativeItemIdentity(sibling), identity, StringComparison.Ordinal),
                        "The shallow sibling mutation did not retain its original file identity and timestamp.");
                    collision.GetVisualDescendants().OfType<ComboBox>().Single().SelectedIndex = 2;
                    Click(collision, "Continue"); await window.LastCommand;
                    Assert(window.OperationHistory.Count == history && Directory.Exists(source) &&
                           (await File.ReadAllBytesAsync(sibling)).SequenceEqual(updated) &&
                           await File.ReadAllTextAsync(sentinel) == "existing destination" &&
                           window.StatusMessage.Contains("clipboard file changed", StringComparison.OrdinalIgnoreCase),
                        "A deep subtree exhausted the inspection budget before a shallow sibling changed during review.");
                }
            });
            await CheckAsync("Oversized folder review detects changes inside its bounded snapshot", async () =>
            {
                var source = Path.Combine(left, "Oversized folder Cut – 東京");
                var destination = Path.Combine(right, Path.GetFileName(source));
                Directory.CreateDirectory(source);
                Directory.CreateDirectory(destination);
                var earlyFile = Path.Combine(source, "000-first-file.txt");
                var before = Encoding.UTF8.GetBytes("original oversized folder file");
                var changed = Enumerable.Repeat((byte)'R', before.Length).ToArray();
                await File.WriteAllBytesAsync(earlyFile, before);
                for (var i = 1; i <= 260; i++)
                    await File.WriteAllTextAsync(Path.Combine(source, $"{i:D3}-item.txt"), "fixture");
                var destinationSentinel = Path.Combine(destination, "retained.txt");
                var retained = Encoding.UTF8.GetBytes("destination survives stale oversized source");
                await File.WriteAllBytesAsync(destinationSentinel, retained);
                await window.ActivePane.NavigateAsync(right);
                await WaitAsync(() => !window.ActivePane.IsLoading);
                var clipboardIntent = new ScriptedWindowsClipboardIntentReader();
                using (window.UseClipboardIntentReaderForDiagnostics(clipboardIntent))
                using (var transfer = new DataTransfer())
                {
                    var folder = await window.StorageProvider.TryGetFolderFromPathAsync(new Uri(source));
                    Assert(folder is not null, "The oversized Cut fixture could not be selected.");
                    transfer.Add(DataTransferItem.CreateFile(folder!));
                    await window.Clipboard!.SetDataAsync(transfer);
                    clipboardIntent.SetMove([source]);
                    var previousHistory = window.OperationHistory.Count;
                    window.Dispatch("paste");
                    var collision = await FindDialogAsync(window, "Existing files");
                    var originalStamp = File.GetLastWriteTimeUtc(earlyFile);
                    var nativeId = ListaryClient.ReadNativeItemIdentity(earlyFile);
                    await File.WriteAllBytesAsync(earlyFile, changed);
                    File.SetLastWriteTimeUtc(earlyFile, originalStamp);
                    Assert(File.GetLastWriteTimeUtc(earlyFile) == originalStamp &&
                           string.Equals(ListaryClient.ReadNativeItemIdentity(earlyFile), nativeId, StringComparison.Ordinal),
                        "The same-metadata oversized folder fixture did not retain its native file identity.");
                    collision.GetVisualDescendants().OfType<ComboBox>().Single().SelectedIndex = 2;
                    Click(collision, "Continue"); await window.LastCommand;
                    Assert(window.OperationHistory.Count == previousHistory &&
                           Directory.Exists(source) &&
                           (await File.ReadAllBytesAsync(earlyFile)).SequenceEqual(changed) &&
                           (await File.ReadAllBytesAsync(destinationSentinel)).SequenceEqual(retained) &&
                           window.StatusMessage.Contains("clipboard file changed", StringComparison.OrdinalIgnoreCase),
                        "An oversized selected folder changed during review but was moved or replaced without revalidation.");
                }
            });
            await CheckAsync("Task-owned drag/drop Move preserves Unicode paths and cold-reopened Undo", async () =>
            {
                try
                {
                    var target = window.OtherPane;
                    await window.ActivePane.NavigateAsync(left);
                    await target.NavigateAsync(right);
                    var source = Path.Combine(left, "Drag drop Move – 東京.txt");
                    var destination = Path.Combine(right, Path.GetFileName(source));
                    var contents = Encoding.UTF8.GetBytes("task-owned drag/drop Move and Undo fixture\n");
                    await File.WriteAllBytesAsync(source, contents);
                    var sourceIdentity = ListaryClient.ReadNativeItemIdentity(source);
                    var destinationIdentity = ListaryClient.ReadNativeItemIdentity(right);
                    Assert(!string.IsNullOrWhiteSpace(sourceIdentity) && !string.IsNullOrWhiteSpace(destinationIdentity),
                        "The task-owned drop fixture did not expose native source and destination identities.");
                    var expectedHash = Convert.ToHexString(SHA256.HashData(contents));
                    var historyBefore = window.OperationHistory.Count;
                    var moveClock = Stopwatch.StartNew();
                    await window.TransferDroppedPathsAsync(target, [source], move: true, toHome: false);
                    moveClock.Stop();
                    var move = window.LastOperation
                        ?? throw new InvalidOperationException("The drop Move did not return an operation result.");
                    Assert(move.Succeeded && window.OperationHistory.Count == historyBefore + 1,
                        "The successful drop Move did not commit through the operation journal.");
                    Assert(!File.Exists(source) && File.Exists(destination),
                        "The drop Move did not move the source into the target pane.");
                    var destinationHash = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(destination)));
                    Assert(destinationHash == expectedHash, "The drop Move changed the source bytes.");
                    var movedIdentity = ListaryClient.ReadNativeItemIdentity(destination);
                    var undoClock = Stopwatch.StartNew();
                    window.Dispatch("undo"); await window.LastCommand;
                    undoClock.Stop();
                    var undo = window.LastOperation
                        ?? throw new InvalidOperationException("The drop Move Undo did not return an operation result.");
                    Assert(undo.Succeeded && File.Exists(source) && !File.Exists(destination),
                        "Undo did not restore the dropped source and remove the destination.");
                    var restoredHash = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(source)));
                    var restoredIdentity = ListaryClient.ReadNativeItemIdentity(source);
                    Assert(restoredHash == expectedHash && !string.IsNullOrWhiteSpace(restoredIdentity),
                        "Undo did not restore the exact source bytes and a readable native identity.");
                    // Exercise the same-content-version guard through the drop
                    // route, including an actual Replace collision dialog.
                    var collidingSource = Path.Combine(left, "Drop collision – 東京.txt");
                    var collidingDestination = Path.Combine(right, Path.GetFileName(collidingSource));
                    var collidingBytes = Encoding.UTF8.GetBytes("same-size drag source before the reviewed collision");
                    var retainedBytes = Encoding.UTF8.GetBytes("destination must survive a stale drop review");
                    await File.WriteAllBytesAsync(collidingSource, collidingBytes);
                    await File.WriteAllBytesAsync(collidingDestination, retainedBytes);
                    var collidingStamp = File.GetLastWriteTimeUtc(collidingSource);
                    var collidingIdentity = ListaryClient.ReadNativeItemIdentity(collidingSource);
                    var modifiedBytes = Enumerable.Repeat((byte)'X', collidingBytes.Length).ToArray();
                    var beforeStaleDrop = window.OperationHistory.Count;
                    var pendingStaleDrop = window.TransferDroppedPathsAsync(target, [collidingSource], move: true, toHome: false);
                    var staleDropDialog = await FindDialogAsync(window, "Existing files");
                    staleDropDialog.GetVisualDescendants().OfType<ComboBox>().Single().SelectedIndex = 2;
                    await File.WriteAllBytesAsync(collidingSource, modifiedBytes);
                    File.SetLastWriteTimeUtc(collidingSource, collidingStamp);
                    Assert(File.GetLastWriteTimeUtc(collidingSource) == collidingStamp &&
                           string.Equals(collidingIdentity, ListaryClient.ReadNativeItemIdentity(collidingSource), StringComparison.Ordinal),
                        "The stale drag/drop fixture did not keep its native identity and timestamp.");
                    Click(staleDropDialog, "Continue");
                    var staleDropRefused = false;
                    try { await pendingStaleDrop; }
                    catch (IOException error) when (error.Message.Contains("dropped item changed", StringComparison.OrdinalIgnoreCase))
                    { staleDropRefused = true; }
                    Assert(staleDropRefused && window.OperationHistory.Count == beforeStaleDrop &&
                           (await File.ReadAllBytesAsync(collidingDestination)).SequenceEqual(retainedBytes) &&
                           (await File.ReadAllBytesAsync(collidingSource)).SequenceEqual(modifiedBytes),
                        "A timestamp-restored, same-size drag edit moved or overwrote a destination after collision review.");
                    await window.CloseForDiagnosticsAsync();
                    window = new MainWindow(launch); window.Show(); await window.InitializeAsync();
                    var reopened = window.OperationHistory.SingleOrDefault(item => item.Id == move.Id)
                        ?? throw new InvalidOperationException("The moved drop operation was missing after reopening.");
                    Assert(reopened.Status == "Undone" && !reopened.CanUndo,
                        "The moved drop did not reopen with a durable Undone journal entry.");
                    if (output is not null)
                        await File.WriteAllTextAsync(Path.Combine(output, "drag-drop-move-undo-receipt.json"),
                            JsonSerializer.Serialize(new
                            {
                                schema = "opusmac_drag_drop_move_undo_v1",
                                source, destination,
                                source_sha256 = expectedHash,
                                destination_sha256 = destinationHash,
                                restored_sha256 = restoredHash,
                                source_native_identity_before = sourceIdentity,
                                moved_native_identity = movedIdentity,
                                source_native_identity_after_undo = restoredIdentity,
                                destination_directory_native_identity = destinationIdentity,
                                move_operation_id = move.Id,
                                undo_operation_id = undo.Id,
                                move_succeeded = move.Succeeded,
                                undo_succeeded = undo.Succeeded,
                                move_ms = moveClock.Elapsed.TotalMilliseconds,
                                undo_ms = undoClock.Elapsed.TotalMilliseconds,
                                source_restored = File.Exists(source),
                                destination_absent = !File.Exists(destination),
                                reopened_status = reopened.Status,
                                reopened_can_undo = reopened.CanUndo,
                                same_metadata_drop_refused = staleDropRefused,
                                test_mode = "headless Windows Avalonia TransferDroppedPathsAsync handler; native pointer and Explorer drag/drop unverified"
                            }, new JsonSerializerOptions { WriteIndented = true }));
                }
                catch
                {
                    preserveDiagnosticRoot = true;
                    throw;
                }
            });
            await CheckAsync("Indexed clipboard retains occurrence order and copies relocated file with Undo", async () =>
            {
                var viewerOrigin = window.Viewer.TranslatePoint(new Point(0, 0), window);
                Assert(viewerOrigin is not null && Math.Abs(viewerOrigin.Value.X - 1410) <= 12,
                    $"The preview begins at x={viewerOrigin?.X:F1}, outside the measured original Directory Opus 2176px layout tolerance (x≈1410).");
                var original = Path.Combine(left, "Indexed scene.txt");
                var relocated = Path.Combine(left, "Indexed scene renamed.txt");
                var destination = Path.Combine(right, "Indexed scene renamed.txt");
                const string contents = "indexed clipboard task-only fixture, immutable source bytes\n";
                await File.WriteAllTextAsync(original, contents);
                var initialEntry = DirectoryBrowser.ReadEntry(original);
                var identity = new ListarySearchResult(initialEntry, 77, Guid.NewGuid(),
                    "clipboard-identity-77", "file", "task-only-index", initialEntry.Length,
                    initialEntry.ModifiedUtc.ToUnixTimeMilliseconds());
                File.Move(original, relocated);
                var unaffected = Path.Combine(left, "Research.md");
                var lookupClock = Stopwatch.StartNew();
                var paths = await MainWindow.RevalidateClipboardSourcesAsync(
                    [original, unaffected], [identity, null],
                    _ => Task.FromResult(DirectoryBrowser.ReadEntry(relocated)));
                lookupClock.Stop();
                Assert(paths.SequenceEqual([relocated, unaffected]),
                    "A moved indexed file did not retain its clipboard slot or correct source path.");
                Assert(!File.Exists(original) && File.Exists(relocated), "The exact source rename was not observed.");
                var confirmedPathDriftRejected = false;
                try { MainWindow.RequireTransferSourcesUnchanged([original], [relocated], StringComparer.OrdinalIgnoreCase); }
                catch (IOException error) when (error.Message.Contains("moved or changed path", StringComparison.Ordinal))
                { confirmedPathDriftRejected = true; }
                Assert(confirmedPathDriftRejected, "A source that changed during authorization was not rejected.");

                var duplicateRejected = false;
                try
                {
                    await MainWindow.RevalidateClipboardSourcesAsync([original, original], [identity, identity],
                        _ => Task.FromResult(DirectoryBrowser.ReadEntry(relocated)));
                }
                catch (IOException error) when (error.Message.Contains("duplicate sources", StringComparison.Ordinal))
                { duplicateRejected = true; }
                Assert(duplicateRejected, "Duplicate revalidated clipboard identities were not rejected before mutation.");

                var mismatchRejected = false;
                try
                {
                    await MainWindow.RevalidateClipboardSourcesAsync([unaffected], [identity],
                        _ => Task.FromResult(DirectoryBrowser.ReadEntry(relocated)));
                }
                catch (IOException error) when (error.Message.Contains("does not match", StringComparison.Ordinal))
                { mismatchRejected = true; }
                Assert(mismatchRejected, "Indexed clipboard source mismatch did not fail closed.");

                await window.ActivePane.RefreshAsync();
                window.ActivePane.SelectPaths([paths[0]]);
                await WaitAsync(() => window.Viewer.DisplayedName == "Indexed scene renamed.txt"
                    && window.Viewer.PreviewStatus == "Text preview");
                await window.Viewer.LoadingTask;
                if (output is not null) SaveFrame(window, Path.Combine(output, "opusmac-relocated-indexed-source-2176x1190.png"));
                var operationCount = window.OperationHistory.Count;
                var copyClock = Stopwatch.StartNew();
                Click(window, "Copy Files"); await window.LastCommand;
                copyClock.Stop();
                Assert(window.OperationHistory.Count == operationCount + 1, "No operation was recorded by the existing journal.");
                Assert(File.Exists(destination) && await File.ReadAllTextAsync(destination) == contents,
                    "Copy from the current indexed path failed or changed bytes.");
                var expectedSha256 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(contents)));
                var destinationSha256 = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(destination)));
                Assert(destinationSha256 == expectedSha256, "The copied destination SHA-256 does not match the source.");
                Assert(!File.Exists(Path.Combine(right, "Indexed scene.txt")), "An obsolete indexed path was used as the destination filename.");
                var undoClock = Stopwatch.StartNew();
                Click(window, "Undo"); await window.LastCommand;
                undoClock.Stop();
                Assert(!File.Exists(destination) && await File.ReadAllTextAsync(relocated) == contents,
                    "Undo did not restore the unchanged task-owned source and remove the destination.");
                var originalSha256 = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(relocated)));
                Assert(originalSha256 == expectedSha256, "Undo changed the original source SHA-256.");
                if (output is not null)
                    await File.WriteAllTextAsync(Path.Combine(output, "indexed-clipboard-operation-receipt.json"),
                        JsonSerializer.Serialize(new {
                            schema = "opusmac_indexed_clipboard_remap_v1", source_before_move = original,
                            source_after_move = relocated, approved_destination = destination,
                            source_sha256 = originalSha256, copied_sha256 = destinationSha256,
                            original_reference_window = "2176x1190 at 96 DPI",
                            reference_viewer_left_x = 1410,
                            clone_viewer_left_x = viewerOrigin.GetValueOrDefault().X,
                            copy_ms = copyClock.Elapsed.TotalMilliseconds, undo_ms = undoClock.Elapsed.TotalMilliseconds,
                            revalidation_ms = lookupClock.Elapsed.TotalMilliseconds,
                            source_old_path_absent = !File.Exists(original), destination_removed_by_undo = !File.Exists(destination),
                            order_preserved = true, duplicate_sources_rejected = duplicateRejected,
                            mismatched_identity_rejected = mismatchRejected,
                            changed_during_confirmation_rejected = confirmedPathDriftRejected,
                            test_mode = "headless Windows Avalonia controls", mac_physical_verified = false
                        }, new JsonSerializerOptions { WriteIndented = true }));
            });
            await CheckAsync("Canceling the collision dialog preserves existing files", async () =>
            {
                var source = Path.Combine(left, "Project notes.txt"); var destination = Path.Combine(right, "Project notes.txt");
                await File.WriteAllTextAsync(destination, "Existing destination contents");
                await window.ActivePane.RefreshAsync(); window.ActivePane.SelectPaths([source]);
                Click(window, "Copy Files");
                var conflict = await FindDialogAsync(window, "Existing files");
                if (output is not null) SaveFrame(conflict, Path.Combine(output, "opusmac-conflict-dialog.png"), true);
                Click(conflict, "Cancel"); await window.LastCommand;
                Assert(await File.ReadAllTextAsync(destination) == "Existing destination contents", "Cancel modified the existing destination.");
                Assert(File.Exists(source), "Cancel removed the source.");
                File.Delete(destination); await window.OtherPane.RefreshAsync();
            });
            await CheckAsync("Changing the destination pane during confirmation fails closed", async () =>
            {
                var source = Path.Combine(left, "Research.md");
                var destination = Path.Combine(right, "Research.md");
                await File.WriteAllTextAsync(destination, "existing destination must survive");
                await window.OtherPane.NavigateAsync(right);
                await window.ActivePane.NavigateAsync(left);
                window.ActivePane.SelectPaths([source]);
                var historyBefore = window.OperationHistory.Count;
                Click(window, "Copy Files");
                var conflict = await FindDialogAsync(window, "Existing files");
                conflict.GetVisualDescendants().OfType<ComboBox>().Single().SelectedIndex = 2;
                await window.OtherPane.NavigateAsync(Path.Combine(right, "Archive"));
                Click(conflict, "Continue");
                await window.LastCommand;
                Assert(window.OperationHistory.Count == historyBefore, "A destination-pane drift entered the file-operation journal.");
                Assert(await File.ReadAllTextAsync(destination) == "existing destination must survive",
                    "Destination-pane drift changed destination contents.");
                Assert(window.StatusMessage.Contains("destination pane changed", StringComparison.OrdinalIgnoreCase),
                    "Destination-pane drift did not surface the authority failure.");
                await window.OtherPane.NavigateAsync(right);
                File.Delete(destination);
                await window.OtherPane.RefreshAsync();
            });
            await CheckAsync("F2 inline rename commits and undo restores the name", async () =>
            {
                window.ActivePane.SelectPaths([Path.Combine(left, "Research.md")]); window.ActivePane.FocusList();
                using (window.CaptureRenderedFrame()) { }
                Press(window, PhysicalKey.F2); await window.LastCommand;
                await WaitAsync(() => window.GetVisualDescendants().OfType<TextBox>().Any(box => box.Name == "InlineRename"));
                var editor = window.GetVisualDescendants().OfType<TextBox>().First(box => box.Name == "InlineRename");
                editor.Text = "Research renamed.md"; editor.Focus(); Press(window, PhysicalKey.Enter);
                await WaitAsync(() => File.Exists(Path.Combine(left, "Research renamed.md")));
                await WaitAsync(() => window.ActivePane.VisibleEntries.Any(entry => entry.Name == "Research renamed.md"));
                Click(window, "Undo"); await window.LastCommand;
                Assert(File.Exists(Path.Combine(left, "Research.md")) && !File.Exists(Path.Combine(left, "Research renamed.md")), "Rename undo did not restore the source.");
            });
            await CheckAsync("Text preview displays file content", async () =>
            {
                window.ActivePane.SelectPaths([Path.Combine(left, "Project notes.txt")]);
                await WaitAsync(() => window.Viewer.GetVisualDescendants().OfType<TextBox>().Any(box => box.Text?.Contains("Source and destination browsing.", StringComparison.Ordinal) == true));
            });
            await CheckAsync("Text preview and selection survive refresh", async () =>
            {
                await window.ActivePane.LoadingTask;
                window.ActivePane.SelectPaths([Path.Combine(left, "Project notes.txt")]);
                Assert(window.ActivePane.SelectedEntries.Any(entry => entry.Name == "Project notes.txt"), "The refresh fixture could not select its file before refresh.");
                await window.ActivePane.RefreshAsync();
                Assert(window.ActivePane.SelectedEntries.Any(entry => entry.Name == "Project notes.txt"), "Refresh lost the selected file.");
                await WaitAsync(() => window.Viewer.GetVisualDescendants().OfType<TextBox>().Any(box => box.Text?.Contains("Source and destination browsing.", StringComparison.Ordinal) == true));
            });
            await CheckAsync("Explicit single multi-file and folder selection persists across view modes", async () =>
            {
                var pane = window.ActivePane;
                await pane.NavigateAsync(left);
                await pane.LoadingTask;
                var first = Path.Combine(left, "Project notes.txt");
                var second = Path.Combine(left, "Research.md");
                var folder = Path.Combine(left, "Design");
                Assert(new[] { first, second, folder }.All(path => pane.VisibleEntries.Any(entry =>
                    string.Equals(entry.FullPath, path, StringComparison.OrdinalIgnoreCase))),
                    "The disposable file/folder selection fixtures are not visible.");
                var originalFileHash = SHA256.HashData(await File.ReadAllBytesAsync(first));
                void RequireSelection(string scenario, params string[] expected)
                {
                    var actual = pane.SelectedEntries.Select(entry => entry.FullPath)
                        .ToHashSet(StringComparer.OrdinalIgnoreCase);
                    Assert(actual.SetEquals(expected), scenario + " did not preserve the exact selected file/folder identities.");
                }
                pane.SelectPaths([first]);
                RequireSelection("Single file in Details", first);
                pane.SelectPaths([first, second]);
                RequireSelection("Two files in Details", first, second);
                pane.SelectPaths([first, second, folder]);
                RequireSelection("Mixed file/folder in Details", first, second, folder);

                var modes = pane.GetVisualDescendants().OfType<ComboBox>().Single(combo =>
                    combo.ItemsSource is string[] items && items.Contains("Details") && items.Contains("Tiles"));
                modes.SelectedItem = "Tiles";
                Assert(pane.CurrentViewMode == "Tiles", "Tile view selection did not take effect.");
                RequireSelection("Mixed file/folder after Details-to-Tiles switch", first, second, folder);
                pane.SelectPaths([folder]);
                RequireSelection("Folder only in Tiles", folder);
                pane.SelectPaths([first, second]);
                RequireSelection("Two files in Tiles", first, second);
                modes.SelectedItem = "Details";
                Assert(pane.CurrentViewMode == "Details", "Details view was not restored.");
                RequireSelection("Two files after Tiles-to-Details switch", first, second);
                pane.SelectPaths([folder]);
                RequireSelection("Folder only after view switch", folder);
                pane.SelectPaths([]);
                RequireSelection("Clear selection");
                Assert(SHA256.HashData(await File.ReadAllBytesAsync(first)).SequenceEqual(originalFileHash) &&
                       File.Exists(second) && Directory.Exists(folder),
                    "Selection or view transitions changed a source fixture.");
            });
            await CheckAsync("Portrait image decode is bounded and canceled previews stay current", async () =>
            {
                var picture = Path.Combine(root, "Portrait.png");
                using (var bitmap = new SKBitmap(64, 2048))
                {
                    using var canvas = new SKCanvas(bitmap); canvas.Clear(SKColors.LightSteelBlue);
                    using var paint = new SKPaint { Color = SKColors.DarkBlue };
                    canvas.DrawRect(8, 20, 48, 1500, paint);
                    using var encoded = bitmap.Encode(SKEncodedImageFormat.Png, 100);
                    await File.WriteAllBytesAsync(picture, encoded.ToArray());
                }
                using (var decoded = await PreviewFiles.ReadImageAsync(picture, 128, CancellationToken.None))
                    Assert(decoded.PixelSize.Width > 0 && decoded.PixelSize.Width <= 128 && decoded.PixelSize.Height <= 128, "Portrait decode exceeded the bounding dimensions.");
                window.Viewer.Show(DirectoryBrowser.ReadEntry(picture));
                window.Viewer.Show(DirectoryBrowser.ReadEntry(Path.Combine(left, "Project notes.txt")));
                await WaitAsync(() => window.Viewer.GetVisualDescendants().OfType<TextBox>().Any(box => box.Text?.Contains("Source and destination browsing.", StringComparison.Ordinal) == true));
                await Task.Delay(100);
                Assert(!window.Viewer.GetVisualDescendants().OfType<Image>().Any(), "An old image replaced the current text preview.");
            });
            await CheckAsync(flippedVideoCheck, async () =>
            {
                var fixture = Environment.GetEnvironmentVariable("OPUSMAC_VIDEO_PREVIEW_FIXTURE");
                Assert(!string.IsNullOrWhiteSpace(fixture) && File.Exists(fixture),
                    "A generated local video fixture is required for the explicit playback check.");
                var ffplay = VideoPreviewSession.FindPlayer();
                Assert(ffplay is not null, "FFplay is unavailable; no working video-preview backend was proven.");
                var video = Path.Combine(left, "Preview fixture with spaces.mp4");
                File.Copy(fixture!, video);
                var originalVideo = SHA256.HashData(await File.ReadAllBytesAsync(video));

                // Runs this precise FFplay backend with -vf hflip, seeks enabled, against
                // real H.264/AAC fixture bytes; shell sets SDL dummy output for safe CI.
                using (var playback = VideoPreviewSession.Start(ffplay!, video, true, CancellationToken.None))
                    Assert(await playback.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30)) == 0, "The mirrored FFplay decoder failed the actual fixture.");
                window.Viewer.Show(DirectoryBrowser.ReadEntry(video));
                await window.Viewer.LoadingTask;
                Assert(window.Viewer.HasVideoActions &&
                       window.Viewer.PreviewStatus.Contains("FFplay", StringComparison.Ordinal),
                    "The selected video still fell through to binary/hex text preview.");
                if (output is not null) SaveFrame(window, Path.Combine(output, "opusmac-video-flip-controls.png"));
                // An immediate Stop must be possible while Process.Start is still
                // pending, including when launching a player is slow under load.
                Click(window, "Flip & play");
                Assert(FindButton(window, "Stop playback")?.IsEnabled == true,
                    "Stop playback remained disabled while FFplay was starting.");
                Click(window, "Stop playback");
                await Task.Delay(150);
                Assert(!window.Viewer.HasActiveVideoPlayer &&
                       window.Viewer.PreviewStatus.Contains("stopped", StringComparison.Ordinal),
                    "Immediate Stop did not retain a stopped state after cancellation settled.");
                var videoStartup = Stopwatch.StartNew();
                Click(window, "Flip & play");
                await WaitAsync(() => window.Viewer.PreviewStatus.Contains("mirrored in FFplay", StringComparison.Ordinal) ||
                    window.Viewer.PreviewStatus.Contains("playback ended", StringComparison.Ordinal));
                var videoStartMs = videoStartup.ElapsedMilliseconds;
                if (window.Viewer.HasActiveVideoPlayer)
                {
                    var pauseClock = Stopwatch.StartNew();
                    Click(window, "Pause");
                    Assert(window.Viewer.IsVideoPaused && !window.Viewer.HasActiveVideoPlayer,
                        "Pause did not release the preview-owned FFplay child.");
                    var pausedAt = window.Viewer.VideoApproximateSeconds;
                    Click(window, "+5 s");
                    Assert(window.Viewer.IsVideoPaused &&
                           window.Viewer.VideoApproximateSeconds >= pausedAt + 4.9,
                        "Five-second seek did not change the paused timeline position.");
                    Click(window, "-5 s");
                    Assert(window.Viewer.VideoApproximateSeconds <= pausedAt + 0.15,
                        "Backward seek did not return to the previous approximate position.");
                    Click(window, "Resume");
                    await WaitAsync(() => window.Viewer.HasActiveVideoPlayer ||
                        window.Viewer.PreviewStatus.Contains("playback ended", StringComparison.Ordinal));
                    Console.WriteLine($"METRIC pause_seek_resume_ms={pauseClock.ElapsedMilliseconds}");
                }
                if (window.Viewer.HasActiveVideoPlayer)
                {
                    Click(window, "Stop playback");
                    Assert(!window.Viewer.HasActiveVideoPlayer, "Stop did not relinquish the owned FFplay process.");
                }
                Click(window, "Flip & play");
                await WaitAsync(() => window.Viewer.HasActiveVideoPlayer ||
                    window.Viewer.PreviewStatus.Contains("playback ended", StringComparison.Ordinal));
                var selectionRecovery = Stopwatch.StartNew();
                window.Viewer.Show(DirectoryBrowser.ReadEntry(Path.Combine(left, "Project notes.txt")));
                await window.Viewer.LoadingTask;
                await Task.Delay(120);
                var selectionMs = selectionRecovery.ElapsedMilliseconds;
                Assert(!window.Viewer.HasVideoActions && !window.Viewer.HasActiveVideoPlayer &&
                       window.Viewer.PreviewStatus == "Text preview",
                    "Changing selection did not dismiss the owned video playback and recover text preview.");
                Assert(SHA256.HashData(await File.ReadAllBytesAsync(video)).SequenceEqual(originalVideo),
                    "The playback changed source video bytes.");

                var imagePath = Path.Combine(left, "Preview mirror.png");
                using (var pixels = new SKBitmap(160, 90))
                {
                    using var canvas = new SKCanvas(pixels);
                    canvas.Clear(SKColors.DarkBlue);
                    using var paint = new SKPaint { Color = SKColors.Yellow };
                    canvas.DrawRect(0, 0, 48, 90, paint);
                    using var data = pixels.Encode(SKEncodedImageFormat.Png, 100);
                    await File.WriteAllBytesAsync(imagePath, data.ToArray());
                }
                var originalImage = SHA256.HashData(await File.ReadAllBytesAsync(imagePath));
                window.Viewer.Show(DirectoryBrowser.ReadEntry(imagePath));
                await window.Viewer.LoadingTask;
                Assert(!window.Viewer.IsImageFlipped, "The new image preview should begin unflipped.");
                var imageFlip = Stopwatch.StartNew();
                Click(window, "Flip image");
                Assert(window.Viewer.IsImageFlipped, "The displayed image did not mirror horizontally.");
                var imageFlipMs = imageFlip.ElapsedMilliseconds;
                if (output is not null) SaveFrame(window, Path.Combine(output, "opusmac-image-flipped.png"));
                Click(window, "Unflip image");
                Assert(!window.Viewer.IsImageFlipped, "Image flip did not reverse without rewriting the source.");
                Assert(SHA256.HashData(await File.ReadAllBytesAsync(imagePath)).SequenceEqual(originalImage),
                    "Flipping the displayed image changed its source bytes.");
                Console.WriteLine($"METRIC video_start_ui_ack_ms={videoStartMs} " +
                    $"selection_recovery_with_stop_ms={selectionMs} image_flip_ms={imageFlipMs}");
            });
            await CheckAsync("Large folder rapid filters keep the latest results and restore browsing", async () =>
            {
                const int total = 4096;
                var folder = Path.Combine(root, "Large filter fixture - 4096");
                Directory.CreateDirectory(folder);
                for (var index = 0; index < total; index++)
                {
                    var group = index < 2048 ? "alpha" : index < 3584 ? "beta" : "final";
                    File.WriteAllText(Path.Combine(folder, $"{group}-{index:D4}.txt"), "disposable catalog entry");
                }
                var pane = window.ActivePane;
                var stopwatch = Stopwatch.StartNew();
                await pane.NavigateAsync(folder);
                var coldLoadMs = stopwatch.ElapsedMilliseconds;
                Assert(!pane.IsLoading && pane.VisibleEntries.Count == total && !pane.IsSearchResults,
                    $"Large folder did not load exactly {total} real entries: {pane.VisibleEntries.Count}.");

                stopwatch.Restart();
                var filterRevisionBefore = pane.ViewRevision.Filter;
                pane.FilterText = "alpha";
                pane.FilterText = "beta";
                pane.FilterText = "final";
                await pane.FilteringTask;
                var latestFilterMs = stopwatch.ElapsedMilliseconds;
                var filterRequests = pane.ViewRevision.Filter - filterRevisionBefore;
                Assert(filterRequests == 3,
                    $"Three programmatic filter changes scheduled {filterRequests} filter jobs instead of three.");
                Assert(pane.FilterText == "final" && pane.VisibleEntries.Count == 512 &&
                       pane.VisibleEntries.All(entry => entry.Name.StartsWith("final-", StringComparison.Ordinal)),
                    "An older large-folder filter replaced the latest requested result set.");

                stopwatch.Restart();
                pane.FilterText = "";
                await pane.FilteringTask;
                var clearMs = stopwatch.ElapsedMilliseconds;
                Assert(pane.VisibleEntries.Count == total && !pane.IsLoading && !pane.IsSearchResults,
                    "Clearing a large-folder filter did not restore all real entries.");

                stopwatch.Restart();
                pane.ApplySort("Name", descending: false);
                await pane.FilteringTask;
                stopwatch.Restart();
                await pane.RefreshAsync();
                var warmRefreshMs = stopwatch.ElapsedMilliseconds;
                Assert(pane.VisibleEntries.Count == total && pane.FilterText.Length == 0,
                    "Refreshing a large folder lost entries or retained a stale filter.");
                Assert(pane.VisibleEntries[0].Name == "alpha-0000.txt" &&
                       pane.VisibleEntries[^1].Name == "final-4095.txt",
                    "An unfiltered folder failed to retain natural ascending Name order.");
                pane.ApplySort("Name", descending: true);
                await pane.FilteringTask;
                Assert(pane.VisibleEntries[0].Name == "final-4095.txt" &&
                       pane.VisibleEntries[^1].Name == "alpha-0000.txt",
                    "Descending Name sort was skipped after reusing the listing's default order.");
                pane.ApplySort("Name", descending: false);
                await pane.FilteringTask;
                Assert(pane.VisibleEntries[0].Name == "alpha-0000.txt" &&
                       pane.VisibleEntries[^1].Name == "final-4095.txt",
                    "Returning to ascending Name sort failed after custom sorting.");
                var tiles = pane.GetVisualDescendants().OfType<ListBox>()
                    .Single(control => control.Name == "FileTiles");
                Assert(!tiles.IsVisible && tiles.ItemsSource is null,
                    "The hidden Tiles lister still retains the complete large-folder data source.");
                var selectedPath = Path.Combine(folder, "final-3584.txt");
                pane.SelectPaths([selectedPath]);
                Assert(pane.SelectedEntries.Single().FullPath == selectedPath,
                    "The large-folder selection was unavailable before switching views.");
                pane.SetViewMode("Tiles");
                Assert(tiles.IsVisible && !pane.FileGrid.IsVisible &&
                       tiles.ItemsSource is IReadOnlyList<FileEntry> tileEntries && tileEntries.Count == total &&
                       pane.FileGrid.ItemsSource is null &&
                       pane.SelectedEntries.Single().FullPath == selectedPath,
                    "Switching to Tiles did not bind the current folder while releasing the hidden Details source.");
                pane.FilterText = "final";
                await pane.FilteringTask;
                Assert(tiles.ItemsSource is IReadOnlyList<FileEntry> matchingTiles && matchingTiles.Count == 512 &&
                       pane.VisibleEntries.Count == 512 && pane.SelectedEntries.Single().FullPath == selectedPath,
                    "Large-folder filtering in Tiles mode did not bind the latest visible items.");
                pane.SetViewMode("Details");
                Assert(pane.FileGrid.IsVisible && !tiles.IsVisible && tiles.ItemsSource is null &&
                       pane.VisibleEntries.Count == 512 && pane.FileGrid.ItemsSource is not null &&
                       pane.SelectedEntries.Single().FullPath == selectedPath,
                    "Returning to Details retained a hidden Tiles binding or lost the filtered folder.");
                pane.FilterText = "";
                await pane.FilteringTask;
                Assert(pane.VisibleEntries.Count == total,
                    "Changing from Tiles to Details lost entries after restoring the unfiltered folder.");
                Console.WriteLine($"METRIC large_folder_entries={total} cold_load_ms={coldLoadMs} latest_filter_ms={latestFilterMs} clear_ms={clearMs} warm_refresh_ms={warmRefreshMs} working_set_bytes={Environment.WorkingSet}");
            });
            await CheckAsync("Newer folder navigation cancels a large unfinished listing", async () =>
            {
                const int largeCount = 4096;
                var large = Directory.CreateDirectory(Path.Combine(root, "Overlapping navigation large folder")).FullName;
                var next = Directory.CreateDirectory(Path.Combine(root, "Overlapping navigation destination")).FullName;
                var latestFile = Path.Combine(next, "latest-navigation.txt");
                await File.WriteAllTextAsync(latestFile, "only the newest folder may remain visible");
                for (var index = 0; index < largeCount; index++)
                    File.WriteAllText(Path.Combine(large, $"scan-{index:D4}.txt"), "disposable listing");
                var pane = window.ActivePane;
                await pane.NavigateAsync(left);
                var first = pane.NavigateAsync(large);
                Assert(!first.IsCompleted, "The large-folder listing completed before the navigation race could be tested.");
                var latestTimer = Stopwatch.StartNew();
                var newest = pane.NavigateAsync(next);
                Assert(ReferenceEquals(pane.LoadingTask, newest),
                    "The pane did not retain the newest navigation task as its loading owner.");
                await Task.WhenAll(first, newest);
                var latestNavigationMs = latestTimer.ElapsedMilliseconds;
                var committedGeneration = pane.ViewRevision.Navigation;
                Assert(pane.LastNavigationSucceeded && !pane.IsLoading && !pane.IsSearchResults &&
                       string.Equals(pane.CurrentDirectory, next, StringComparison.OrdinalIgnoreCase) &&
                       pane.VisibleEntries.Count == 1 && pane.VisibleEntries[0].FullPath == latestFile,
                    "A canceled large-folder listing replaced the newer destination folder.");
                await Task.Delay(450);
                Assert(pane.ViewRevision.Navigation == committedGeneration &&
                       pane.VisibleEntries.Count == 1 && pane.VisibleEntries[0].FullPath == latestFile,
                    "An obsolete large-folder callback refreshed the newer destination after completion.");
                var reopenTimer = Stopwatch.StartNew();
                await pane.NavigateAsync(large);
                var reopenMs = reopenTimer.ElapsedMilliseconds;
                Assert(pane.LastNavigationSucceeded && !pane.IsLoading &&
                       pane.VisibleEntries.Count == largeCount && !pane.IsSearchResults,
                    "The large folder failed to load normally after its earlier listing was canceled.");
                pane.FilterText = "scan";
                var oldFilter = pane.FilteringTask;
                var filterRevision = pane.ViewRevision.Filter;
                var nextAfterFilter = pane.NavigateAsync(next);
                Assert(pane.ViewRevision.Filter > filterRevision,
                    "Starting another folder navigation left the old folder filter active while reading.");
                await Task.WhenAll(oldFilter, nextAfterFilter);
                Assert(pane.LastNavigationSucceeded && pane.VisibleEntries.Count == 1 &&
                       pane.VisibleEntries[0].FullPath == latestFile && pane.FilterText.Length == 0,
                    "The previous folder's filter leaked into the newer folder after navigation.");
                pane.FilterText = "latest";
                var searchFilter = pane.FilteringTask;
                var searchRevision = pane.ViewRevision.Filter;
                var searched = pane.VisibleEntries.Single();
                pane.ShowResults([searched], "Transient filtered search result");
                Assert(pane.ViewRevision.Filter > searchRevision,
                    "Publishing new search results left the old folder filter active.");
                await Task.WhenAll(searchFilter, pane.LoadingTask);
                Assert(pane.IsSearchResults && pane.VisibleEntries.Count == 1 &&
                       pane.VisibleEntries[0].FullPath == latestFile,
                    "An old folder filter displaced the newer search results.");
                await pane.NavigateAsync(large);
                pane.FilterText = "scan-000";
                var pendingBeforeFailure = pane.FilteringTask;
                var missing = Path.Combine(root, "unavailable-navigation-" + Guid.NewGuid().ToString("N"));
                await pane.NavigateAsync(missing);
                await pendingBeforeFailure;
                Assert(!pane.LastNavigationSucceeded && pane.CurrentDirectory == large &&
                       pane.FilterText == "scan-000" && pane.VisibleEntries.Count == 10 &&
                       pane.VisibleEntries.All(entry => entry.Name.StartsWith("scan-000", StringComparison.Ordinal)) &&
                       pane.VisibleEntries.All(entry => File.Exists(entry.FullPath)) &&
                       window.StatusMessage.Contains("Could not read folder", StringComparison.Ordinal),
                    "Failed navigation left a canceled filter showing mismatched files or lost the error status.");
                Console.WriteLine($"METRIC overlapping_navigation_entries={largeCount} latest_navigation_ms={latestNavigationMs} subsequent_large_reopen_ms={reopenMs}");
            });
            await CheckAsync("Recursive search finds real files and clear restores the folder", async () =>
            {
                var query = window.GetVisualDescendants().OfType<TextBox>().First(box => box.Name == "FileSearch");
                var scope = window.GetVisualDescendants().OfType<ComboBox>().First(box => box.Name == "SearchScope");
                scope.SelectedIndex = 1; query.Text = "Layout.txt"; Click(window, "Find"); await window.LastCommand;
                Assert(window.ActivePane.IsSearchResults && window.ActivePane.VisibleEntries.Any(entry => entry.FullPath == Path.Combine(left, "Design", "Layout.txt")), "Recursive search did not publish the nested file.");
                Click(window, "Clear"); await window.ActivePane.LoadingTask;
                Assert(!window.ActivePane.IsSearchResults && window.ActivePane.FilterText == "", "Clear did not restore the normal folder.");
            });
            if (rootFixture is not null)
            await CheckAsync(searchClearCheck, async () =>
            {
                var fixture = rootFixture ?? throw new InvalidOperationException("Missing isolated indexed search fixture.");
                fixture.SetRoots(new { enabled = true, availability = "available" });
                var query = window.GetVisualDescendants().OfType<TextBox>().Single(box => box.Name == "FileSearch");
                var scope = window.GetVisualDescendants().OfType<ComboBox>().Single(box => box.Name == "SearchScope");
                await window.ActivePane.NavigateAsync(left);
                scope.SelectedIndex = 2;
                query.Text = "Design";
                var seen = fixture.PauseNextSearch();
                Click(window, "Find");
                var pending = window.LastCommand;
                try
                {
                    await seen.WaitAsync(TimeSpan.FromSeconds(3));
                    Assert(!pending.IsCompleted, "The delayed indexed request completed before Clear could be tested.");
                    var clock = Stopwatch.StartNew();
                    Click(window, "Clear");
                    await WaitAsync(() => !window.ActivePane.IsSearchResults && !window.ActivePane.IsLoading &&
                        query.Text == "" && window.ActivePane.VisibleEntries.Any(entry => entry.Name == "Research.md"));
                    var recoveryMs = clock.ElapsedMilliseconds;
                    Assert(recoveryMs < 1200, $"Clear waited {recoveryMs}ms for a superseded indexed request.");
                    fixture.ReleasePausedSearch();
                    try { await pending.WaitAsync(TimeSpan.FromSeconds(3)); }
                    catch (OperationCanceledException) { }
                    Assert(!window.ActivePane.IsSearchResults && query.Text == "" &&
                           window.ActivePane.VisibleEntries.Any(entry => entry.Name == "Research.md"),
                        "A late indexed response replaced the folder after Clear.");
                    Console.WriteLine($"METRIC pending_index_clear_recovery_ms={recoveryMs}");
                }
                finally { fixture.ReleasePausedSearch(); }
            });
            if (rootFixture is not null)
            await CheckAsync(rootReadinessCheck, async () =>
            {
                var fixture = rootFixture ?? throw new InvalidOperationException(
                    "The isolated Listary protocol fixture was not activated.");
                Application.Current!.RequestedThemeVariant = ThemeVariant.Dark;
                var scope = window.GetVisualDescendants().OfType<ComboBox>().Single(box => box.Name == "SearchScope");
                var query = window.GetVisualDescendants().OfType<TextBox>().Single(box => box.Name == "FileSearch");
                var statuses = window.GetVisualDescendants().OfType<TextBlock>().ToArray();
                async Task SearchAndVerifyAsync(string expected)
                {
                    scope.SelectedIndex = 2;
                    query.Text = "No such generated filename";
                    Click(window, "Find"); await window.LastCommand;
                    Assert(window.ActivePane.IsSearchResults && window.ActivePane.VisibleEntries.Count == 0,
                        "Empty Listary results did not reach the actual indexed lister.");
                    Assert(statuses.Any(text => text.Text?.Contains(expected, StringComparison.OrdinalIgnoreCase) == true),
                        "Indexed lister did not render the expected root-health status: " + expected);
                }
                await SearchAndVerifyAsync("No indexed folders selected");
                if (output is not null)
                {
                    window.Width = 1080; window.Height = 847;
                    await Task.Delay(50);
                    SaveFrame(window, Path.Combine(output, "indexed-rootless-desktop-1080x847.png"));
                    window.Width = 1000; window.Height = 650;
                    await Task.Delay(50);
                    SaveFrame(window, Path.Combine(output, "indexed-rootless-compact-1000x650.png"));
                }
                fixture.SetRoots(new { enabled = false, availability = "available" });
                await SearchAndVerifyAsync("All indexed folders are disabled");
                fixture.SetRoots(new { enabled = true, availability = "unavailable" });
                await SearchAndVerifyAsync("Indexed folders are unavailable");
                fixture.SetRoots(new { enabled = true, availability = "available" });
                await SearchAndVerifyAsync("0 matches");
                Assert(!statuses.Any(text => text.IsVisible && text.Text?.Contains("No indexed folders selected", StringComparison.Ordinal) == true),
                    "Healthy indexed search retained an obsolete rootless warning.");
                query.Text = "Design";
                var initialSearchClock = Stopwatch.StartNew();
                Click(window, "Find"); await window.LastCommand;
                initialSearchClock.Stop();
                var indexedFolder = window.ActivePane.VisibleEntries.SingleOrDefault(
                    entry => entry.FullPath == Path.Combine(left, "Design"))
                    ?? throw new InvalidOperationException("Available indexed root did not populate the fixture folder.");
                Assert(window.ActivePane.IndexedIdentity(indexedFolder) is not null,
                    "Available indexed root did not publish a canonical folder identity.");
                await WaitAsync(() => statuses.Any(text => text.IsVisible &&
                    text.Text?.Contains("1 matches", StringComparison.Ordinal) == true));
                if (output is not null)
                {
                    await Task.Delay(60);
                    SaveFrame(window, Path.Combine(output, "indexed-positive-compact-1000x650.png"));
                }
                window.ActivePane.SelectPaths([indexedFolder.FullPath]);
                window.Dispatch("open"); await window.LastCommand;
                await WaitAsync(() => window.ActivePane.CurrentDirectory == indexedFolder.FullPath);
                Assert(File.Exists(Path.Combine(indexedFolder.FullPath, "Layout.txt")),
                    "Indexed navigation left the generated authorized fixture root.");
                await window.ActivePane.NavigateAsync(left);
                scope.SelectedIndex = 0; query.Text = "Research";
                Click(window, "Find"); await window.LastCommand;
                Assert(!window.ActivePane.IsSearchResults && window.ActivePane.VisibleEntries.Count == 1 &&
                    window.ActivePane.VisibleEntries[0].Name == "Research.md",
                    "Local folder search did not recover after an indexed root-health change.");
                Click(window, "Clear"); await window.ActivePane.LoadingTask;
                await window.CloseForDiagnosticsAsync();
                window = new MainWindow(launch);
                window.Show(); await window.InitializeAsync();
                var reopenedScope = window.GetVisualDescendants().OfType<ComboBox>().Single(box => box.Name == "SearchScope");
                var reopenedQuery = window.GetVisualDescendants().OfType<TextBox>().Single(box => box.Name == "FileSearch");
                reopenedScope.SelectedIndex = 2; reopenedQuery.Text = "Design";
                var reopenedSearchClock = Stopwatch.StartNew();
                Click(window, "Find"); await window.LastCommand;
                reopenedSearchClock.Stop();
                await WaitAsync(() => window.ActivePane.VisibleEntries.Any(entry =>
                    entry.FullPath == Path.Combine(left, "Design")));
                Assert(window.ActivePane.IndexedIdentity(window.ActivePane.VisibleEntries.Single(
                    entry => entry.FullPath == Path.Combine(left, "Design"))) is not null,
                    "Reopened lister lost the canonical Listary result identity.");
               var indexSource = Path.Combine(left, "Design");
               var indexDestination = Path.Combine(right, "Design");
                indexedSourcePath = indexSource;
                indexedDestinationPath = indexDestination;
                indexedSourceIdentityPath = Path.Combine(indexSource, "Layout.txt");
                indexedDestinationIdentityPath = Path.Combine(indexDestination, "Layout.txt");
                diagnosticPhase = "indexed-search-result-before-copy";
               var sourceHash = Convert.ToHexString(SHA256.HashData(
                   await File.ReadAllBytesAsync(Path.Combine(indexSource, "Layout.txt"))));
                indexedSourceHash = sourceHash;
               window.ActivePane.SelectPaths([indexSource]);
                indexedResultIdentity = window.ActivePane.IndexedIdentity(window.ActivePane.VisibleEntries.Single(entry =>
                    string.Equals(entry.FullPath, indexSource, StringComparison.OrdinalIgnoreCase)));
                diagnosticPhase = "indexed-copy-dispatch";
               var copyTimer = Stopwatch.StartNew();
               Click(window, "Copy Files"); await window.LastCommand;
                indexedOperationId = window.LastOperation?.Id;
                diagnosticPhase = "long-path-copy-committed";
               copyTimer.Stop();
                indexedOperationId = window.LastOperation?.Id ?? window.OperationHistory.FirstOrDefault()?.Id;
                diagnosticPhase = "indexed-copy-committed-before-cold-reopen";
                Assert(File.Exists(Path.Combine(indexDestination, "Layout.txt")) &&
                    Convert.ToHexString(SHA256.HashData(
                        await File.ReadAllBytesAsync(Path.Combine(indexDestination, "Layout.txt")))) == sourceHash,
                    "The selected indexed folder did not copy through the guarded operation owner.");
                var copyId = window.OperationHistory.First().Id;
                await window.CloseForDiagnosticsAsync();
               window = new MainWindow(launch);
               window.Show(); await window.InitializeAsync();
                diagnosticPhase = "indexed-journal-cold-reopen-before-undo";
               Assert(window.OperationHistory.Any(item => item.Id == copyId) && window.CanUndoOperation,
                    "Cold reopen lost the indexed selection's durable copy journal.");
               var undoTimer = Stopwatch.StartNew();
                diagnosticPhase = "indexed-undo-dispatch";
               Click(window, "Undo"); await window.LastCommand;
                diagnosticPhase = "long-path-copy-undo-result";
               undoTimer.Stop();
                diagnosticPhase = "indexed-undo-result-before-filesystem-assertion";
                var undoResponse = window.LastOperation;
               var destinationStillExists = Directory.Exists(indexDestination);
                var originalStillMatches = File.Exists(Path.Combine(indexSource, "Layout.txt")) &&
                    Convert.ToHexString(SHA256.HashData(
                        await File.ReadAllBytesAsync(Path.Combine(indexSource, "Layout.txt")))) == sourceHash;
                Console.WriteLine($"METRIC isolated_index_undo_status={window.StatusMessage} " +
                    $"destination_exists={destinationStillExists} original_matches={originalStillMatches} " +
                    $"history={string.Join(",", window.OperationHistory.Select(item => item.Status))}");
               if ((destinationStillExists || !originalStillMatches) && output is not null)
               {
                    preserveDiagnosticRoot = true;
                    // Retain bounded diagnostic custody before the per-run
                    // disposable filesystem is removed by the harness.
                    await File.WriteAllTextAsync(Path.Combine(output, "indexed-undo-failure-state.json"),
                        JsonSerializer.Serialize(new
                        {
                            operationId = copyId,
                            status = window.StatusMessage,
                            lastOperation = window.LastOperation,
                            undoResponse,
                            history = window.OperationHistory.Where(item => item.Id == copyId).ToArray(),
                            destinationDirectoryExists = destinationStillExists,
                            destinationFileExists = File.Exists(Path.Combine(indexDestination, "Layout.txt")),
                            sourceFileExists = File.Exists(Path.Combine(indexSource, "Layout.txt")),
                            sourceHashMatches = originalStillMatches,
                            sourceHashExpected = sourceHash,
                            utc = DateTimeOffset.UtcNow
                        }, new JsonSerializerOptions { WriteIndented = true }));
                }
                Assert(!destinationStillExists && originalStillMatches,
                    "Indexed-result cold-reopen Undo did not restore the exact generated original.");
                Console.WriteLine($"METRIC isolated_index_copy_ms={copyTimer.Elapsed.TotalMilliseconds:F2} " +
                    $"isolated_index_cold_undo_ms={undoTimer.Elapsed.TotalMilliseconds:F2} " +
                    $"first_search_ms={initialSearchClock.Elapsed.TotalMilliseconds:F2} " +
                    $"reopened_search_ms={reopenedSearchClock.Elapsed.TotalMilliseconds:F2} " +
                    $"process_workingset_bytes={Process.GetCurrentProcess().WorkingSet64}");
                // The indexed lister may expose a folder and a child from its
                // recursive catalog at the same time. This is a genuine UI
                // selection, and both entries have provider identities.
                var nestedIndexedFolder = Path.Combine(indexSource, "Nested");
                Directory.CreateDirectory(nestedIndexedFolder);
                await File.WriteAllTextAsync(Path.Combine(nestedIndexedFolder, "Child.txt"), "nested indexed item");
                fixture.SetIndexedDirectories([indexSource, nestedIndexedFolder]);
                var nextScope = window.GetVisualDescendants().OfType<ComboBox>().Single(box => box.Name == "SearchScope");
                var nextQuery = window.GetVisualDescendants().OfType<TextBox>().Single(box => box.Name == "FileSearch");
                nextScope.SelectedIndex = 2; nextQuery.Text = "Design";
                Click(window, "Find"); await window.LastCommand;
                Assert(window.ActivePane.VisibleEntries.Count == 2 &&
                    window.ActivePane.VisibleEntries.All(entry => window.ActivePane.IndexedIdentity(entry) is not null),
                    "The Listary fixture did not supply two independently identified nested results.");
                window.ActivePane.SelectPaths([indexSource, nestedIndexedFolder]);
                var beforeRefusal = window.OperationHistory.Count;
                Click(window, "Copy Files"); await window.LastCommand;
                Assert(window.OperationHistory.Count == beforeRefusal &&
                    !Directory.Exists(indexDestination) &&
                    File.Exists(Path.Combine(nestedIndexedFolder, "Child.txt")) &&
                    window.StatusMessage.Contains("overlapping", StringComparison.OrdinalIgnoreCase),
                    "Nested indexed-result Copy bypassed the shared overlap guard.");
            });
            await CheckAsync(indexedSizeCheck, async () =>
            {
                var fixture = rootFixture ?? throw new InvalidOperationException("Missing disposable index fixture.");
                var extra = Enumerable.Range(1, 200).Select(index =>
                    Directory.CreateDirectory(Path.Combine(right, $"Design {index:D3}")).FullName).ToArray();
                fixture.SetIndexedDirectories([Path.Combine(left, "Design"), ..extra]);
                fixture.SetRoots(new { enabled = true, availability = "available" });
                var pane = window.ActivePane;
                var scope = window.GetVisualDescendants().OfType<ComboBox>().Single(box => box.Name == "SearchScope");
                var query = window.GetVisualDescendants().OfType<TextBox>().Single(box => box.Name == "FileSearch");
                scope.SelectedIndex = 2;
                query.Text = "Design";
                Click(window, "Find");
                await window.LastCommand;
                var indexed = pane.VisibleEntries.First();
                Assert(pane.VisibleEntries.Count == 200 && FindButton(window, "Next")?.IsEnabled == true,
                    "The indexed fixture did not publish the first full page.");
                Assert(pane.IndexedIdentity(indexed) is not null, "The fixture did not produce a canonical indexed folder.");
                var originalSize = new FileInfo(Path.Combine(indexed.FullPath, "Layout.txt")).Length;
                pane.SelectPaths([indexed.FullPath]);
                pane.FocusList();
                window.KeyPressQwerty(PhysicalKey.K, RawInputModifiers.Control);
                window.KeyReleaseQwerty(PhysicalKey.K, RawInputModifiers.Control);
                await window.LastCommand;
                Assert(pane.IsSearchResults && pane.VisibleEntries.First().CalculatedFolderLength == originalSize &&
                    pane.IndexedIdentity(pane.VisibleEntries.First())?.ItemId == 23,
                    "Calculating sizes lost the indexed result identity or exact folder total.");
                Directory.Move(indexed.FullPath, Path.Combine(left, "Original Design"));
                Directory.CreateDirectory(indexed.FullPath);
                await File.WriteAllBytesAsync(Path.Combine(indexed.FullPath, "Replacement.bin"), new byte[2048]);
                window.Dispatch("folder-sizes");
                await window.LastCommand;
                Assert(pane.VisibleEntries.First().CalculatedFolderLength is null &&
                    window.StatusMessage.Contains("failed", StringComparison.Ordinal),
                    "An indexed row accepted the size of a same-path replacement.");
                Click(window, "Next");
                await WaitAsync(() => pane.VisibleEntries.Count == 1 && pane.VisibleEntries[0].FullPath == extra[^1]);
                Assert(pane.IndexedIdentity(pane.VisibleEntries.Single())?.ItemId == 223,
                    "The next page lost the provider occurrence identity after sizing.");
            });
            await CheckAsync("Indexed results preserve canonical provider occurrence order", async () =>
            {
                var pane = window.ActivePane;
                var entries = new[]
                {
                    DirectoryBrowser.ReadEntry(Path.Combine(left, "Research.md")),
                    DirectoryBrowser.ReadEntry(Path.Combine(left, "Budget.csv")),
                    DirectoryBrowser.ReadEntry(Path.Combine(left, "Project notes.txt"))
                };
                var indexed = entries.Select((entry, index) => new ListarySearchResult(
                    entry,
                    (uint)(index + 1),
                    Guid.Parse($"00000000-0000-4000-8000-{index + 1:000000000000}"),
                    $"diagnostic:{index + 1}",
                    "file",
                    "diagnostic-endpoint",
                    entry.Length,
                    entry.ModifiedUtc.ToUnixTimeMilliseconds())).ToArray();
                pane.ShowResults(entries, "Listary · occurrence order", indexed);
                await pane.LoadingTask;
                Assert(
                    pane.VisibleEntries.Select(entry => entry.FullPath).SequenceEqual(entries.Select(entry => entry.FullPath)),
                    "The indexed result pane reordered canonical Listary occurrences through the folder sort.");
                await pane.NavigateAsync(left);
            });
            await CheckAsync("A pending search cannot replace newer navigation or query", async () =>
            {
                var pane = window.ActivePane;
                var query = window.GetVisualDescendants().OfType<TextBox>().First(box => box.Name == "FileSearch");
                var scope = window.GetVisualDescendants().OfType<ComboBox>().First(box => box.Name == "SearchScope");
                scope.SelectedIndex = 1; query.Text = "*.txt"; Click(window, "Find"); var oldSearch = window.LastCommand;
                await pane.NavigateAsync(Path.Combine(left, "Design")); await oldSearch;
                Assert(!pane.IsSearchResults && pane.CurrentDirectory == Path.Combine(left, "Design"), "An old search overwrote newer navigation.");
                await pane.NavigateAsync(left);
                scope.SelectedIndex = 1; query.Text = "*.txt"; Click(window, "Find");
                scope.SelectedIndex = 0; query.Text = "Research"; Click(window, "Find"); await window.LastCommand;
                Assert(!pane.IsSearchResults && pane.VisibleEntries.Count == 1 && pane.VisibleEntries[0].Name == "Research.md", "An old search overwrote the newer local query.");
                Click(window, "Clear"); await pane.LoadingTask;
                pane.SelectPaths([Path.Combine(left, "Project notes.txt")]);
            });
            await CheckAsync("Advanced rename previews exact names and applies through the operation owner", async () =>
            {
                window.ActivePane.SelectPaths([Path.Combine(left, "Project notes.txt")]);
                Click(window, "Rename");
                var rename = await FindDialogAsync(window, "Advanced Rename");
                rename.GetVisualDescendants().OfType<TextBox>().First(box => box.Name == "RenamePrefix").Text = "Reviewed ";
                Click(rename, "Preview");
                await WaitAsync(() => FindButton(rename, "Apply rename")?.IsEnabled == true);
                if (output is not null) SaveFrame(rename, Path.Combine(output, "opusmac-rename-dialog.png"), true);
                Click(rename, "Apply rename"); await window.LastCommand;
                Assert(File.Exists(Path.Combine(left, "Reviewed Project notes.txt")), "The previewed rename was not applied.");
                Click(window, "Undo"); await window.LastCommand;
                Assert(File.Exists(Path.Combine(left, "Project notes.txt")), "Advanced rename undo failed.");
            });
            await CheckAsync("Local Advanced Rename rejects replaced preview sources and permits fresh selection", async () =>
            {
                Application.Current!.RequestedThemeVariant = ThemeVariant.Dark;
                window.Width = 1400; window.Height = 860;
                var first = Path.Combine(left, "rename-first.txt");
                var second = Path.Combine(left, "rename-second.txt");
                var held = Path.Combine(left, "held-original-rename-first.txt");
                var original = Encoding.UTF8.GetBytes("Selected native file before rename preview");
                var replacement = Encoding.UTF8.GetBytes(new string('R', original.Length));
                var secondary = Encoding.UTF8.GetBytes("Second selected rename source stays unchanged");
                await File.WriteAllBytesAsync(first, original);
                await File.WriteAllBytesAsync(second, secondary);
                await window.ActivePane.NavigateAsync(left);
                window.ActivePane.SelectPaths([first, second]);
                var historyBefore = window.OperationHistory.Count;
                Click(window, "Rename");
                var staleDialog = await FindDialogAsync(window, "Advanced Rename");
                staleDialog.GetVisualDescendants().OfType<TextBox>().First(box => box.Name == "RenamePrefix").Text = "Unreviewed ";
                Click(staleDialog, "Preview");
                await WaitAsync(() => FindButton(staleDialog, "Apply rename")?.IsEnabled == true);
                var stamp = File.GetLastWriteTimeUtc(first);
                File.Move(first, held);
                await File.WriteAllBytesAsync(first, replacement);
                File.SetLastWriteTimeUtc(first, stamp);
                Assert(new FileInfo(first).Length == original.Length && File.GetLastWriteTimeUtc(first) == stamp,
                    "The adversarial same-path replacement must match the preview's byte count and timestamp.");
                Click(staleDialog, "Apply rename");
                await WaitAsync(() => !staleDialog.IsVisible || staleDialog.GetVisualDescendants().OfType<TextBlock>()
                    .Any(block => block.Text?.Contains("changed since preview", StringComparison.OrdinalIgnoreCase) == true));
                Assert(!File.Exists(Path.Combine(left, "Unreviewed rename-first.txt")) &&
                       !File.Exists(Path.Combine(left, "Unreviewed rename-second.txt")) &&
                       window.OperationHistory.Count == historyBefore,
                    "A changed ordinary source was renamed after preview or the unreviewed batch partly committed.");
                Assert((await File.ReadAllBytesAsync(first)).SequenceEqual(replacement) &&
                       (await File.ReadAllBytesAsync(held)).SequenceEqual(original) &&
                       (await File.ReadAllBytesAsync(second)).SequenceEqual(secondary),
                    "The refused rename mutated a replacement, an original or the unaffected selected file.");
                Click(staleDialog, "Cancel");
                await window.ActivePane.RefreshAsync();
                window.ActivePane.SelectPaths([first, second]);
                Click(window, "Rename");
                var freshDialog = await FindDialogAsync(window, "Advanced Rename");
                freshDialog.GetVisualDescendants().OfType<TextBox>().First(box => box.Name == "RenamePrefix").Text = "Verified ";
                Click(freshDialog, "Preview");
                await WaitAsync(() => FindButton(freshDialog, "Apply rename")?.IsEnabled == true);
                Click(freshDialog, "Apply rename");
                var verifiedFirst = Path.Combine(left, "Verified rename-first.txt");
                var verifiedSecond = Path.Combine(left, "Verified rename-second.txt");
                await WaitAsync(() => !freshDialog.IsVisible && File.Exists(verifiedFirst) && File.Exists(verifiedSecond));
                await window.LastCommand;
                Assert((await File.ReadAllBytesAsync(verifiedFirst)).SequenceEqual(replacement) &&
                       (await File.ReadAllBytesAsync(verifiedSecond)).SequenceEqual(secondary),
                    "Freshly reviewed sources were not renamed with matching bytes.");
                Click(window, "Undo"); await window.LastCommand;
                Assert((await File.ReadAllBytesAsync(first)).SequenceEqual(replacement) &&
                       (await File.ReadAllBytesAsync(second)).SequenceEqual(secondary),
                    "Rename Undo did not restore the exact selected source bytes.");
                if (output is not null) SaveFrame(window, Path.Combine(output, "opusmac-local-rename-identity-undo.png"));
            });
            await CheckAsync("Status File Type Summary shows selected counts and visible group sizes", async () =>
            {
                var folder = Directory.CreateDirectory(Path.Combine(root, "Type summary")).FullName;
                var first = Path.Combine(folder, "one.JPG");
                var second = Path.Combine(folder, "two.jpeg");
                await File.WriteAllBytesAsync(first, new byte[10]);
                await File.WriteAllBytesAsync(second, new byte[20]);
                await File.WriteAllBytesAsync(Path.Combine(folder, "three.mp4"), new byte[40]);
                Directory.CreateDirectory(Path.Combine(folder, "directory.jpg"));
                var pane = window.ActivePane;
                var original = pane.CurrentDirectory;
                try
                {
                    await pane.NavigateAsync(folder);
                    pane.SelectPaths([first, second]);
                    var status = pane.GetVisualDescendants().OfType<Button>().Single(button => button.Name == "FileTypeSummaryStatus");
                    Assert(status.IsVisible && status.Content is StackPanel badges &&
                        badges.Children.OfType<Border>().Single().Child is TextBlock { Text: "jpg" } &&
                        AutomationProperties.GetName(status) == "File Type Summary 2 jpg" && ToolTip.GetTip(status) is Control,
                        "The status summary did not group selected JPEG variants.");
                    ClickNamedButton(window, "FileTypeSummaryStatus");
                    var summary = await FindDialogAsync(window, "File Type Summary");
                    Assert(summary.GetVisualDescendants().OfType<TextBlock>().Any(block => block.Text == ".jpg   2"),
                        "The dialog did not open with selected file counts.");
                    summary.GetVisualDescendants().OfType<RadioButton>().Single(button => button.Name == "FileTypeSummaryAll").IsChecked = true;
                    summary.GetVisualDescendants().OfType<RadioButton>().Single(button => button.Name == "FileTypeSummarySize").IsChecked = true;
                    summary.GetVisualDescendants().OfType<RadioButton>().Single(button => button.Name == "FileTypeSummaryGroups").IsChecked = true;
                    await WaitAsync(() => summary.GetVisualDescendants().OfType<TextBlock>().Any(block => block.Text == "Movies   40 B") &&
                        summary.GetVisualDescendants().OfType<TextBlock>().Any(block => block.Text == "Images   30 B"));
                    var pie = summary.GetVisualDescendants().OfType<Canvas>().Single(canvas => canvas.Name == "FileTypeSummaryPie");
                    Assert(pie.Children.Count == 2, "The grouped size chart did not draw both segments.");
                    if (output is not null) SaveFrame(summary, Path.Combine(output, "opusmac-file-type-summary.png"), true);
                    Click(summary, "Close"); await window.LastCommand;
                    var archive = Path.Combine(folder, "four.zip");
                    var binary = Path.Combine(folder, "five.bin");
                    await File.WriteAllBytesAsync(archive, new byte[2]);
                    await File.WriteAllBytesAsync(binary, new byte[3]);
                    await pane.RefreshAsync();
                    pane.SelectPaths([first, second, Path.Combine(folder, "three.mp4"), archive, binary]);
                    Assert(status.Content is StackPanel manyBadges &&
                        manyBadges.Children.OfType<TextBlock>().Any(text => text.Text?.EndsWith(" more", StringComparison.Ordinal) == true) &&
                        manyBadges.Children.OfType<Border>().Count() <= 3,
                        "The compact status did not aggregate file types beyond its badge limit.");
                    if (output is not null) SaveFrame(window, Path.Combine(output, "opusmac-file-type-badges.png"), true);
                    pane.SelectPaths([]);
                    Assert(!status.IsVisible, "The summary remained visible after clearing selection.");
                }
                finally { await pane.NavigateAsync(original); }
            });
            await CheckAsync("Split File creates numbered pieces that Join Files restores", async () =>
            {
                var pane = window.ActivePane;
                var original = pane.CurrentDirectory;
                var folder = Directory.CreateDirectory(Path.Combine(root, "Split source")).FullName;
                var outputFolder = Directory.CreateDirectory(Path.Combine(root, "Split pieces")).FullName;
                var collisionFolder = Directory.CreateDirectory(Path.Combine(root, "Split collision")).FullName;
                var input = Path.Combine(folder, "sample.bin");
                var bytes = Enumerable.Range(0, 13).Select(value => (byte)value).ToArray();
                await File.WriteAllBytesAsync(input, bytes);
                try
                {
                    await pane.NavigateAsync(folder);
                    async Task SplitIntoAsync(string destination, bool encode = false)
                    {
                        pane.SelectPaths([input]);
                        Click(window, "Split File…");
                        var split = await FindDialogAsync(window, "Split File");
                        split.GetVisualDescendants().OfType<TextBox>().Single(box => box.Name == "SplitFolder").Text = destination;
                        var chunkSize = split.GetVisualDescendants().OfType<TextBox>().Single(box => box.Name == "SplitChunkSize");
                        var presetButton = split.GetVisualDescendants().OfType<Button>().Single(button => button.Name == "SplitChunkPresets");
                        var menu = (MenuFlyout)presetButton.Flyout!;
                        var preset = menu.Items.OfType<MenuItem>().Single(item => Equals(item.Header, "698 MB (CDR)"));
                        preset.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                        Assert(chunkSize.Text == "698 MB (CDR)", "The Windows CD-size preset did not fill the editable chunk size.");
                        Click(split, "OK");
                        Assert(split.IsVisible && split.GetVisualDescendants().OfType<TextBlock>()
                            .Any(block => block.Text == "Choose a chunk size smaller than the source file."),
                            "Selecting an oversized preset did not keep the Split dialog open with validation.");
                        chunkSize.Text = "5 B";
                        split.GetVisualDescendants().OfType<CheckBox>().Single(box => box.Name == "SplitUuEncode").IsChecked = encode;
                        if (output is not null && encode) SaveFrame(split, Path.Combine(output, "opusmac-split-file.png"), true);
                        Click(split, "OK"); await window.LastCommand;
                    }
                    await SplitIntoAsync(outputFolder);
                    var parts = Directory.GetFiles(outputFolder).Order(StringComparer.Ordinal).ToArray();
                    Assert(parts.Select(Path.GetFileName).SequenceEqual(["sample.bin.001", "sample.bin.002", "sample.bin.003"]) &&
                        parts.Select(path => new FileInfo(path).Length).SequenceEqual([5L, 5L, 3L]),
                        "Split did not produce the expected sizes and numbered names.");

                    var sentinel = Path.Combine(collisionFolder, "sample.bin.002");
                    await File.WriteAllBytesAsync(sentinel, [99]);
                    await SplitIntoAsync(collisionFolder);
                    Assert(Directory.GetFiles(collisionFolder).SequenceEqual([sentinel]) &&
                        (await File.ReadAllBytesAsync(sentinel)).SequenceEqual(new byte[] { 99 }),
                        "A pre-existing piece was replaced or partial output was left.");

                    async Task JoinIntoAsync(string folder, string destination, bool decode = false)
                    {
                        await pane.NavigateAsync(folder);
                        pane.SelectPaths(Directory.GetFiles(folder).Reverse());
                        Click(window, "Join Files…");
                        var join = await FindDialogAsync(window, "Join Files");
                        join.GetVisualDescendants().OfType<TextBox>().Single(box => box.Name == "JoinOutputPath").Text = destination;
                        join.GetVisualDescendants().OfType<CheckBox>().Single(box => box.Name == "JoinUuDecode").IsChecked = decode;
                        Click(join, "OK"); await window.LastCommand;
                    }
                    var restored = Path.Combine(root, "split-restored.bin");
                    await JoinIntoAsync(outputFolder, restored);
                    Assert((await File.ReadAllBytesAsync(restored)).SequenceEqual(bytes) &&
                        (await File.ReadAllBytesAsync(input)).SequenceEqual(bytes), "The split and join did not preserve source bytes.");

                    var encodedFolder = Directory.CreateDirectory(Path.Combine(root, "UUencoded pieces")).FullName;
                    await pane.NavigateAsync(folder);
                    await SplitIntoAsync(encodedFolder, encode: true);
                    var encodedPiece = Path.Combine(encodedFolder, "sample.bin.001");
                    Assert(await File.ReadAllTextAsync(encodedPiece) == "begin 644 sample.bin.001\r\n%``$\"`P0`\r\n`\r\nend\r\n",
                        "The UUencoded piece differs from the independently encoded sample.");
                    var decoded = Path.Combine(root, "decoded.bin");
                    await JoinIntoAsync(encodedFolder, decoded, decode: true);
                    Assert((await File.ReadAllBytesAsync(decoded)).SequenceEqual(bytes), "UUencoded pieces did not round-trip.");

                    var external = Directory.CreateDirectory(Path.Combine(root, "External UUencoded pieces")).FullName;
                    var second = Path.Combine(external, "sample.002");
                    await File.WriteAllTextAsync(Path.Combine(external, "sample.001"), "begin 644 sample.bin\n#0V%T\n");
                    await File.WriteAllTextAsync(second, "#1&]G\n \nend");
                    var foreignOutput = Path.Combine(root, "external-decoded.bin");
                    await JoinIntoAsync(external, foreignOutput, decode: true);
                    Assert(await File.ReadAllTextAsync(foreignOutput) == "CatDog", "External UU data spanning two files did not decode.");
                    await File.WriteAllTextAsync(second, "#1&]G\n");
                    var rejected = Path.Combine(root, "malformed-decoded.bin");
                    await JoinIntoAsync(external, rejected, decode: true);
                    Assert(!File.Exists(rejected) && !Directory.EnumerateFiles(root, ".opusmac-join-*").Any(),
                        "A truncated UUencoded block left an output or a stage file.");
                }
                finally { await pane.NavigateAsync(original); }
            });
            await CheckAsync("Join Files follows the reviewed order and rejects replaced pieces", async () =>
            {
                var pane = window.ActivePane;
                var original = pane.CurrentDirectory;
                var source = Directory.CreateDirectory(Path.Combine(root, "Join parts")).FullName;
                var first = Path.Combine(source, "test.bin.002");
                var last = Path.Combine(source, "test.bin.010");
                await File.WriteAllBytesAsync(first, [1, 2]);
                await File.WriteAllBytesAsync(last, [3, 4, 5]);
                try
                {
                    await pane.NavigateAsync(source);
                    pane.SelectPaths([last, first]);
                    Click(window, "Join Files…");
                    var dialog = await FindDialogAsync(window, "Join Files");
                    var rows = dialog.GetVisualDescendants().OfType<ListBox>().Single(list => list.Name == "JoinFileParts");
                    IEnumerable<string> Paths() => rows.Items.Cast<MainWindow.JoinPartRow>().Select(row => row.Path);
                    Assert(Paths().SequenceEqual([first, last]) && rows.Items.Cast<MainWindow.JoinPartRow>().Select(row => row.Number).SequenceEqual([1, 2]),
                        "The join list did not sort and number the parts.");
                    rows.SelectedIndex = 1;
                    Click(dialog, "Move Up");
                    Assert(Paths().SequenceEqual([last, first]), "Move Up did not change the join order.");
                    Click(dialog, "Move Down");
                    dialog.UpdateLayout();
                    var from = rows.ContainerFromIndex(1) as ListBoxItem ?? throw new InvalidOperationException("The second part row is missing.");
                    var to = rows.ContainerFromIndex(0) as ListBoxItem ?? throw new InvalidOperationException("The first part row is missing.");
                    var origin = from.TranslatePoint(new Point(30, from.Bounds.Height / 2), dialog) ??
                        throw new InvalidOperationException("The source join row is not arranged.");
                    var end = to.TranslatePoint(new Point(30, to.Bounds.Height / 4), dialog) ??
                        throw new InvalidOperationException("The target join row is not arranged.");
                    dialog.MouseDown(origin, MouseButton.Left, RawInputModifiers.None);
                    dialog.MouseMove(end, RawInputModifiers.LeftMouseButton);
                    dialog.MouseUp(end, MouseButton.Left, RawInputModifiers.None);
                    Assert(Paths().SequenceEqual([last, first]) && rows.SelectedIndex == 0,
                        "Dragging the second piece to the first row did not reorder the reviewed list.");
                    var path = Path.Combine(root, "joined.bin");
                    dialog.GetVisualDescendants().OfType<TextBox>().Single(box => box.Name == "JoinOutputPath").Text = path;
                    if (output is not null) SaveFrame(dialog, Path.Combine(output, "opusmac-join-files.png"), true);
                    Click(dialog, "OK"); await window.LastCommand;
                    Assert((await File.ReadAllBytesAsync(path)).SequenceEqual(new byte[] { 3, 4, 5, 1, 2 }),
                        "The join output did not follow the reviewed list order.");
                    Assert((await File.ReadAllBytesAsync(first)).SequenceEqual(new byte[] { 1, 2 }) &&
                        (await File.ReadAllBytesAsync(last)).SequenceEqual(new byte[] { 3, 4, 5 }), "A source piece was modified.");

                    pane.SelectPaths([first, last]);
                    Click(window, "Join Files…");
                    dialog = await FindDialogAsync(window, "Join Files");
                    var rejected = Path.Combine(root, "stale-join.bin");
                    dialog.GetVisualDescendants().OfType<TextBox>().Single(box => box.Name == "JoinOutputPath").Text = rejected;
                    File.Move(first, first + ".old");
                    await File.WriteAllBytesAsync(first, [9, 9]);
                    Click(dialog, "OK"); await window.LastCommand;
                    Assert(!File.Exists(rejected), "A replacement piece entered the join.");
                    Assert(!Directory.EnumerateFiles(root, ".opusmac-join-*").Any(), "A temporary output was left behind.");
                }
                finally { await pane.NavigateAsync(original); }
            });
            await CheckAsync("Print Folder exports filtered CSV and grouped clipboard output", async () =>
            {
                var source = Directory.CreateDirectory(Path.Combine(root, "Listing source")).FullName;
                var child = Directory.CreateDirectory(Path.Combine(source, "Child")).FullName;
                await File.WriteAllTextAsync(Path.Combine(child, "Alpha, Ω.txt"), "abc");
                await File.WriteAllTextAsync(Path.Combine(source, "Skip.bin"), "skip");
                var target = Path.Combine(root, "Review.csv");
                async Task<Window> OpenListingAsync()
                {
                    Click(window, "Print / Export Folder Listing…");
                    var dialog = await FindDialogAsync(window, "Print Folder");
                    dialog.GetVisualDescendants().OfType<TextBox>().Single(box => box.Name == "ListingFolder").Text = source;
                    return dialog;
                }
                var listing = await OpenListingAsync();
                listing.GetVisualDescendants().OfType<CheckBox>().Single(box => box.Name == "ListingUseFilter").IsChecked = true;
                Click(listing, "Define…");
                var define = await FindDialogAsync(listing, "Define Filename Filter");
                define.GetVisualDescendants().OfType<TextBox>().Single(box => box.Name == "ListingFilterName").Text = "Listing text documents";
                define.GetVisualDescendants().OfType<TextBox>().Single(box => box.Name == "ListingFilterPattern").Text = "*.txt";
                Click(define, "Save");
                await WaitAsync(() => listing.GetVisualDescendants().OfType<TextBox>().Single(box => box.Name == "ListingFilter").Text == "*.txt");
                Click(listing, "Cancel"); await window.LastCommand;
                listing = await OpenListingAsync();
                var useFilter = listing.GetVisualDescendants().OfType<CheckBox>().Single(box => box.Name == "ListingUseFilter");
                var presets = listing.GetVisualDescendants().OfType<ComboBox>().Single(box => box.Name == "ListingSavedFilters");
                Assert(!presets.IsEffectivelyEnabled && presets.Items.Count == 1, "The filter switch or persisted preset was missing.");
                useFilter.IsChecked = true;
                presets.SelectedIndex = 0;
                listing.GetVisualDescendants().OfType<TextBox>().Single(box => box.Name == "ListingOutput").Text = target;
                listing.GetVisualDescendants().OfType<CheckBox>().Single(box => box.Name == "ListingUseFlatView").IsChecked = true;
                listing.GetVisualDescendants().OfType<ComboBox>().Single(box => box.Name == "ListingFlatView").SelectedIndex = 1;
                Click(listing, "Export"); await window.LastCommand;
                var csv = await File.ReadAllTextAsync(target);
                Assert(csv.StartsWith("Name,Size,Type,Modified,Attr\r\n") && csv.Contains("\"Alpha, Ω.txt\",3 B,") &&
                    !csv.Contains("Skip.bin") && csv.Count(character => character == '\n') == 2,
                    "The exported CSV did not preserve the filtered rows, quoting, or Unicode.");

                listing = await OpenListingAsync();
                listing.GetVisualDescendants().OfType<TextBox>().Single(box => box.Name == "ListingOutput").Text = target;
                Click(listing, "Export"); await window.LastCommand;
                Assert(await File.ReadAllTextAsync(target) == csv, "An existing export was overwritten.");

                listing = await OpenListingAsync();
                listing.GetVisualDescendants().OfType<CheckBox>().Single(box => box.Name == "ListingUseFlatView").IsChecked = true;
                listing.GetVisualDescendants().OfType<ComboBox>().Single(box => box.Name == "ListingFlatView").SelectedIndex = 2;
                listing.GetVisualDescendants().OfType<RadioButton>().Single(box => box.Name == "ListingClipboardDestination").IsChecked = true;
                listing.GetVisualDescendants().OfType<ComboBox>().Single(box => box.Name == "ListingFormat").SelectedIndex = 2;
                listing.GetVisualDescendants().OfType<CheckBox>().Single(box => Equals(box.Content, "Calculate sub-folder sizes")).IsChecked = true;
                if (output is not null) SaveFrame(listing, Path.Combine(output, "opusmac-print-folder.png"), true);
                Click(listing, "Export"); await window.LastCommand;
                var copied = await window.Clipboard!.TryGetTextAsync();
                Assert(copied is not null && copied.Contains("\r\nChild\t3 B\t") &&
                    copied.Contains("\r\n  Alpha, Ω.txt\t3 B\t") &&
                    copied.IndexOf("Child\t", StringComparison.Ordinal) < copied.IndexOf("Alpha, Ω.txt\t", StringComparison.Ordinal),
                    "Grouped tab-separated clipboard output did not retain the nested row.");

                listing = await OpenListingAsync();
                var preview = listing.GetVisualDescendants().OfType<DataGrid>().Single(grid => grid.Name == "ListingColumnPreview");
                preview.Columns.First(column => Equals(column.Header, "Name")).Width = new DataGridLength(224);
                listing.GetVisualDescendants().OfType<ComboBox>().Single(box => box.Name == "ListingHeaderFooter").SelectedIndex = 3;
                var textOutput = Path.Combine(root, "Review.txt");
                listing.GetVisualDescendants().OfType<TextBox>().Single(box => box.Name == "ListingOutput").Text = textOutput;
                Click(listing, "Export"); await window.LastCommand;
                var lines = await File.ReadAllLinesAsync(textOutput);
                Assert(lines.Length == 5 && lines[0].StartsWith(source) && lines[^1] == lines[0] &&
                    lines[1].IndexOf("Size", StringComparison.Ordinal) == 32,
                    "Plain-text export did not use the resized width or emit a matching header and footer.");
                listing = await OpenListingAsync();
                preview = listing.GetVisualDescendants().OfType<DataGrid>().Single(grid => grid.Name == "ListingColumnPreview");
                Assert(preview.Columns.First(column => Equals(column.Header, "Name")).Width.Value == 224,
                    "The last successful width did not reopen.");
                Click(listing, "Edit…");
                var editor = await FindDialogAsync(listing, "Print Folder Format");
                ClickNamedButton(editor, "FolderFormatDown_Name");
                var width = editor.GetVisualDescendants().OfType<TextBox>().Single(box => box.Name == "FolderFormatWidth_Size");
                width.Text = "invalid";
                await WaitAsync(() => FindButton(editor, "OK")?.IsEnabled == false);
                width.Text = "112";
                await WaitAsync(() => FindButton(editor, "OK")?.IsEnabled == true);
                editor.GetVisualDescendants().OfType<CheckBox>().Single(box => box.Name == "PrintFolderDescending").IsChecked = true;
                Click(editor, "OK");
                await WaitAsync(() => Equals(preview.Columns.First().Header, "Size"));
                await File.WriteAllTextAsync(Path.Combine(source, "Zulu.txt"), "z");
                var reordered = Path.Combine(root, "Reordered.csv");
                listing.GetVisualDescendants().OfType<TextBox>().Single(box => box.Name == "ListingOutput").Text = reordered;
                Click(listing, "Export"); await window.LastCommand;
                var editedLines = await File.ReadAllLinesAsync(reordered);
                Assert(editedLines[0].StartsWith("Size,Name,") && editedLines[2].StartsWith("1 B,Zulu.txt,") &&
                    editedLines[3].Contains(",Skip.bin,"), "The edited column order or descending name sort did not affect export.");
                var storedFormat = new SettingsStore(state).Load().PrintFolderFormat;
                Assert(storedFormat is { Descending: true } && storedFormat.Columns[0] is { Id: "Size", Width: 112 },
                    "The edited print format was not persisted to the isolated profile.");
            });
            await CheckAsync("ZIP create and extraction dialogs produce real matching contents", async () =>
            {
                window.ActivePane.SelectPaths([Path.Combine(left, "Project notes.txt")]);
                Click(window, "Archive");
                var prompt = await FindDialogAsync(window, "Create ZIP");
                var archive = Path.Combine(right, "Review.zip");
                prompt.GetVisualDescendants().OfType<TextBox>().First().Text = archive;
                Click(prompt, "Create ZIP"); await window.LastCommand;
                Assert(File.Exists(archive), "ZIP output is missing.");
                window.Dispatch("switch-pane"); await window.LastCommand;
                window.ActivePane.SelectPaths([archive]); window.Dispatch("extract");
                var zip = await FindDialogAsync(window, "ZIP — Review.zip");
                var extraction = Path.Combine(root, "Extracted");
                zip.GetVisualDescendants().OfType<TextBox>().First().Text = extraction;
                if (output is not null) SaveFrame(zip, Path.Combine(output, "opusmac-zip-dialog.png"), true);
                Click(zip, "Extract all");
                await WaitAsync(() => FindButton(zip, "Extract all")?.IsEnabled == true && File.Exists(Path.Combine(extraction, "Project notes.txt")));
                Assert(await File.ReadAllTextAsync(Path.Combine(extraction, "Project notes.txt")) == await File.ReadAllTextAsync(Path.Combine(left, "Project notes.txt")), "Extracted contents differ.");
                Click(zip, "Close"); await window.LastCommand;
                window.Dispatch("switch-pane"); await window.LastCommand;
            });
            await CheckAsync("Local ZIP extraction rejects replaced unindexed archive after listing", async () =>
            {
                Application.Current!.RequestedThemeVariant = ThemeVariant.Dark;
                window.Width = 1400; window.Height = 860;
                var archivePath = Path.Combine(left, "ordinary reviewed archive.zip");
                var heldOriginal = Path.Combine(root, "held-original-reviewed-archive.zip");
                var destination = Path.Combine(root, "unreviewed-archive-extraction");
                var originalPayload = Encoding.UTF8.GetBytes("Original archive payload stays reviewed.");
                var replacementPayload = Encoding.UTF8.GetBytes(new string('R', originalPayload.Length));
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
                var originalId = ListaryClient.ReadNativeItemIdentity(archivePath);
                await window.ActivePane.NavigateAsync(left);
                window.ActivePane.SelectPaths([archivePath]);
                Assert(window.ActivePane.SelectedEntries.Count == 1 &&
                       window.ActivePane.IndexedIdentity(window.ActivePane.SelectedEntries[0]) is null,
                    "The fixture must use the ordinary unindexed folder-selection route.");
                window.Dispatch("extract");
                var dialog = await FindDialogAsync(window, "ZIP — " + Path.GetFileName(archivePath));
                try
                {
                    dialog.GetVisualDescendants().OfType<TextBox>().First().Text = destination;
                    File.Move(archivePath, heldOriginal);
                    await WriteFixtureAsync(archivePath, replacementPayload);
                    File.SetLastWriteTimeUtc(archivePath, originalStamp);
                    var replacementArchiveBytes = await File.ReadAllBytesAsync(archivePath);
                    var replacementId = ListaryClient.ReadNativeItemIdentity(archivePath);
                    Assert(replacementArchiveBytes.Length == originalArchiveBytes.Length &&
                           File.GetLastWriteTimeUtc(archivePath) == originalStamp &&
                           !string.Equals(originalId, replacementId, StringComparison.Ordinal),
                        "The replacement must retain the reviewed size and timestamp but have a different native identity.");
                    Click(dialog, "Extract all");
                    await WaitAsync(() => FindButton(dialog, "Extract all")?.IsEnabled == true);
                    Assert(!Directory.Exists(destination),
                        "A same-path replacement ZIP was extracted after its contents were reviewed.");
                    Assert(File.Exists(archivePath) && (await File.ReadAllBytesAsync(archivePath)).SequenceEqual(replacementArchiveBytes),
                        "The replacement archive was mutated or removed after stale-review refusal.");
                    Assert(File.Exists(heldOriginal) && (await File.ReadAllBytesAsync(heldOriginal)).SequenceEqual(originalArchiveBytes),
                        "The original reviewed archive was not preserved byte-for-byte.");
                    var refusal = string.Join(" | ", dialog.GetVisualDescendants().OfType<TextBlock>()
                        .Select(block => block.Text).Where(text => !string.IsNullOrWhiteSpace(text)));
                    Assert(refusal.Contains("changed since its contents were reviewed", StringComparison.OrdinalIgnoreCase),
                        "The dialog did not explain why the reviewed archive was refused: " + refusal);
                    if (output is not null) SaveFrame(dialog, Path.Combine(output, "opusmac-local-zip-stale-archive-refusal.png"), true);
                }
                finally
                {
                    if (dialog.IsVisible) Click(dialog, "Close");
                }
            });
            await CheckAsync("Local Create ZIP Save rejects replaced file identity and permits fresh selection", async () =>
            {
                Application.Current!.RequestedThemeVariant = ThemeVariant.Dark;
                window.Width = 1400; window.Height = 860;
                // Headless UI uses the incumbent explicit-path fallback. The
                // source confirmation is shared with the native Windows Save
                // picker; this must not be represented as installed picker UI.
                var source = Path.Combine(left, "ordinary unindexed selection.txt");
                var movedOriginal = Path.Combine(left, "held-original-different-inode.txt");
                var blockedZip = Path.Combine(right, "unreviewed-selection.zip");
                var freshZip = Path.Combine(right, "fresh-selection.zip");
                var original = Encoding.UTF8.GetBytes("original content for controlled source replacement");
                var replacement = Encoding.UTF8.GetBytes(new string('Z', original.Length));
                Assert(original.Length == replacement.Length, "The stale fixture must have matching byte counts.");
                await File.WriteAllBytesAsync(source, original);
                await window.ActivePane.NavigateAsync(left);
                window.ActivePane.SelectPaths([source]);
                Click(window, "Archive");
                var staleDialog = await FindDialogAsync(window, "Create ZIP");
                staleDialog.GetVisualDescendants().OfType<TextBox>().First().Text = blockedZip;
                var timestamp = File.GetLastWriteTimeUtc(source);
                File.Move(source, movedOriginal);
                await File.WriteAllBytesAsync(source, replacement);
                File.SetLastWriteTimeUtc(source, timestamp);
                Assert(File.GetLastWriteTimeUtc(source) == timestamp && new FileInfo(source).Length == original.Length,
                    "The replacement must preserve the stale selection's size and timestamp.");
                Click(staleDialog, "Create ZIP");
                await window.LastCommand;
                Assert(!File.Exists(blockedZip), "An unreviewed same-path replacement was archived after the Save dialog.");
                Assert((await File.ReadAllBytesAsync(source)).SequenceEqual(replacement), "A refused Save modified its replacement source.");
                Assert((await File.ReadAllBytesAsync(movedOriginal)).SequenceEqual(original), "A refused Save modified the held original.");
                Assert(window.StatusMessage.Contains("changed", StringComparison.OrdinalIgnoreCase) ||
                       window.StatusMessage.Contains("selection", StringComparison.OrdinalIgnoreCase),
                    "Stale source refusal must provide useful status feedback.");

                await window.ActivePane.RefreshAsync();
                window.ActivePane.SelectPaths([source]);
                Click(window, "Archive");
                var newDialog = await FindDialogAsync(window, "Create ZIP");
                newDialog.GetVisualDescendants().OfType<TextBox>().First().Text = freshZip;
                Click(newDialog, "Create ZIP");
                await window.LastCommand;
                Assert(File.Exists(freshZip), "A fresh verified selection did not produce the ZIP.");
                using (var zipped = ZipFile.OpenRead(freshZip))
                {
                    var entry = zipped.Entries.Single(item => item.Name == Path.GetFileName(source));
                    await using var reader = entry.Open();
                    using var memory = new MemoryStream();
                    await reader.CopyToAsync(memory);
                    Assert(memory.ToArray().SequenceEqual(replacement), "The retried ZIP did not contain the fresh selected bytes.");
                }
                Assert((await File.ReadAllBytesAsync(source)).SequenceEqual(replacement), "ZIP creation changed the selected source.");
                Assert(!File.Exists(blockedZip), "The stale refusal acquired an output after retry.");

                var second = Path.Combine(left, "second selected source.bin");
                var secondaryOriginal = Encoding.UTF8.GetBytes("a second source in the ordered two-file selection");
                var secondaryReplacement = Encoding.UTF8.GetBytes(new string('Y', secondaryOriginal.Length));
                var secondaryHeld = Path.Combine(left, "held secondary source.bin");
                var refusedBatchZip = Path.Combine(right, "refused-mixed-batch.zip");
                var verifiedBatchZip = Path.Combine(right, "fresh-mixed-batch.zip");
                await File.WriteAllBytesAsync(second, secondaryOriginal);
                await window.ActivePane.RefreshAsync();
                window.ActivePane.SelectPaths([source, second]);
                Click(window, "Archive");
                var batchDialog = await FindDialogAsync(window, "Create ZIP");
                batchDialog.GetVisualDescendants().OfType<TextBox>().First().Text = refusedBatchZip;
                var secondTimestamp = File.GetLastWriteTimeUtc(second);
                File.Move(second, secondaryHeld);
                await File.WriteAllBytesAsync(second, secondaryReplacement);
                File.SetLastWriteTimeUtc(second, secondTimestamp);
                Click(batchDialog, "Create ZIP");
                await window.LastCommand;
                Assert(!File.Exists(refusedBatchZip), "One replaced member admitted an unreviewed two-file ZIP.");
                Assert((await File.ReadAllBytesAsync(source)).SequenceEqual(replacement) &&
                       (await File.ReadAllBytesAsync(second)).SequenceEqual(secondaryReplacement) &&
                       (await File.ReadAllBytesAsync(secondaryHeld)).SequenceEqual(secondaryOriginal),
                    "Rejected mixed ZIP changed a selected or held source.");

                await window.ActivePane.RefreshAsync();
                window.ActivePane.SelectPaths([source, second]);
                Click(window, "Archive");
                var freshBatchDialog = await FindDialogAsync(window, "Create ZIP");
                freshBatchDialog.GetVisualDescendants().OfType<TextBox>().First().Text = verifiedBatchZip;
                Click(freshBatchDialog, "Create ZIP");
                await window.LastCommand;
                Assert(File.Exists(verifiedBatchZip), "Verified two-file retry did not commit its ZIP.");
                using (var batch = ZipFile.OpenRead(verifiedBatchZip))
                {
                    Assert(batch.Entries.Count == 2, "Verified retry has the wrong number of ZIP entries.");
                    foreach (var (filename, expected) in new[] {
                                 (Path.GetFileName(source), replacement), (Path.GetFileName(second), secondaryReplacement) })
                    {
                        await using var reader = batch.Entries.Single(item => item.Name == filename).Open();
                        using var memory = new MemoryStream();
                        await reader.CopyToAsync(memory);
                        Assert(memory.ToArray().SequenceEqual(expected), "Verified mixed ZIP content does not match its source.");
                    }
                }
                await window.ActivePane.RefreshAsync();
                window.ActivePane.SelectPaths([source]);
                Click(window, "Archive");
                var canceledDialog = await FindDialogAsync(window, "Create ZIP");
                var canceledZip = Path.Combine(right, "cancel-must-not-exist.zip");
                canceledDialog.GetVisualDescendants().OfType<TextBox>().First().Text = canceledZip;
                Click(canceledDialog, "Cancel");
                await window.LastCommand;
                Assert(!File.Exists(canceledZip) && (await File.ReadAllBytesAsync(source)).SequenceEqual(replacement),
                    "Canceling ZIP destination review changed a source or wrote an archive.");
                if (output is not null) SaveFrame(window, Path.Combine(output, "opusmac-local-create-zip-identity-retry.png"));
            });
            await CheckAsync("Local Copy and Move reject same-path replacements during collision review", async () =>
            {
                async Task ClickReadyTransferAsync(string action, string source)
                {
                    // Watcher-driven pane refreshes can begin after RefreshAsync
                    // returns. Require a stable, selected pane before invoking
                    // the actual file-operation command under test.
                    for (var attempt = 0; attempt < 5; attempt++)
                    {
                        await WaitAsync(() => !window.ActivePane.IsLoading &&
                            !window.OtherPane.IsLoading &&
                            window.ActivePane.SelectedEntries.Any(entry => entry.FullPath == source));
                        await Task.Delay(50);
                        if (!window.ActivePane.IsLoading && !window.OtherPane.IsLoading &&
                            window.ActivePane.SelectedEntries.Any(entry => entry.FullPath == source))
                        {
                            Click(window, action);
                            return;
                        }
                    }
                    throw new TimeoutException("The transfer fixture did not settle after file-watcher refresh.");
                }
                async Task<Window> FindCollisionAsync(string phase)
                {
                    try { return await FindDialogAsync(window, "Existing files"); }
                    catch (TimeoutException error)
                    {
                        throw new InvalidOperationException(
                            $"Collision review absent at {phase}. Command: {window.LastCommand.Status}; " +
                            $"status: {window.StatusMessage}; active loading: {window.ActivePane.IsLoading}; " +
                            $"destination loading: {window.OtherPane.IsLoading}; " +
                            $"source directory: {window.ActivePane.CurrentDirectory}; " +
                            $"destination directory: {window.OtherPane.CurrentDirectory}; " +
                            $"selected: [{string.Join(", ", window.ActivePane.SelectedEntries.Select(entry => entry.Name))}]; " +
                            $"action entries: [{string.Join(", ", window.ActivePane.ActionEntries.Select(entry => entry.Name))}]; " +
                            $"owned windows: [{string.Join(", ", window.OwnedWindows.Select(dialog => $"{dialog.Title}:{dialog.IsVisible}"))}]; " +
                            $"history: {window.OperationHistory.Count}.", error);
                    }
                }
                Application.Current!.RequestedThemeVariant = ThemeVariant.Dark;
                window.Width = 1400; window.Height = 860;
                await window.ActivePane.NavigateAsync(left);
                await window.OtherPane.NavigateAsync(right);
                var sourceCopy = Path.Combine(left, "selected-copy.txt");
                var destinationCopy = Path.Combine(right, "selected-copy.txt");
                var heldCopy = Path.Combine(left, "held-original-copy.txt");
                var original = Encoding.UTF8.GetBytes("Original file selected before transfer collision review");
                var replacement = Encoding.UTF8.GetBytes(new string('C', original.Length));
                var destinationBytes = Encoding.UTF8.GetBytes("Existing destination must not be overwritten by a stale selection");
                await File.WriteAllBytesAsync(sourceCopy, original);
                await File.WriteAllBytesAsync(destinationCopy, destinationBytes);
                await window.ActivePane.RefreshAsync();
                window.ActivePane.SelectPaths([sourceCopy]);
                await ClickReadyTransferAsync("Copy Files", sourceCopy);
                var conflict = await FindCollisionAsync("stale Copy");
                conflict.GetVisualDescendants().OfType<ComboBox>().Single().SelectedIndex = 2;
                var stamp = File.GetLastWriteTimeUtc(sourceCopy);
                File.Move(sourceCopy, heldCopy);
                await File.WriteAllBytesAsync(sourceCopy, replacement);
                File.SetLastWriteTimeUtc(sourceCopy, stamp);
                Click(conflict, "Continue");
                await window.LastCommand;
                Assert((await File.ReadAllBytesAsync(destinationCopy)).SequenceEqual(destinationBytes),
                    "A changed source was copied over an existing destination after the collision review.");
                Assert((await File.ReadAllBytesAsync(sourceCopy)).SequenceEqual(replacement) &&
                       (await File.ReadAllBytesAsync(heldCopy)).SequenceEqual(original),
                    "Rejected Copy changed the task-owned source or held original.");

                // A short same-file edit can preserve native identity, length,
                // and even last-write time. A reviewed Replace decision must
                // not authorize the new bytes merely because the timestamp
                // was restored by the editing program.
                await window.ActivePane.RefreshAsync();
                window.ActivePane.SelectPaths([sourceCopy]);
                var approvedStamp = File.GetLastWriteTimeUtc(sourceCopy);
                var approvedNativeId = ListaryClient.ReadNativeItemIdentity(sourceCopy);
                var sameSizeEdit = Enumerable.Repeat((byte)'Z', replacement.Length).ToArray();
                var beforeSameSizeReview = window.OperationHistory.Count;
                await ClickReadyTransferAsync("Copy Files", sourceCopy);
                var sameSizeConflict = await FindCollisionAsync("same-size timestamp-restored Copy");
                sameSizeConflict.GetVisualDescendants().OfType<ComboBox>().Single().SelectedIndex = 2;
                await File.WriteAllBytesAsync(sourceCopy, sameSizeEdit);
                File.SetLastWriteTimeUtc(sourceCopy, approvedStamp);
                Assert(File.GetLastWriteTimeUtc(sourceCopy) == approvedStamp &&
                       new FileInfo(sourceCopy).Length == replacement.Length &&
                       string.Equals(approvedNativeId, ListaryClient.ReadNativeItemIdentity(sourceCopy), StringComparison.Ordinal),
                    "The same-metadata source-edit fixture did not preserve its version fields.");
                Click(sameSizeConflict, "Continue"); await window.LastCommand;
                Assert(window.OperationHistory.Count == beforeSameSizeReview &&
                       (await File.ReadAllBytesAsync(destinationCopy)).SequenceEqual(destinationBytes) &&
                       (await File.ReadAllBytesAsync(sourceCopy)).SequenceEqual(sameSizeEdit) &&
                       window.StatusMessage.Contains("selected file changed", StringComparison.OrdinalIgnoreCase),
                    "A timestamp-restored, same-size in-place edit was copied over an existing destination after review.");
                await File.WriteAllBytesAsync(sourceCopy, replacement);
                File.SetLastWriteTimeUtc(sourceCopy, approvedStamp);

                // A larger file skips the bounded content hash. Windows still
                // needs to detect an in-place edit with length and mtime kept.
                var largeSource = Path.Combine(left, "selected-large-copy.bin");
                var largeTarget = Path.Combine(right, "selected-large-copy.bin");
                var largeOriginal = Enumerable.Repeat((byte)'A', 1024 * 1024 + 4096).ToArray();
                var largeChanged = Enumerable.Repeat((byte)'B', largeOriginal.Length).ToArray();
                var largeHeldTarget = Encoding.UTF8.GetBytes("existing large-file collision must survive");
                await File.WriteAllBytesAsync(largeSource, largeOriginal);
                await File.WriteAllBytesAsync(largeTarget, largeHeldTarget);
                await window.ActivePane.RefreshAsync();
                window.ActivePane.SelectPaths([largeSource]);
                var largeStamp = File.GetLastWriteTimeUtc(largeSource);
                var largeId = ListaryClient.ReadNativeItemIdentity(largeSource);
                var largeBaseline = window.OperationHistory.Count;
                await ClickReadyTransferAsync("Copy Files", largeSource);
                var largeDialog = await FindCollisionAsync("large same-size timestamp-restored Copy");
                largeDialog.GetVisualDescendants().OfType<ComboBox>().Single().SelectedIndex = 2;
                await File.WriteAllBytesAsync(largeSource, largeChanged);
                File.SetLastWriteTimeUtc(largeSource, largeStamp);
                Assert(new FileInfo(largeSource).Length == largeOriginal.Length &&
                       File.GetLastWriteTimeUtc(largeSource) == largeStamp &&
                       string.Equals(largeId, ListaryClient.ReadNativeItemIdentity(largeSource), StringComparison.Ordinal),
                    "The large same-metadata edit fixture changed its native identity, length or mtime.");
                Click(largeDialog, "Continue"); await window.LastCommand;
                Assert(window.OperationHistory.Count == largeBaseline &&
                       (await File.ReadAllBytesAsync(largeTarget)).SequenceEqual(largeHeldTarget) &&
                       (await File.ReadAllBytesAsync(largeSource)).SequenceEqual(largeChanged) &&
                       window.StatusMessage.Contains("selected file changed", StringComparison.OrdinalIgnoreCase),
                    "A same-size, restored-mtime large source edit was accepted during Copy collision review.");

                // Check the other side of the same decision. A large collision
                // target must retain its review identity even when an editor
                // restores the timestamp and keeps the byte length unchanged.
                var largeReviewedTarget = Enumerable.Repeat((byte)'D', largeOriginal.Length).ToArray();
                var largeTargetEdit = Enumerable.Repeat((byte)'E', largeOriginal.Length).ToArray();
                await File.WriteAllBytesAsync(largeSource, largeOriginal);
                await File.WriteAllBytesAsync(largeTarget, largeReviewedTarget);
                await window.ActivePane.RefreshAsync();
                window.ActivePane.SelectPaths([largeSource]);
                var targetStamp = File.GetLastWriteTimeUtc(largeTarget);
                var targetId = ListaryClient.ReadNativeItemIdentity(largeTarget);
                var targetReviewHistory = window.OperationHistory.Count;
                await ClickReadyTransferAsync("Copy Files", largeSource);
                var targetDialog = await FindCollisionAsync("large timestamp-restored destination edit");
                targetDialog.GetVisualDescendants().OfType<ComboBox>().Single().SelectedIndex = 2;
                await File.WriteAllBytesAsync(largeTarget, largeTargetEdit);
                File.SetLastWriteTimeUtc(largeTarget, targetStamp);
                Assert(new FileInfo(largeTarget).Length == largeReviewedTarget.Length &&
                       File.GetLastWriteTimeUtc(largeTarget) == targetStamp &&
                       string.Equals(targetId, ListaryClient.ReadNativeItemIdentity(largeTarget), StringComparison.Ordinal),
                    "The stale destination fixture changed its approved metadata or identity.");
                Click(targetDialog, "Continue"); await window.LastCommand;
                Assert(window.OperationHistory.Count == targetReviewHistory &&
                       (await File.ReadAllBytesAsync(largeTarget)).SequenceEqual(largeTargetEdit) &&
                       (await File.ReadAllBytesAsync(largeSource)).SequenceEqual(largeOriginal) &&
                       window.StatusMessage.Contains("collision target changed", StringComparison.OrdinalIgnoreCase),
                    "A large destination changed during review was overwritten or committed.");

                // A new intentional selection must still work under the same
                // native operation owner; choose a new destination to avoid
                // an unneeded second overwrite prompt.
                File.Move(destinationCopy, Path.Combine(right, "held-destination-copy.txt"));
                await window.ActivePane.NavigateAsync(left);
                await window.OtherPane.NavigateAsync(right);
                await WaitAsync(() => !window.ActivePane.IsLoading && !window.OtherPane.IsLoading);
                window.ActivePane.SelectPaths([sourceCopy]);
                await WaitAsync(() => !window.ActivePane.IsLoading && !window.OtherPane.IsLoading &&
                    window.ActivePane.SelectedEntries.Any(entry => entry.FullPath == sourceCopy));
                await ClickReadyTransferAsync("Copy Files", sourceCopy);
                await window.LastCommand;
                Assert(File.Exists(destinationCopy),
                    $"Fresh Copy did not publish the expected destination. UI status: {window.StatusMessage}; " +
                    $"source loading: {window.ActivePane.IsLoading}; destination loading: {window.OtherPane.IsLoading}; " +
                    $"selected: {string.Join(", ", window.ActivePane.SelectedEntries.Select(entry => entry.Name))}; " +
                    $"history entries: {window.OperationHistory.Count}.");
                Assert((await File.ReadAllBytesAsync(destinationCopy)).SequenceEqual(replacement) &&
                       (await File.ReadAllBytesAsync(sourceCopy)).SequenceEqual(replacement),
                    "The authorized fresh Copy did not preserve exact source and output bytes.");

                var sourceMove = Path.Combine(left, "selected-move.txt");
                var destinationMove = Path.Combine(right, "selected-move.txt");
                var heldMove = Path.Combine(left, "held-original-move.txt");
                await File.WriteAllBytesAsync(sourceMove, original);
                await File.WriteAllBytesAsync(destinationMove, destinationBytes);
                await window.ActivePane.RefreshAsync();
                await window.OtherPane.RefreshAsync();
                window.ActivePane.SelectPaths([sourceMove]);
                await ClickReadyTransferAsync("Move Files", sourceMove);
                var moveConflict = await FindCollisionAsync("stale Move");
                moveConflict.GetVisualDescendants().OfType<ComboBox>().Single().SelectedIndex = 2;
                stamp = File.GetLastWriteTimeUtc(sourceMove);
                File.Move(sourceMove, heldMove);
                await File.WriteAllBytesAsync(sourceMove, replacement);
                File.SetLastWriteTimeUtc(sourceMove, stamp);
                Click(moveConflict, "Continue");
                await window.LastCommand;
                Assert((await File.ReadAllBytesAsync(destinationMove)).SequenceEqual(destinationBytes) &&
                       (await File.ReadAllBytesAsync(sourceMove)).SequenceEqual(replacement) &&
                       (await File.ReadAllBytesAsync(heldMove)).SequenceEqual(original),
                    "A changed source was moved or replaced during the collision review.");

                File.Move(destinationMove, Path.Combine(right, "held-destination-move.txt"));
                await window.ActivePane.RefreshAsync();
                await window.OtherPane.RefreshAsync();
                window.ActivePane.SelectPaths([sourceMove]);
                await ClickReadyTransferAsync("Move Files", sourceMove);
                await window.LastCommand;
                Assert(!File.Exists(sourceMove) && (await File.ReadAllBytesAsync(destinationMove)).SequenceEqual(replacement),
                    "The authorized fresh Move did not transfer the exact selected file.");

                // The destination folder object is also sealed before the
                // collision decision. A new directory at the SAME path must
                // not inherit the reviewed source/destination authority.
                var newSource = Path.Combine(left, "destination-folder-guard.txt");
                var oldDestination = Path.Combine(right, "destination-folder-guard.txt");
                var retainedDirectory = Path.Combine(root, "held-original-destination-folder");
                await File.WriteAllBytesAsync(newSource, original);
                await File.WriteAllBytesAsync(oldDestination, destinationBytes);
                await window.ActivePane.RefreshAsync();
                await window.OtherPane.RefreshAsync();
                window.ActivePane.SelectPaths([newSource]);
                await ClickReadyTransferAsync("Copy Files", newSource);
                var destinationReview = await FindCollisionAsync("destination folder replacement");
                destinationReview.GetVisualDescendants().OfType<ComboBox>().Single().SelectedIndex = 2;
                Directory.Move(right, retainedDirectory);
                Directory.CreateDirectory(right);
                var newOccupant = Path.Combine(right, Path.GetFileName(newSource));
                var competingBytes = Encoding.UTF8.GetBytes("Competing folder and file must survive stale destination refusal");
                await File.WriteAllBytesAsync(newOccupant, competingBytes);
                Click(destinationReview, "Continue");
                await window.LastCommand;
                Assert((await File.ReadAllBytesAsync(newOccupant)).SequenceEqual(competingBytes) &&
                       (await File.ReadAllBytesAsync(Path.Combine(retainedDirectory, Path.GetFileName(newSource)))).SequenceEqual(destinationBytes) &&
                       (await File.ReadAllBytesAsync(newSource)).SequenceEqual(original),
                    "A changed dual-pane destination redirected an unreviewed Copy into a replacement folder.");
                // Explicitly navigate to the new real folder before retrying.
                await window.OtherPane.NavigateAsync(right);
                File.Move(newOccupant, Path.Combine(right, "retained-competing-file.txt"));
                await window.OtherPane.RefreshAsync();
                await window.ActivePane.RefreshAsync();
                window.ActivePane.SelectPaths([newSource]);
                await ClickReadyTransferAsync("Copy Files", newSource);
                await window.LastCommand;
                Assert((await File.ReadAllBytesAsync(newOccupant)).SequenceEqual(original),
                    "A fresh destination selection did not create the verified file.");
                if (output is not null) SaveFrame(window, Path.Combine(output, "opusmac-local-transfer-identity-verified.png"));
            });
            await CheckAsync("Folder columns follow saved paths and descendants", async () =>
            {
                var pane = window.ActivePane;
                await pane.NavigateAsync(left);
                window.Dispatch("folder-format");
                var dialog = await FindDialogAsync(window, "Folder Format / Columns");
                var path = dialog.GetVisualDescendants().OfType<CheckBox>()
                    .Single(control => control.Name == "FolderFormatVisible_Path");
                path.IsChecked = true;
                path.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                dialog.GetVisualDescendants().OfType<ComboBox>()
                    .Single(control => control.Name == "FolderFormatScope").SelectedIndex = 2;
                dialog.GetVisualDescendants().OfType<ComboBox>()
                    .Single(control => control.Name == "FolderFormatSortColumn").SelectedItem = "Name";
                dialog.GetVisualDescendants().OfType<ComboBox>()
                    .Single(control => control.Name == "FolderFormatSortDirection").SelectedIndex = 1;
                dialog.GetVisualDescendants().OfType<ComboBox>()
                    .Single(control => control.Name == "FolderFormatViewMode").SelectedItem = "Tiles";
                ClickNamedButton(dialog, "FolderFormatApply");
                await window.LastCommand;
                Assert(pane.VisibleColumnIds.Contains("Path"), "The folder rule did not apply immediately.");
                await pane.NavigateAsync(Path.Combine(left, "Design"));
                Assert(pane.VisibleColumnIds.Contains("Path"), "A child folder did not inherit its columns.");
                Assert(pane.CurrentSortColumn == "Name" && pane.CurrentSortDescending,
                    "The child folder did not inherit the selected sort.");
                Assert(pane.CurrentViewMode == "Tiles" && !pane.FileGrid.IsVisible,
                    "The child folder did not render the inherited tile view.");
                await pane.NavigateAsync(right);
                Assert(!pane.VisibleColumnIds.Contains("Path"), "The folder rule leaked to a sibling.");
                Assert(pane.CurrentSortColumn == "Modified", "The folder sort leaked into the tab fallback.");
                Assert(pane.CurrentViewMode == "Details" && pane.FileGrid.IsVisible,
                    "The folder view leaked into the tab fallback.");
                await pane.NavigateAsync(left);
                Assert(pane.VisibleColumnIds.Contains("Path"), "The folder rule did not reapply on return.");
                await window.SaveSettingsAsync();
                var reloaded = new SettingsStore(state).Load();
                var rule = FolderColumnRule.Resolve(reloaded.FolderColumnRules, Path.Combine(left, "Design"));
                Assert(rule is { IncludeSubfolders: true, SortColumn: "Name", SortDescending: true, ViewMode: "Tiles" } &&
                    rule.Columns.Any(column => column.Id == "Path" && column.Visible),
                    "The inherited column rule did not survive reopening settings.");
                var child = Path.Combine(left, "Design");
                await pane.NavigateAsync(child);
                window.Dispatch("folder-format");
                var childDialog = await FindDialogAsync(window, "Folder Format / Columns");
                childDialog.GetVisualDescendants().OfType<ComboBox>()
                    .Single(control => control.Name == "FolderFormatScope").SelectedIndex = 1;
                childDialog.GetVisualDescendants().OfType<CheckBox>()
                    .Single(control => control.Name == "FolderFormatInheritColumns").IsChecked = true;
                childDialog.GetVisualDescendants().OfType<ComboBox>()
                    .Single(control => control.Name == "FolderFormatSortColumn").SelectedItem = "Inherit parent sort";
                childDialog.GetVisualDescendants().OfType<ComboBox>()
                    .Single(control => control.Name == "FolderFormatViewMode").SelectedItem = "Inherit parent view";
                ClickNamedButton(childDialog, "FolderFormatApply");
                await window.LastCommand;
                Assert(pane.VisibleColumnIds.Contains("Path"), "The child did not use its parent's columns.");
                Assert(pane.CurrentSortColumn == "Name" && pane.CurrentSortDescending && pane.CurrentViewMode == "Tiles",
                    "The child did not use its parent's sort and view.");

                await pane.NavigateAsync(left);
                window.Dispatch("folder-format");
                var parentDialog = await FindDialogAsync(window, "Folder Format / Columns");
                var parentPath = parentDialog.GetVisualDescendants().OfType<CheckBox>()
                    .Single(control => control.Name == "FolderFormatVisible_Path");
                parentPath.IsChecked = false;
                parentPath.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                parentDialog.GetVisualDescendants().OfType<ComboBox>()
                    .Single(control => control.Name == "FolderFormatSortColumn").SelectedItem = "Type";
                parentDialog.GetVisualDescendants().OfType<ComboBox>()
                    .Single(control => control.Name == "FolderFormatSortDirection").SelectedIndex = 0;
                parentDialog.GetVisualDescendants().OfType<ComboBox>()
                    .Single(control => control.Name == "FolderFormatViewMode").SelectedItem = "Details";
                ClickNamedButton(parentDialog, "FolderFormatApply");
                await window.LastCommand;
                await pane.NavigateAsync(child);
                Assert(!pane.VisibleColumnIds.Contains("Path"), "The child retained a stale snapshot of its parent's columns.");
                Assert(pane.CurrentSortColumn == "Type" && !pane.CurrentSortDescending && pane.CurrentViewMode == "Details",
                    "The child did not follow the updated parent sort and view.");
                await window.SaveSettingsAsync();
                var childRule = FolderColumnRule.Resolve(new SettingsStore(state).Load().FolderColumnRules, child);
                Assert(childRule is { InheritColumns: true, InheritSort: true, InheritView: true },
                    "The child inheritance choices did not survive settings reload.");
                window.Dispatch("folder-format");
                var disableDialog = await FindDialogAsync(window, "Folder Format / Columns");
                disableDialog.GetVisualDescendants().OfType<ComboBox>()
                    .Single(control => control.Name == "FolderFormatSortColumn").SelectedItem = "Size";
                disableDialog.GetVisualDescendants().OfType<CheckBox>()
                    .Single(control => control.Name == "FolderFormatEnabled").IsChecked = false;
                ClickNamedButton(disableDialog, "FolderFormatApply");
                await window.LastCommand;
                Assert(pane.CurrentSortColumn == "Type", "A disabled child overrode the enabled parent.");
                await window.SaveSettingsAsync();
                var stored = new SettingsStore(state).Load().FolderColumnRules.Single(entry => entry.Path == child);
                Assert(stored is { Disabled: true, SortColumn: "Size" }, "Disabling lost the saved rule.");
                window.Dispatch("folder-format");
                var enableDialog = await FindDialogAsync(window, "Folder Format / Columns");
                enableDialog.GetVisualDescendants().OfType<CheckBox>()
                    .Single(control => control.Name == "FolderFormatEnabled").IsChecked = true;
                ClickNamedButton(enableDialog, "FolderFormatApply");
                await window.LastCommand;
                Assert(pane.CurrentSortColumn == "Size", "Re-enabling did not restore the saved child rule.");
                await pane.NavigateAsync(left);
                var formatStatus = pane.GetVisualDescendants().OfType<Button>()
                    .Single(control => control.Name == "FolderFormatStatus");
                var lockMenu = formatStatus.ContextMenu!.Items.OfType<MenuItem>().First();
                Assert(Equals(lockMenu.Header, "Lock Folder Format"), "The pane did not offer Format Lock.");
                lockMenu.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                await window.LastCommand;
                await pane.NavigateAsync(child);
                Assert(pane.IsFormatLocked && pane.CurrentSortColumn == "Type",
                    "Navigating into a different rule changed the locked format.");
                Assert(AutomationProperties.GetName(formatStatus) == "Folder format locked" &&
                    Equals(lockMenu.Header, "Unlock Folder Format"), "The indicator did not show the active lock.");
                await window.SaveSettingsAsync();
                var lockedTab = new SettingsStore(state).Load().Panes[window.ActivePaneIndex].Tabs[pane.ActiveTabIndex];
                Assert(lockedTab.FormatLock is { SortColumn: "Type", ViewMode: "Details" },
                    "The locked format did not survive settings reload.");
                await pane.AddTabAsync(right);
                Assert(AutomationProperties.GetName(formatStatus) == "Folder format follows navigation",
                    "A new tab inherited another tab's format indicator.");
                pane.NextTab(-1);
                await pane.LoadingTask;
                Assert(AutomationProperties.GetName(formatStatus) == "Folder format locked",
                    "Returning to the locked tab did not restore its indicator.");
                window.Dispatch("lock-format");
                await window.LastCommand;
                Assert(!pane.IsFormatLocked && pane.CurrentSortColumn == "Size",
                    "Unlocking did not resume the current folder rule.");
                var resetMenu = formatStatus.ContextMenu!.Items.OfType<MenuItem>()
                    .Single(item => Equals(item.Header, "Reset to Folder's Format"));
                pane.ApplySort("Name", true);
                Assert(pane.CurrentSortColumn == "Name", "The temporary sort did not take effect.");
                resetMenu.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                await window.LastCommand;
                Assert(pane.CurrentSortColumn == "Size", "Reset did not restore the saved folder sort.");
                lockMenu.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                await window.LastCommand;
                pane.ApplySort("Modified", true);
                resetMenu.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                await window.LastCommand;
                Assert(pane.IsFormatLocked && pane.CurrentSortColumn == "Size",
                    "Reset did not refresh the locked format without clearing the lock.");
                window.Dispatch("lock-format");
                await window.LastCommand;
                var defaultMenu = formatStatus.ContextMenu!.Items.OfType<MenuItem>()
                    .Single(item => Equals(item.Header, "Edit User Default Format…"));
                defaultMenu.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                var defaultDialog = await FindDialogAsync(window, "User Default Format");
                defaultDialog.GetVisualDescendants().OfType<ComboBox>()
                    .Single(control => control.Name == "FolderFormatSortColumn").SelectedItem = "Created";
                defaultDialog.GetVisualDescendants().OfType<ComboBox>()
                    .Single(control => control.Name == "FolderFormatViewMode").SelectedItem = "Tiles";
                ClickNamedButton(defaultDialog, "FolderFormatApply");
                await window.LastCommand;
                Assert(pane.CurrentSortColumn == "Size", "User Default overrode the specific folder rule.");
                var resetDefault = formatStatus.ContextMenu!.Items.OfType<MenuItem>()
                    .Single(item => Equals(item.Header, "Reset to User Default Format"));
                resetDefault.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                await window.LastCommand;
                Assert(pane.CurrentSortColumn == "Created" && pane.CurrentViewMode == "Tiles",
                    "Reset to User Default did not bypass the specific folder rule.");
                await pane.RefreshAsync();
                Assert(pane.CurrentSortColumn == "Created", "Refreshing dropped the temporary User Default.");
                await pane.NavigateAsync(right);
                Assert(pane.CurrentSortColumn == "Created" && pane.CurrentViewMode == "Tiles",
                    "The unmatched folder did not pick up User Default.");
                await window.SaveSettingsAsync();
                Assert(new SettingsStore(state).Load().UserDefaultFolderFormat is { SortColumn: "Created", ViewMode: "Tiles" },
                    "User Default did not survive settings reload.");
                await pane.NavigateAsync(child);
                Assert(pane.CurrentSortColumn == "Size", "The temporary reset changed the saved folder rule.");
                await pane.NavigateAsync(right);
                window.Dispatch("manage-folder-formats");
                var manager = await FindDialogAsync(window, "Manage Folder Formats");
                var rules = manager.GetVisualDescendants().OfType<ListBox>()
                    .Single(control => control.Name == "SavedFolderFormats");
                rules.SelectedItem = rules.Items.OfType<FolderColumnRule>().Single(rule => rule.Path == child);
                Click(manager, "Enable / Disable");
                Assert(rules.SelectedItem is FolderColumnRule { Disabled: true },
                    "The manager did not retain the disabled rule.");
                Click(manager, "Edit…");
                var managed = await FindDialogAsync(window, "Folder Format / Columns");
                managed.GetVisualDescendants().OfType<CheckBox>()
                    .Single(control => control.Name == "FolderFormatEnabled").IsChecked = true;
                managed.GetVisualDescendants().OfType<ComboBox>()
                    .Single(control => control.Name == "FolderFormatSortColumn").SelectedItem = "Name";
                ClickNamedButton(managed, "FolderFormatApply");
                await window.LastCommand;
                Assert(pane.CurrentDirectory == right && pane.CurrentSortColumn == "Created",
                    "Editing a different folder's rule changed the current folder.");
                await pane.NavigateAsync(child);
                Assert(pane.CurrentSortColumn == "Name", "The manager did not edit the selected rule.");
                window.Dispatch("manage-folder-formats");
                manager = await FindDialogAsync(window, "Manage Folder Formats");
                rules = manager.GetVisualDescendants().OfType<ListBox>().Single(control => control.Name == "SavedFolderFormats");
                rules.SelectedItem = rules.Items.OfType<FolderColumnRule>().Single(rule => rule.Path == child);
                Click(manager, "Remove Rule");
                Assert(!rules.Items.OfType<FolderColumnRule>().Any(rule => rule.Path == child) &&
                    Directory.Exists(child) && pane.CurrentSortColumn == "Type",
                    "Removing a rule did not leave the folder intact and restore its parent's format.");
                Click(manager, "Close");
                await window.LastCommand;
                await pane.NavigateAsync(right);
                window.Dispatch("manage-folder-formats");
                manager = await FindDialogAsync(window, "Manage Folder Formats");
                var newRulePath = manager.GetVisualDescendants().OfType<TextBox>()
                    .Single(control => control.Name == "NewFolderFormatPath");
                newRulePath.Text = "relative-folder";
                Click(manager, "Add Rule…");
                Assert(manager.IsVisible, "A relative path was accepted for a saved folder rule.");
                newRulePath.Text = child;
                Click(manager, "Add Rule…");
                managed = await FindDialogAsync(window, "Folder Format / Columns");
                managed.GetVisualDescendants().OfType<ComboBox>()
                    .Single(control => control.Name == "FolderFormatSortColumn").SelectedItem = "Attr";
                ClickNamedButton(managed, "FolderFormatApply");
                await window.LastCommand;
                Assert(pane.CurrentDirectory == right, "Adding a rule navigated away from the current folder.");
                await pane.NavigateAsync(child);
                Assert(pane.CurrentSortColumn == "Attr", "The newly added rule did not apply on navigation.");
                window.Dispatch("folder-format");
                var previewDialog = await FindDialogAsync(window, "Folder Format / Columns");
                var previewPicker = previewDialog.GetVisualDescendants().OfType<ComboBox>()
                    .Single(control => control.Name == "ImportedFolderFormats");
                var previewScope = previewDialog.GetVisualDescendants().OfType<ComboBox>()
                    .Single(control => control.Name == "FolderFormatScope");
                var sample = new ImportedFolderColumns("Descendants", FileColumnSettings.CreateDefaultLayout(),
                    "", null, null, false, true, false, false, false, false);
                previewPicker.ItemsSource = new[] { sample, sample with { Label = "Unspecified scope", IncludeSubfolders = null } };
                previewPicker.SelectedIndex = 0;
                Assert(previewScope.SelectedIndex == 2, "Explicit subfolder scope did not appear in preview.");
                previewPicker.SelectedIndex = 1;
                Assert(previewScope.SelectedIndex == 1, "Switching preview incorrectly retained descendant scope.");
                Click(previewDialog, "Cancel");
                await window.LastCommand;
                Assert(pane.CurrentSortColumn == "Attr", "Preview cancellation changed the saved folder rule.");
            });
            await CheckAsync("Opus column import maps supported fields without modifying settings", async () =>
            {
                const string xml = """
                    <folder_formats><path name="Fixture"><format view_mode="details" useforsubfolders="yes">
                    <field id="thumbnail" size="144" size_dpi="150" sort="1"/>
                    <field id="name" fill_max="yes"/><field id="type" size="300" size_dpi="150" sort="1"/>
                    <field id="sizeauto" size="100"/><field id="duration"/>
                    </format></path></folder_formats>
                    """;
                using var stream = new MemoryStream(Encoding.UTF8.GetBytes(xml));
                var imported = (await ImportedFolderColumns.ReadAsync(stream)).Single();
                Assert(imported.PrimarySortColumn == "Type", "The importer did not identify the primary sort column.");
                Assert(imported.ViewMode == "Details", "The importer did not identify Details view.");
                Assert(imported.IncludeSubfolders == true && !imported.KeepCurrentColumns,
                    "The importer did not carry the explicit descendant scope.");
                Assert(!imported.IsUserDefaultEntry, "An ordinary path format was misidentified as User Default.");
                using var defaults = new MemoryStream(Encoding.UTF8.GetBytes(xml
                    .Replace("name=\"Fixture\"", "name=\"default\"", StringComparison.Ordinal)));
                Assert((await ImportedFolderColumns.ReadAsync(defaults)).Single().IsUserDefaultEntry,
                    "The importer did not recognize the Windows User Default record.");
                var visible = imported.Columns.Where(column => column.Visible).ToArray();
                Assert(visible.Select(column => column.Id).SequenceEqual(new[] { "Thumbnail", "Name", "Type", "Size" }),
                    "The supported columns did not retain their reference order.");
                Assert(visible[0].Width == 96 && visible[1].WidthMode == "Star" && visible[2].Width == 200,
                    "The import did not preserve fill sizing or normalize the DPI width.");
                Assert(imported.Summary.StartsWith("1 unsupported column", StringComparison.Ordinal),
                    "The unsupported column was not reported.");
                using var inherited = new MemoryStream(Encoding.UTF8.GetBytes(xml
                    .Replace("useforsubfolders=\"yes\"",
                        "useforsubfolders=\"no\" inheritcolumns=\"yes\" inherit_sorting=\"yes\" inherit_view=\"yes\" disable=\"yes\"",
                        StringComparison.Ordinal)));
                var inheritedFormat = (await ImportedFolderColumns.ReadAsync(inherited)).Single();
                Assert(inheritedFormat is { KeepCurrentColumns: true, IncludeSubfolders: false,
                    InheritSort: true, InheritView: true, PrimarySortColumn: null, ViewMode: null, Disabled: true },
                    "The importer ignored inherited columns or exact-folder scope.");
                var pane = window.ActivePane;
                pane.ApplyCurrentColumnLayout(imported.Columns);
                Assert(pane.VisibleColumnIds.SequenceEqual(visible.Select(column => column.Id)) &&
                    pane.FileGrid.RowHeight == 96, "The imported thumbnail layout did not reach the real Details grid.");
                using var prohibited = new MemoryStream(Encoding.UTF8.GetBytes("<!DOCTYPE folder_formats><folder_formats/>"));
                try { await ImportedFolderColumns.ReadAsync(prohibited); }
                catch (System.Xml.XmlException) { return; }
                throw new InvalidOperationException("The import accepted a DTD.");
            });
            await CheckAsync("Folder comparison reviews and executes the complete copy plan", async () =>
            {
                window.ActivePane.FocusList();
                window.KeyPressQwerty(PhysicalKey.Y, RawInputModifiers.Control);
                window.KeyReleaseQwerty(PhysicalKey.Y, RawInputModifiers.Control);
                var compare = await FindDialogAsync(window, "Synchronize / Compare Folders");
                Click(compare, "Compare"); await WaitAsync(() => FindButton(compare, "Review copy plan…")?.IsEnabled == true);
                Click(compare, "Review copy plan…"); var plan = await FindDialogAsync(compare, "Confirm Synchronization");
                Assert(plan.GetVisualDescendants().OfType<DataGrid>().Any(grid => grid.ItemsSource.Cast<object>().Any()), "The complete copy plan is empty.");
                if (output is not null) SaveFrame(plan, Path.Combine(output, "opusmac-copy-plan-dialog.png"), true);
                Click(plan, "Apply copy plan");
                await WaitAsync(() => FindButton(compare, "Compare")?.IsEnabled == true && File.Exists(Path.Combine(right, "Design", "Layout.txt")));
                Assert(await File.ReadAllTextAsync(Path.Combine(right, "Research.md")) == await File.ReadAllTextAsync(Path.Combine(left, "Research.md")), "Synchronization did not copy the expected file.");
                Click(compare, "Close"); await window.LastCommand;
                window.ActivePane.SelectPaths([Path.Combine(left, "Project notes.txt")]);
            });
            await CheckAsync("Operation History reviews cancel and reclaims Undo storage", async () =>
            {
                var source = Path.Combine(left, "Project notes.txt");
                var destination = Path.Combine(right, "Project notes.txt");
                await File.WriteAllTextAsync(destination, "Previous destination contents retained for Undo history");
                await window.OtherPane.RefreshAsync();
                window.ActivePane.SelectPaths([source]);
                Click(window, "Copy Files");
                var conflict = await FindDialogAsync(window, "Existing files");
                conflict.GetVisualDescendants().OfType<ComboBox>().Single().SelectedIndex = 2;
                Click(conflict, "Continue");
                await window.LastCommand;
                var expected = await File.ReadAllTextAsync(source);
                Assert(await File.ReadAllTextAsync(destination) == expected, "Replacement copy did not commit before History review.");

                Click(window, "History");
                var history = await FindDialogAsync(window, "Operation History");
                Assert(FindButton(history, "Discard Undo History…") is { IsEnabled: true },
                    "History did not expose reviewed discard for live Undo records.");
                Click(history, "Discard Undo History…");
                var cancel = await FindDialogAsync(history, "Discard Undo History");
                Click(cancel, "Cancel");
                await WaitAsync(() => !history.OwnedWindows.Any(dialog => dialog.Title == "Discard Undo History" && dialog.IsVisible));
                Assert(window.CanUndoOperation, "Canceling discard disabled Undo.");
                Assert(await File.ReadAllTextAsync(destination) == expected, "Canceling discard changed destination bytes.");

                Click(history, "Discard Undo History…");
                var confirm = await FindDialogAsync(history, "Discard Undo History");
                Click(confirm, "Discard Undo History");
                await WaitAsync(() => FindButton(history, "Discard Undo History…") is not { IsVisible: true });
                Assert(await File.ReadAllTextAsync(destination) == expected, "Discard changed the committed destination bytes.");
                Assert(window.StatusMessage.Contains("reclaimed", StringComparison.OrdinalIgnoreCase),
                    "History discard did not report reclaimed storage.");
                Assert(!window.CanUndoOperation, "Discarded reviewed Undo remained enabled.");
                Click(history, "Close");
                await window.LastCommand;
            });
            await CheckAsync("Layout and tabs persist to an isolated profile", async () =>
            {
                await window.SaveSettingsAsync();
                var restored = new SettingsStore(state).Load();
                Assert(restored.Panes[0].Tabs[0].Path == left && restored.Panes[1].Tabs[0].Path == right, "Saved folders differ.");
                Assert(restored.DualPane && restored.ShowViewer, "Saved layout differs.");
            });
            await CheckAsync("Live Details column resize and reorder remain tab-local", async () =>
            {
                if (window.ActivePaneIndex != 0) { window.Dispatch("switch-pane"); await window.LastCommand; }
                var pane = window.ActivePane;
                var original = pane.Capture();
                var originalPath = pane.CurrentDirectory;
                try
                {
                    pane.ApplyCurrentColumnLayout(FileColumnSettings.CreateDefaultLayout());
                    var viewMode = pane.GetVisualDescendants().OfType<ComboBox>().Single();
                    viewMode.SelectedItem = "Details";
                    Dispatcher.UIThread.RunJobs();

                    // Sorted headers contain a vector direction marker, so
                    // their content is a Grid. Use the canonical column ID
                    // rather than assuming the header remains a text label.
                    var typeIndex = pane.VisibleColumnIds.ToList().FindIndex(id => id == "Type");
                    Assert(typeIndex >= 0, "The Type column is missing from the live Details grid.");
                    var typeColumn = pane.FileGrid.Columns[typeIndex];
                    typeColumn.DisplayIndex = 1;
                    typeColumn.Width = new DataGridLength(140);
                    Dispatcher.UIThread.RunJobs();

                    await pane.AddTabAsync(Path.Combine(left, "Design"));
                    Assert(pane.VisibleColumnIds.SequenceEqual(new[] { "Name", "Size", "Type", "Modified", "Attr" }),
                        "A new tab inherited the previous tab's live column order.");

                    pane.NextTab(-1);
                    await pane.LoadingTask;
                    Assert(pane.VisibleColumnIds.Take(2).SequenceEqual(new[] { "Name", "Type" }),
                        "Returning to the previous tab did not restore its live column order.");
                    var restoredType = pane.CurrentColumnLayout.Single(column => column.Id == "Type");
                    Assert(Math.Abs(restoredType.Width - 140) < 1,
                        "Returning to the previous tab did not restore its live resized column width.");
                }
                finally
                {
                    await pane.ApplySnapshotAsync(original);
                }
                Assert(pane.TabCount == 1 && pane.CurrentDirectory == originalPath,
                    "The column-isolation diagnostic did not restore the original tab state.");
            });
            await CheckAsync("Folder Formats inherit and override Checkbox Mode", async () =>
            {
                var parent = Directory.CreateDirectory(Path.Combine(root, "Checkbox format")).FullName;
                var inherited = Directory.CreateDirectory(Path.Combine(parent, "Inherited")).FullName;
                var exception = Directory.CreateDirectory(Path.Combine(parent, "Exception")).FullName;
                var outside = Directory.CreateDirectory(Path.Combine(root, "Outside checkbox format")).FullName;
                var pane = window.ActivePane;
                var original = pane.CurrentDirectory;
                async Task SetFormatAsync(string path, int mode, int scope)
                {
                    await pane.NavigateAsync(path);
                    Click(window, "Folder Format / Columns…");
                    var dialog = await FindDialogAsync(window, "Folder Format / Columns");
                    var combos = dialog.GetVisualDescendants().OfType<ComboBox>().ToArray();
                    combos.Single(control => control.Name == "FolderFormatScope").SelectedIndex = scope;
                    combos.Single(control => control.Name == "FolderFormatCheckboxMode").SelectedIndex = mode;
                    ClickNamedButton(dialog, "FolderFormatApply");
                    await window.LastCommand;
                }
                try
                {
                    await SetFormatAsync(parent, mode: 1, scope: 2);
                    Assert(pane.CheckboxMode, "The saved checkbox rule did not apply to its folder.");
                    await pane.NavigateAsync(inherited);
                    Assert(pane.CheckboxMode, "The child did not inherit the parent checkbox setting.");
                    await SetFormatAsync(exception, mode: 2, scope: 1);
                    Assert(!pane.CheckboxMode, "An explicit Off rule did not override the inherited checkbox setting.");
                    await pane.NavigateAsync(outside);
                    Assert(!pane.CheckboxMode, "Checkbox Mode leaked into an unrelated folder.");
                    await pane.NavigateAsync(inherited);
                    Assert(pane.CheckboxMode, "The inherited checkbox setting did not return on navigation.");
                    window.Dispatch("lock-format"); await window.LastCommand;
                    await pane.NavigateAsync(outside);
                    Assert(pane.CheckboxMode && pane.IsFormatLocked, "The format lock did not carry the checkbox setting.");
                    window.Dispatch("lock-format"); await window.LastCommand;
                    Assert(!pane.CheckboxMode && !pane.IsFormatLocked, "Unlocking did not restore the outside folder setting.");
                    await window.SaveSettingsAsync();
                    var rules = new SettingsStore(state).Load().FolderColumnRules;
                    Assert(rules.Any(rule => rule.Path == parent && rule is { IncludeSubfolders: true, CheckboxMode: true }) &&
                        rules.Any(rule => rule.Path == exception && rule.CheckboxMode == false), "Checkbox rules were not saved.");
                }
                finally { await pane.NavigateAsync(original); }
            });
            await CheckAsync("Named Lister layouts save, restore, replace, rename, delete, and roll back safely", async () =>
            {
                if (window.ActivePaneIndex != 0) { window.Dispatch("switch-pane"); await window.LastCommand; }
                if (window.SplitOrientation != "Horizontal") { window.Dispatch("horizontal"); await window.LastCommand; }
                if (window.TreeVisible) { window.Dispatch("tree"); await window.LastCommand; }
                if (!window.ViewerVisible) { window.Dispatch("viewer"); await window.LastCommand; }
                DragSplitter(window, "ViewerSplitter", new Vector(-40, 0));
                await window.SaveSettingsAsync();
                var savedViewerWidth = new SettingsStore(state).Load().ViewerWidth;
                Assert(savedViewerWidth is not null, "The Lister fixture could not establish a custom Viewer width.");
                if (window.ViewerVisible) { window.Dispatch("viewer"); await window.LastCommand; }
                window.Width = 1320; window.Height = 760;

                await window.ActivePane.AddTabAsync(Path.Combine(left, "Design"));
                var viewMode = window.ActivePane.GetVisualDescendants().OfType<ComboBox>().Single();
                viewMode.SelectedItem = "Tiles";
                Dispatcher.UIThread.RunJobs();
                window.Dispatch("lock-tab"); await window.LastCommand;
                Assert(window.ActivePane.CurrentViewMode == "Tiles" && window.ActivePane.CurrentTabLocked,
                    "The saved source tab did not enter the intended view/lock state.");

                window.Dispatch("folder-format");
                var format = await FindDialogAsync(window, "Folder Format / Columns");
                var pathVisible = format.GetVisualDescendants().OfType<CheckBox>().Single(control => control.Name == "FolderFormatVisible_Path");
                pathVisible.IsChecked = true;
                pathVisible.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                MoveFolderFormatColumnToSecond(format, "Path");
                format.GetVisualDescendants().OfType<TextBox>().Single(control => control.Name == "FolderFormatWidth_Path").Text = "240";
                Dispatcher.UIThread.RunJobs();
                if (output is not null)
                {
                    await Task.Delay(60);
                    SaveFrame(format, Path.Combine(output, "opusmac-folder-format-dialog.png"), true);
                }
                Click(format, "Apply"); await window.LastCommand;
                Assert(window.ActivePane.VisibleColumnIds.Take(2).SequenceEqual(new[] { "Name", "Path" }),
                    "Folder Format did not apply the requested source column order.");
                var sourcePathColumn = window.ActivePane.CurrentColumnLayout.Single(column => column.Id == "Path");
                Assert(sourcePathColumn.Visible && Math.Abs(sourcePathColumn.Width - 240) < 1,
                    $"Folder Format did not retain the requested Path width (actual {sourcePathColumn.Width}, mode {sourcePathColumn.WidthMode}, visible {sourcePathColumn.Visible}).");
                viewMode.SelectedItem = "Details"; Dispatcher.UIThread.RunJobs();
                var detailsColumns = window.ActivePane.VisibleColumnIds.ToArray();
                viewMode.SelectedItem = "Tiles"; Dispatcher.UIThread.RunJobs();
                Assert(window.ActivePane.CurrentViewMode == "Tiles" && window.ActivePane.VisibleColumnIds.SequenceEqual(detailsColumns),
                    "Tiles did not retain the Details column layout for this tab.");

                window.Dispatch("switch-pane"); await window.LastCommand;
                await window.ActivePane.AddTabAsync(Path.Combine(right, "Archive"));
                window.Dispatch("folder-format");
                format = await FindDialogAsync(window, "Folder Format / Columns");
                var createdVisible = format.GetVisualDescendants().OfType<CheckBox>().Single(control => control.Name == "FolderFormatVisible_Created");
                createdVisible.IsChecked = true;
                createdVisible.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                var sizeVisible = format.GetVisualDescendants().OfType<CheckBox>().Single(control => control.Name == "FolderFormatVisible_Size");
                sizeVisible.IsChecked = false;
                sizeVisible.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                MoveFolderFormatColumnToSecond(format, "Created");
                format.GetVisualDescendants().OfType<TextBox>().Single(control => control.Name == "FolderFormatWidth_Created").Text = "180";
                Dispatcher.UIThread.RunJobs();
                Click(format, "Apply"); await window.LastCommand;
                Assert(window.ActivePane.VisibleColumnIds.Take(2).SequenceEqual(new[] { "Name", "Created" }) &&
                       !window.ActivePane.CurrentColumnLayout.Single(column => column.Id == "Size").Visible,
                    "Destination folder format did not apply independently from the source tab.");

                DragSplitter(window, "FilePaneSplitter", new Vector(0, 45));
                window.Dispatch("save-lister-layout");
                var save = await FindDialogAsync(window, "Save Lister Layout");
                save.GetVisualDescendants().OfType<TextBox>().Single().Text = "Editing";
                Click(save, "Save Layout"); await window.LastCommand;

                var stored = new SettingsStore(state).Load();
                var editing = stored.SavedListers.Single(layout => layout.Name == "Editing");
                Assert(editing.ViewerWidth == savedViewerWidth, "The saved Lister lost the hidden Viewer's custom width.");
                Assert(editing.StackedPaneShare > .5, "The saved Lister lost its stacked divider position.");
                Assert(editing.DualPane && editing.SplitOrientation == "Horizontal" && !editing.ShowTree && !editing.ShowViewer,
                    "The saved Lister did not preserve the window layout switches.");
                Assert(editing.ActivePane == 1 && editing.Panes[0].Tabs.Count == 2 && editing.Panes[1].Tabs.Count == 2,
                    "The saved Lister did not preserve both pane/tab states.");
                Assert(editing.Panes[0].Tabs[1].Path == Path.Combine(left, "Design") &&
                       editing.Panes[0].Tabs[1].ViewMode == "Tiles" && editing.Panes[0].Tabs[1].Locked,
                    "The saved Lister did not preserve the source tab path/view/lock state.");
                Assert(editing.Panes[1].Tabs[1].Path == Path.Combine(right, "Archive"),
                    "The saved Lister did not preserve the destination tab path.");
                Assert(editing.Panes[0].Tabs[1].Columns.Take(2).Select(column => column.Id).SequenceEqual(new[] { "Name", "Path" }) &&
                       editing.Panes[0].Tabs[1].Columns.Single(column => column.Id == "Path").Visible,
                    "The saved Lister did not preserve the source folder format.");
                Assert(editing.Panes[1].Tabs[1].Columns.Take(2).Select(column => column.Id).SequenceEqual(new[] { "Name", "Created" }) &&
                       !editing.Panes[1].Tabs[1].Columns.Single(column => column.Id == "Size").Visible,
                    "The saved Lister did not preserve the destination folder format.");

                await window.ActivePane.ApplySnapshotAsync(new PaneSettings { Tabs = [new TabSettings { Path = right }], ActiveTab = 0 });
                window.Dispatch("switch-pane"); await window.LastCommand;
                await window.ActivePane.ApplySnapshotAsync(new PaneSettings { Tabs = [new TabSettings { Path = left }], ActiveTab = 0 });
                window.Dispatch("vertical"); await window.LastCommand;
                if (!window.TreeVisible) { window.Dispatch("tree"); await window.LastCommand; }
                if (!window.ViewerVisible) { window.Dispatch("viewer"); await window.LastCommand; }

                window.Dispatch("lister-layouts");
                var manager = await FindDialogAsync(window, "Lister Layouts");
                var layouts = manager.GetVisualDescendants().OfType<DataGrid>().Single(grid => grid.Name == "ListerLayoutsList");
                layouts.SelectedIndex = 0; Dispatcher.UIThread.RunJobs();
                Click(manager, "Load"); await window.LastCommand;

                Assert(window.DualPaneVisible && window.SplitOrientation == "Horizontal" && !window.TreeVisible && !window.ViewerVisible,
                    "Loading the saved Lister did not restore the window layout switches.");
                Assert(window.ActivePaneIndex == 1 && window.OtherPane.TabCount == 2 && window.ActivePane.TabCount == 2,
                    "Loading the saved Lister did not restore active pane and tab counts.");
                Assert(window.OtherPane.CurrentDirectory == Path.Combine(left, "Design") &&
                       window.OtherPane.CurrentViewMode == "Tiles" && window.OtherPane.CurrentTabLocked,
                    "Loading the saved Lister did not restore the source tab state.");
                Assert(window.ActivePane.CurrentDirectory == Path.Combine(right, "Archive"),
                    "Loading the saved Lister did not restore the destination tab.");
                Assert(window.OtherPane.VisibleColumnIds.Take(2).SequenceEqual(new[] { "Name", "Path" }),
                    "Loading the saved Lister did not restore the source column order.");
                Assert(window.ActivePane.VisibleColumnIds.Take(2).SequenceEqual(new[] { "Name", "Created" }) &&
                       !window.ActivePane.CurrentColumnLayout.Single(column => column.Id == "Size").Visible,
                    "Loading the saved Lister did not restore the destination column visibility.");

                window.Dispatch("lister-layouts");
                manager = await FindDialogAsync(window, "Lister Layouts");
                layouts = manager.GetVisualDescendants().OfType<DataGrid>().Single(grid => grid.Name == "ListerLayoutsList");
                layouts.SelectedIndex = 0; Dispatcher.UIThread.RunJobs();
                Click(manager, "Rename…");
                var rename = await FindDialogAsync(manager, "Rename Lister Layout");
                rename.GetVisualDescendants().OfType<TextBox>().Single().Text = "Editing Prime";
                Click(rename, "Rename");
                await WaitAsync(() => layouts.ItemsSource.Cast<SavedLister>().Any(layout => layout.Name == "Editing Prime") &&
                    FindButton(manager, "Close")?.IsEnabled == true);
                Click(manager, "Close"); await window.LastCommand;
                Assert(new SettingsStore(state).Load().SavedListers.Single().Name == "Editing Prime",
                    "Renaming the saved Lister did not persist.");

                window.Dispatch("vertical"); await window.LastCommand;
                if (!window.TreeVisible) { window.Dispatch("tree"); await window.LastCommand; }
                if (!window.ViewerVisible) { window.Dispatch("viewer"); await window.LastCommand; }
                window.Dispatch("save-lister-layout");
                save = await FindDialogAsync(window, "Save Lister Layout");
                save.GetVisualDescendants().OfType<TextBox>().Single().Text = "Editing Prime";
                Click(save, "Save Layout");
                var replace = await FindDialogAsync(window, "Replace Lister Layout");
                Click(replace, "Replace Layout"); await window.LastCommand;
                stored = new SettingsStore(state).Load();
                editing = stored.SavedListers.Single();
                Assert(editing.Name == "Editing Prime" && editing.SplitOrientation == "Vertical" && editing.ShowTree && editing.ShowViewer,
                    "Replacing the saved Lister did not persist the current layout.");

                var beforeFailure = window.ActivePane.Capture();
                var beforeFailurePath = window.ActivePane.CurrentDirectory;
                var broken = new PaneSettings
                {
                    Tabs = [new TabSettings { Path = Path.Combine(root, "Missing Lister Folder"), ViewMode = "Tiles" }],
                    ActiveTab = 0
                };
                var rejected = false;
                try { await window.ActivePane.ApplySnapshotAsync(broken); }
                catch (Exception error) when (error is IOException or DirectoryNotFoundException or UnauthorizedAccessException)
                {
                    rejected = true;
                }
                Assert(rejected, "An unreadable saved Lister pane unexpectedly loaded.");
                var afterFailure = window.ActivePane.Capture();
                Assert(window.ActivePane.CurrentDirectory == beforeFailurePath && afterFailure.ActiveTab == beforeFailure.ActiveTab &&
                       afterFailure.Tabs.Count == beforeFailure.Tabs.Count &&
                       afterFailure.Tabs.Zip(beforeFailure.Tabs).All(pair => pair.First.Path == pair.Second.Path && pair.First.ViewMode == pair.Second.ViewMode &&
                           pair.First.SortColumn == pair.Second.SortColumn && pair.First.Descending == pair.Second.Descending && pair.First.Locked == pair.Second.Locked &&
                           ColumnLayoutsMatch(pair.First.Columns, pair.Second.Columns)),
                    "A failed saved Lister pane restore did not roll back the previous tab state.");

                window.Dispatch("lister-layouts");
                manager = await FindDialogAsync(window, "Lister Layouts");
                layouts = manager.GetVisualDescendants().OfType<DataGrid>().Single(grid => grid.Name == "ListerLayoutsList");
                layouts.SelectedIndex = 0; Dispatcher.UIThread.RunJobs();
                Click(manager, "Delete…");
                var delete = await FindDialogAsync(manager, "Delete Lister Layout");
                Click(delete, "Delete Layout");
                await WaitAsync(() => !layouts.ItemsSource.Cast<SavedLister>().Any() && FindButton(manager, "Close")?.IsEnabled == true);
                Click(manager, "Close"); await window.LastCommand;
                Assert(new SettingsStore(state).Load().SavedListers.Count == 0, "Deleting the saved Lister did not persist.");
            });
            var installedListarySource = Environment.GetEnvironmentVariable("SPIFFY_LISTARY_READ_ONLY_SOURCE");
            if (!string.IsNullOrWhiteSpace(installedListarySource))
            {
                await CheckAsync("Live installed Listary agent readiness and exact task-owned result", async () =>
                {
                    installedListarySource = Path.GetFullPath(installedListarySource);
                    Assert(File.Exists(installedListarySource), "The task-owned installed-Listary source is missing.");
                    using var client = new ListaryClient();
                    var descriptorPath = Path.Combine(client.StateDirectory, "integration-endpoint-v1.json");
                    using var descriptor = JsonDocument.Parse(await File.ReadAllTextAsync(descriptorPath));
                    var endpoint = descriptor.RootElement;
                    var endpointProcessId = endpoint.GetProperty("process_id").GetInt32();
                    var endpointAddress = endpoint.GetProperty("address").GetString();
                    var readinessTimer = Stopwatch.StartNew();
                    var readiness = await client.GetIndexReadinessAsync();
                    readinessTimer.Stop();
                    Assert(readiness.IsAvailable,
                        $"The installed Listary agent did not report usable indexed roots: {readiness.ErrorCode} {readiness.Message}");

                    var query = Path.GetFileName(installedListarySource);
                    var searchTimer = Stopwatch.StartNew();
                    var page = await client.SearchAsync(query, limit: 100);
                    searchTimer.Stop();
                    var matches = page.Results.Where(result => string.Equals(
                        Path.TrimEndingDirectorySeparator(result.Entry.FullPath),
                        Path.TrimEndingDirectorySeparator(installedListarySource),
                        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)).ToArray();
                    Assert(matches.Length == 1,
                        $"The installed Listary agent returned {matches.Length} exact results for the task-owned file; total matches={page.TotalMatches}, rejected={page.RejectedResults}.");
                    var identity = matches[0];
                    var nativeIdentity = ListaryClient.ReadNativeItemIdentity(installedListarySource);
                    Assert(!string.IsNullOrWhiteSpace(nativeIdentity) &&
                        string.Equals(identity.StableFileId, nativeIdentity, StringComparison.Ordinal),
                        "The installed Listary result did not retain the current native file identity.");
                    var revalidateTimer = Stopwatch.StartNew();
                    var revalidated = await client.RevalidateAsync(identity);
                    revalidateTimer.Stop();
                    Assert(string.Equals(revalidated.FullPath, installedListarySource,
                            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal) &&
                        revalidated.Length == new FileInfo(installedListarySource).Length,
                        "The installed Listary result did not revalidate to the exact current task-owned file.");

                    // Measure repeated warm requests through the same authenticated
                    // production client without changing indexed roots or the catalog.
                    var warmSearchMs = new List<double>(4);
                    for (var sample = 0; sample < 4; sample++)
                    {
                        var warmTimer = Stopwatch.StartNew();
                        var warmPage = await client.SearchAsync(query, limit: 100);
                        warmTimer.Stop();
                        var exact = warmPage.Results.Where(result => string.Equals(
                            Path.TrimEndingDirectorySeparator(result.Entry.FullPath),
                            Path.TrimEndingDirectorySeparator(installedListarySource),
                            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)).ToArray();
                        Assert(exact.Length == 1 && warmPage.RejectedResults == 0 &&
                               string.Equals(exact[0].StableFileId, identity.StableFileId, StringComparison.Ordinal),
                            $"Warm Listary search {sample + 1} changed exact result identity or rejected results.");
                        warmSearchMs.Add(warmTimer.Elapsed.TotalMilliseconds);
                    }

                    if (output is not null)
                        await File.WriteAllTextAsync(Path.Combine(output, "installed-listary-read-only-receipt.json"),
                            JsonSerializer.Serialize(new
                            {
                                schema = "opusmac_installed_listary_read_only_v1",
                                state_directory = client.StateDirectory,
                                endpoint_process_id = endpointProcessId,
                                endpoint_address = endpointAddress,
                                endpoint_identity = page.EndpointIdentity,
                                readiness = readiness.Message,
                                readiness_ms = readinessTimer.Elapsed.TotalMilliseconds,
                                query,
                                total_matches = page.TotalMatches,
                                rejected_results = page.RejectedResults,
                                exact_result_count = matches.Length,
                                item_id = identity.ItemId,
                                root_id = identity.RootId,
                                stable_file_id = identity.StableFileId,
                                native_file_id = nativeIdentity,
                                path = installedListarySource,
                                size = revalidated.Length,
                                sha256 = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(installedListarySource))),
                                search_ms = searchTimer.Elapsed.TotalMilliseconds,
                                warm_search_ms = warmSearchMs,
                                warm_search_median_ms = warmSearchMs.Order().ElementAt(1),
                                revalidate_ms = revalidateTimer.Elapsed.TotalMilliseconds,
                                read_only = true,
                                test_mode = "Windows .NET 8 headless OpusMac UI diagnostic calling the installed authenticated Listary agent; no root or index mutation"
                            }, new JsonSerializerOptions { WriteIndented = true }));
                });
            }
            var liveSource = Environment.GetEnvironmentVariable("SPIFFY_LISTARY_QUALIFICATION_SOURCE");
            var liveDestination = Environment.GetEnvironmentVariable("SPIFFY_LISTARY_QUALIFICATION_DESTINATION");
            var liveStaleSource = Environment.GetEnvironmentVariable("SPIFFY_LISTARY_QUALIFICATION_STALE_SOURCE");
            if (!string.IsNullOrWhiteSpace(liveSource) && !string.IsNullOrWhiteSpace(liveDestination))
            {
                await CheckAsync("Live Listary result navigates Directory Opus, copies, undoes, and survives reopen", async () =>
                {
                    liveSource = Path.GetFullPath(liveSource);
                    liveDestination = Path.GetFullPath(liveDestination);
                    Assert(File.Exists(liveSource), "The live Listary qualification source is missing.");
                    Assert(Directory.Exists(liveDestination), "The live Listary qualification destination is missing.");
                    var destinationCopy = Path.Combine(liveDestination, Path.GetFileName(liveSource));
                    Assert(!File.Exists(destinationCopy), "The live Listary qualification destination is not clean.");

                    async Task<(ListarySearchResult Identity, long ElapsedMs)> SearchExactAsync(string exactPath)
                    {
                        var queryBox = window.GetVisualDescendants().OfType<TextBox>().First(box => box.Name == "FileSearch");
                        var scopeBox = window.GetVisualDescendants().OfType<ComboBox>().First(box => box.Name == "SearchScope");
                        scopeBox.SelectedIndex = 2;
                        queryBox.Text = Path.GetFileName(Path.TrimEndingDirectorySeparator(exactPath));
                        var timer = Stopwatch.StartNew();
                        Click(window, "Find");
                        await window.LastCommand;
                        await WaitAsync(() => window.ActivePane.VisibleEntries.Any(entry =>
                            string.Equals(Path.TrimEndingDirectorySeparator(entry.FullPath),
                                Path.TrimEndingDirectorySeparator(exactPath),
                                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)));
                        timer.Stop();
                        var entry = window.ActivePane.VisibleEntries.Single(entry =>
                            string.Equals(Path.TrimEndingDirectorySeparator(entry.FullPath),
                                Path.TrimEndingDirectorySeparator(exactPath),
                                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal));
                        var identity = window.ActivePane.IndexedIdentity(entry)
                            ?? throw new InvalidOperationException("The live result lost its Listary identity.");
                        window.ActivePane.SelectPaths([entry.FullPath]);
                        return (identity, timer.ElapsedMilliseconds);
                    }

                    var (destinationIdentity, destinationSearchMs) = await SearchExactAsync(liveDestination);
                    Assert(destinationIdentity.Entry.IsDirectory, "The qualification destination was not indexed as a folder.");
                    var navigationTimer = Stopwatch.StartNew();
                    window.Dispatch("open");
                    await window.LastCommand;
                    await WaitAsync(() => string.Equals(
                        Path.TrimEndingDirectorySeparator(window.ActivePane.CurrentDirectory),
                        Path.TrimEndingDirectorySeparator(liveDestination),
                        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal));
                    navigationTimer.Stop();
                    Assert(window.ActivePane.CurrentDirectoryIndexedIdentity is { } openedDestinationIdentity &&
                        openedDestinationIdentity.ItemId == destinationIdentity.ItemId &&
                        openedDestinationIdentity.RootId == destinationIdentity.RootId &&
                        string.Equals(openedDestinationIdentity.StableFileId, destinationIdentity.StableFileId, StringComparison.Ordinal),
                        "Indexed destination navigation did not retain its canonical Listary identity for action-time revalidation.");

                    window.Dispatch("switch-pane");
                    await window.LastCommand;
                    var (sourceIdentity, firstResultMs) = await SearchExactAsync(liveSource);
                    var actionTimer = Stopwatch.StartNew();
                    Click(window, "Copy Files");
                    await window.LastCommand;
                    actionTimer.Stop();
                    var operation = window.LastOperation ?? throw new InvalidOperationException("The copy produced no operation receipt.");
                    Assert(operation.Succeeded && !operation.Canceled, "The authorized copy did not succeed.");
                    Assert(operation.Changes.Count == 1 &&
                        string.Equals(operation.Changes[0].Source, liveSource, StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(operation.Changes[0].Destination, destinationCopy, StringComparison.OrdinalIgnoreCase),
                        "The copy receipt did not preserve the exact source and destination.");
                    Assert(File.Exists(destinationCopy), "The authorized copy is missing from the destination.");

                    var undoTimer = Stopwatch.StartNew();
                    Click(window, "Undo");
                    await window.LastCommand;
                    undoTimer.Stop();
                    Assert(!File.Exists(destinationCopy), "Undo did not remove the copied destination.");
                    var undoReceipt = window.LastOperation ?? throw new InvalidOperationException("Undo produced no receipt.");
                    Assert(undoReceipt.Succeeded, "Undo did not succeed.");

                    var operationId = operation.Id;
                    await window.CloseForDiagnosticsAsync();
                    window = new MainWindow(launch);
                    window.Show();
                    await window.InitializeAsync();
                    var reopenedHistory = window.OperationHistory.SingleOrDefault(item => item.Id == operationId)
                        ?? throw new InvalidOperationException("The operation journal lost the copied operation across reopen.");
                    Assert(!reopenedHistory.CanUndo, "The reopened journal incorrectly advertised the already-undone operation as undoable.");

                    var (reopenedSourceIdentity, reopenSearchMs) = await SearchExactAsync(liveSource);
                    Assert(reopenedSourceIdentity.ItemId == sourceIdentity.ItemId &&
                        reopenedSourceIdentity.RootId == sourceIdentity.RootId &&
                        string.Equals(reopenedSourceIdentity.StableFileId, sourceIdentity.StableFileId, StringComparison.Ordinal) &&
                        string.Equals(reopenedSourceIdentity.EndpointIdentity, sourceIdentity.EndpointIdentity, StringComparison.Ordinal),
                        "Reopen did not retain the same canonical Listary item and endpoint identity.");

                    string? staleFailure = null;
                    var staleRejectedWithoutJournal = false;
                    if (!string.IsNullOrWhiteSpace(liveStaleSource))
                    {
                        liveStaleSource = Path.GetFullPath(liveStaleSource);
                        Assert(File.Exists(liveStaleSource), "The stale-source qualification file is missing.");
                        var staleDestination = Path.Combine(liveDestination, Path.GetFileName(liveStaleSource));
                        await File.WriteAllTextAsync(staleDestination, "preexisting destination must remain");
                        if (!string.Equals(window.OtherPane.CurrentDirectory, liveDestination, StringComparison.OrdinalIgnoreCase))
                            await window.OtherPane.NavigateAsync(liveDestination);
                        var (_, _) = await SearchExactAsync(liveStaleSource);
                        var historyBefore = window.OperationHistory.Count;
                        Click(window, "Copy Files");
                        var conflict = await FindDialogAsync(window, "Existing files");
                        conflict.GetVisualDescendants().OfType<ComboBox>().Single().SelectedIndex = 2;
                        File.Delete(liveStaleSource);
                        await File.WriteAllTextAsync(liveStaleSource, "same-path replacement " + Guid.NewGuid().ToString("N"));
                        Click(conflict, "Continue");
                        await window.LastCommand;
                        staleFailure = window.StatusMessage;
                        staleRejectedWithoutJournal = window.OperationHistory.Count == historyBefore;
                        Assert(staleRejectedWithoutJournal, "A stale indexed source entered the operation journal after confirmation.");
                        Assert(await File.ReadAllTextAsync(staleDestination) == "preexisting destination must remain",
                            "A stale indexed source overwrote the destination.");
                        File.Delete(staleDestination);
                    }

                    if (output is not null)
                    {
                        var receipt = new
                        {
                            schema = "opusmac_listary_daily_workflow_v1",
                            source = liveSource,
                            destination = liveDestination,
                            destination_search_ms = destinationSearchMs,
                            first_result_ms = firstResultMs,
                            directory_navigation_ms = navigationTimer.ElapsedMilliseconds,
                            authorized_action_ms = actionTimer.ElapsedMilliseconds,
                            undo_ms = undoTimer.ElapsedMilliseconds,
                            reopen_search_ms = reopenSearchMs,
                            source_identity = new
                            {
                                sourceIdentity.ItemId,
                                sourceIdentity.RootId,
                                sourceIdentity.StableFileId,
                                sourceIdentity.EndpointIdentity,
                                sourceIdentity.IndexedSize,
                                sourceIdentity.IndexedModifiedUnixMilliseconds
                            },
                            destination_identity = new
                            {
                                destinationIdentity.ItemId,
                                destinationIdentity.RootId,
                                destinationIdentity.StableFileId,
                                destinationIdentity.EndpointIdentity
                            },
                            operation = new
                            {
                                operation.Id,
                                operation.Succeeded,
                                operation.Canceled,
                                changes = operation.Changes
                            },
                            undo = new { undoReceipt.Id, undoReceipt.Succeeded, undoReceipt.Canceled, changes = undoReceipt.Changes },
                            reopened_history = reopenedHistory,
                            stale_source = liveStaleSource,
                            stale_rejected_without_journal = staleRejectedWithoutJournal,
                            stale_failure = staleFailure,
                            utc = DateTimeOffset.UtcNow
                        };
                        await File.WriteAllTextAsync(
                            Path.Combine(output, "listary-daily-workflow.json"),
                            JsonSerializer.Serialize(receipt, new JsonSerializerOptions { WriteIndented = true }));
                    }
                });
            }
            var liveTrashSource = Environment.GetEnvironmentVariable("SPIFFY_LISTARY_TRASH_QUALIFICATION_SOURCE");
            var liveTrashStaleSource = Environment.GetEnvironmentVariable("SPIFFY_LISTARY_TRASH_QUALIFICATION_STALE_SOURCE");
            if (!string.IsNullOrWhiteSpace(liveTrashSource) && !string.IsNullOrWhiteSpace(liveTrashStaleSource))
            {
                await CheckAsync("Live Listary Trash revalidates after confirmation, survives restart, and undoes", async () =>
                {
                    liveTrashSource = Path.GetFullPath(liveTrashSource);
                    liveTrashStaleSource = Path.GetFullPath(liveTrashStaleSource);
                    Assert(File.Exists(liveTrashSource), "The live Trash qualification source is missing.");
                    Assert(File.Exists(liveTrashStaleSource), "The live stale Trash qualification source is missing.");
                    window.Width = 1400;
                    window.Height = 860;
                    Application.Current!.RequestedThemeVariant = ThemeVariant.Dark;
                    await Task.Delay(80);
                    var sourceBytes = await File.ReadAllBytesAsync(liveTrashSource);
                    var staleReplacementBytes = Encoding.UTF8.GetBytes("same-path Trash replacement " + Guid.NewGuid().ToString("N"));

                    async Task<(ListarySearchResult Identity, long ElapsedMs)> SearchExactAsync(string exactPath)
                    {
                        var queryBox = window.GetVisualDescendants().OfType<TextBox>().First(box => box.Name == "FileSearch");
                        var scopeBox = window.GetVisualDescendants().OfType<ComboBox>().First(box => box.Name == "SearchScope");
                        scopeBox.SelectedIndex = 2;
                        queryBox.Text = Path.GetFileName(Path.TrimEndingDirectorySeparator(exactPath));
                        var timer = Stopwatch.StartNew();
                        Click(window, "Find");
                        await window.LastCommand;
                        await WaitAsync(() => window.ActivePane.VisibleEntries.Any(entry =>
                            string.Equals(Path.TrimEndingDirectorySeparator(entry.FullPath),
                                Path.TrimEndingDirectorySeparator(exactPath),
                                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)));
                        timer.Stop();
                        var entry = window.ActivePane.VisibleEntries.Single(entry =>
                            string.Equals(Path.TrimEndingDirectorySeparator(entry.FullPath),
                                Path.TrimEndingDirectorySeparator(exactPath),
                                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal));
                        var identity = window.ActivePane.IndexedIdentity(entry)
                            ?? throw new InvalidOperationException("The live Trash result lost its Listary identity.");
                        window.ActivePane.SelectPaths([entry.FullPath]);
                        return (identity, timer.ElapsedMilliseconds);
                    }

                    async Task<ListarySearchResult> WaitForIndexedVersionAsync(string exactPath, Func<ListarySearchResult, bool> predicate)
                    {
                        using var witness = new ListaryClient();
                        var timer = Stopwatch.StartNew();
                        while (timer.Elapsed < TimeSpan.FromSeconds(12))
                        {
                            var page = await witness.SearchAsync(Path.GetFileName(Path.TrimEndingDirectorySeparator(exactPath)), 32);
                            var match = page.Results.FirstOrDefault(result =>
                                string.Equals(Path.TrimEndingDirectorySeparator(result.Entry.FullPath),
                                    Path.TrimEndingDirectorySeparator(exactPath),
                                    OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal));
                            if (match is not null && predicate(match)) return match;
                            await Task.Delay(50);
                        }
                        throw new TimeoutException("The Listary watcher did not publish the expected Trash qualification version.");
                    }

                    var (sourceIdentity, firstResultMs) = await SearchExactAsync(liveTrashSource);
                    var historyBeforeTrash = window.OperationHistory.Count;
                    var trashTimer = Stopwatch.StartNew();
                    Click(window, "Delete");
                    var trashDialog = await FindDialogAsync(window, "Move to Trash");
                    if (output is not null)
                    {
                        var confirmPath = Path.Combine(output, "opusmac-listary-trash-confirm.png");
                        Dispatcher.UIThread.RunJobs();
                        using var confirmFrame = trashDialog.CaptureRenderedFrame()
                            ?? throw new InvalidOperationException("The Trash confirmation renderer returned no bitmap.");
                        confirmFrame.Save(confirmPath);
                        Assert(new FileInfo(confirmPath).Length > 1000, "The Trash confirmation image is unexpectedly empty.");
                    }
                    Click(trashDialog, "Move to Trash");
                    await window.LastCommand;
                    trashTimer.Stop();
                    var trashReceipt = window.LastOperation ?? throw new InvalidOperationException("Trash produced no operation receipt.");
                    Assert(trashReceipt.Succeeded && !trashReceipt.Canceled, "The authorized Trash operation did not succeed.");
                    Assert(trashReceipt.Changes.Count == 1 &&
                        string.Equals(trashReceipt.Changes[0].Source, liveTrashSource, StringComparison.OrdinalIgnoreCase) &&
                        trashReceipt.Changes[0].Action == "Trash",
                        "The Trash receipt did not preserve the exact indexed source.");
                    Assert(!File.Exists(liveTrashSource), "The authorized Trash source still exists.");
                    Assert(window.OperationHistory.Count == historyBeforeTrash + 1, "Trash did not enter the canonical operation journal.");

                    var operationId = trashReceipt.Id;
                    var reopenBeforeUndoTimer = Stopwatch.StartNew();
                    await window.CloseForDiagnosticsAsync();
                    window = new MainWindow(launch);
                    window.Show();
                    await window.InitializeAsync();
                    reopenBeforeUndoTimer.Stop();
                    var reopenedTrash = window.OperationHistory.SingleOrDefault(item => item.Id == operationId)
                        ?? throw new InvalidOperationException("The Trash operation journal entry did not survive reopen.");
                    Assert(reopenedTrash.CanUndo && reopenedTrash.Status == "Succeeded",
                        "Reopened Trash history did not preserve its Undo authority.");

                    var undoTimer = Stopwatch.StartNew();
                    Click(window, "Undo");
                    await window.LastCommand;
                    undoTimer.Stop();
                    var undoReceipt = window.LastOperation ?? throw new InvalidOperationException("Trash Undo produced no receipt.");
                    Assert(undoReceipt.Succeeded && File.Exists(liveTrashSource), "Trash Undo did not restore the source.");
                    Assert((await File.ReadAllBytesAsync(liveTrashSource)).SequenceEqual(sourceBytes), "Trash Undo restored different bytes.");

                    var reopenAfterUndoTimer = Stopwatch.StartNew();
                    await window.CloseForDiagnosticsAsync();
                    window = new MainWindow(launch);
                    window.Show();
                    await window.InitializeAsync();
                    reopenAfterUndoTimer.Stop();
                    var reopenedUndone = window.OperationHistory.Single(item => item.Id == operationId);
                    Assert(!reopenedUndone.CanUndo && reopenedUndone.Status == "Undone",
                        "A second reopen did not preserve the completed Undo state.");
                    var restoredIndexed = await WaitForIndexedVersionAsync(liveTrashSource, _ => true);
                    var (_, restoredSearchMs) = await SearchExactAsync(liveTrashSource);

                    var (staleIdentity, staleSearchMs) = await SearchExactAsync(liveTrashStaleSource);
                    var historyBeforeStale = window.OperationHistory.Count;
                    Click(window, "Delete");
                    var staleDialog = await FindDialogAsync(window, "Move to Trash");
                    File.Delete(liveTrashStaleSource);
                    await File.WriteAllBytesAsync(liveTrashStaleSource, staleReplacementBytes);
                    var replacement = await WaitForIndexedVersionAsync(liveTrashStaleSource, current =>
                        current.ItemId != staleIdentity.ItemId ||
                        !string.Equals(current.StableFileId, staleIdentity.StableFileId, StringComparison.Ordinal) ||
                        current.IndexedSize != staleIdentity.IndexedSize ||
                        current.IndexedModifiedUnixMilliseconds != staleIdentity.IndexedModifiedUnixMilliseconds);
                    var staleConfirmTimer = Stopwatch.StartNew();
                    Click(staleDialog, "Move to Trash");
                    await window.LastCommand;
                    staleConfirmTimer.Stop();
                    var staleFailure = window.StatusMessage;
                    var staleRejectedWithoutJournal = window.OperationHistory.Count == historyBeforeStale;
                    var replacementSurvived = File.Exists(liveTrashStaleSource) &&
                        (await File.ReadAllBytesAsync(liveTrashStaleSource)).SequenceEqual(staleReplacementBytes);
                    if (!staleRejectedWithoutJournal || !replacementSurvived)
                    {
                        if (window.CanUndoOperation)
                        {
                            Click(window, "Undo");
                            await window.LastCommand;
                        }
                        throw new InvalidOperationException(
                            "A same-path replacement entered Trash after confirmation instead of failing stale-item authority.");
                    }

                    if (output is not null)
                    {
                        window.Width = 1400; window.Height = 860;
                        Application.Current!.RequestedThemeVariant = ThemeVariant.Dark;
                        await Task.Delay(80);
                        SaveFrame(window, Path.Combine(output, "opusmac-listary-trash-restored.png"));
                        var receipt = new
                        {
                            schema = "opusmac_listary_trash_workflow_v1",
                            source = liveTrashSource,
                            stale_source = liveTrashStaleSource,
                            first_result_ms = firstResultMs,
                            trash_confirm_to_receipt_ms = trashTimer.ElapsedMilliseconds,
                            reopen_before_undo_ms = reopenBeforeUndoTimer.ElapsedMilliseconds,
                            undo_ms = undoTimer.ElapsedMilliseconds,
                            reopen_after_undo_ms = reopenAfterUndoTimer.ElapsedMilliseconds,
                            restored_search_ms = restoredSearchMs,
                            stale_search_ms = staleSearchMs,
                            stale_confirm_to_refusal_ms = staleConfirmTimer.ElapsedMilliseconds,
                            source_identity = new
                            {
                                sourceIdentity.ItemId,
                                sourceIdentity.RootId,
                                sourceIdentity.StableFileId,
                                sourceIdentity.EndpointIdentity
                            },
                            restored_identity = new
                            {
                                restoredIndexed.ItemId,
                                restoredIndexed.RootId,
                                restoredIndexed.StableFileId,
                                restoredIndexed.EndpointIdentity
                            },
                            stale_identity = new
                            {
                                staleIdentity.ItemId,
                                staleIdentity.RootId,
                                staleIdentity.StableFileId,
                                staleIdentity.EndpointIdentity
                            },
                            replacement_identity = new
                            {
                                replacement.ItemId,
                                replacement.RootId,
                                replacement.StableFileId,
                                replacement.EndpointIdentity
                            },
                            trash = new { trashReceipt.Id, trashReceipt.Succeeded, trashReceipt.Canceled, changes = trashReceipt.Changes },
                            undo = new { undoReceipt.Id, undoReceipt.Succeeded, undoReceipt.Canceled, changes = undoReceipt.Changes },
                            reopened_before_undo = reopenedTrash,
                            reopened_after_undo = reopenedUndone,
                            stale_rejected_without_journal = staleRejectedWithoutJournal,
                            stale_replacement_survived = replacementSurvived,
                            stale_failure = staleFailure,
                            utc = DateTimeOffset.UtcNow
                        };
                        await File.WriteAllTextAsync(
                            Path.Combine(output, "listary-trash-workflow.json"),
                            JsonSerializer.Serialize(receipt, new JsonSerializerOptions { WriteIndented = true }));
                    }
                });
            }
            var liveRenameSource = Environment.GetEnvironmentVariable("SPIFFY_LISTARY_ADVANCED_RENAME_QUALIFICATION_SOURCE");
            var liveRenameStaleSource = Environment.GetEnvironmentVariable("SPIFFY_LISTARY_ADVANCED_RENAME_QUALIFICATION_STALE_SOURCE");
            if (!string.IsNullOrWhiteSpace(liveRenameSource) && !string.IsNullOrWhiteSpace(liveRenameStaleSource))
            {
                await CheckAsync("Live Listary Advanced Rename revalidates preview before apply, survives restart, and undoes", async () =>
                {
                    liveRenameSource = Path.GetFullPath(liveRenameSource);
                    liveRenameStaleSource = Path.GetFullPath(liveRenameStaleSource);
                    Assert(File.Exists(liveRenameSource), "The live Advanced Rename qualification source is missing.");
                    Assert(File.Exists(liveRenameStaleSource), "The live stale Advanced Rename qualification source is missing.");
                    window.Width = 1400;
                    window.Height = 860;
                    Application.Current!.RequestedThemeVariant = ThemeVariant.Dark;
                    await Task.Delay(80);
                    var sourceBytes = await File.ReadAllBytesAsync(liveRenameSource);
                    var staleReplacementBytes = Encoding.UTF8.GetBytes("same-path Advanced Rename replacement " + Guid.NewGuid().ToString("N"));

                    async Task<(ListarySearchResult Identity, long ElapsedMs)> SearchExactAsync(string exactPath)
                    {
                        var queryBox = window.GetVisualDescendants().OfType<TextBox>().First(box => box.Name == "FileSearch");
                        var scopeBox = window.GetVisualDescendants().OfType<ComboBox>().First(box => box.Name == "SearchScope");
                        scopeBox.SelectedIndex = 2;
                        queryBox.Text = Path.GetFileName(Path.TrimEndingDirectorySeparator(exactPath));
                        var timer = Stopwatch.StartNew();
                        Click(window, "Find");
                        await window.LastCommand;
                        await WaitAsync(() => window.ActivePane.VisibleEntries.Any(entry =>
                            string.Equals(Path.TrimEndingDirectorySeparator(entry.FullPath),
                                Path.TrimEndingDirectorySeparator(exactPath),
                                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)));
                        timer.Stop();
                        var entry = window.ActivePane.VisibleEntries.Single(entry =>
                            string.Equals(Path.TrimEndingDirectorySeparator(entry.FullPath),
                                Path.TrimEndingDirectorySeparator(exactPath),
                                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal));
                        var identity = window.ActivePane.IndexedIdentity(entry)
                            ?? throw new InvalidOperationException("The live Advanced Rename result lost its Listary identity.");
                        window.ActivePane.SelectPaths([entry.FullPath]);
                        return (identity, timer.ElapsedMilliseconds);
                    }

                    async Task<ListarySearchResult> WaitForIndexedVersionAsync(string exactPath, Func<ListarySearchResult, bool> predicate)
                    {
                        using var witness = new ListaryClient();
                        var timer = Stopwatch.StartNew();
                        while (timer.Elapsed < TimeSpan.FromSeconds(12))
                        {
                            var page = await witness.SearchAsync(Path.GetFileName(Path.TrimEndingDirectorySeparator(exactPath)), 32);
                            var match = page.Results.FirstOrDefault(result =>
                                string.Equals(Path.TrimEndingDirectorySeparator(result.Entry.FullPath),
                                    Path.TrimEndingDirectorySeparator(exactPath),
                                    OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal));
                            if (match is not null && predicate(match)) return match;
                            await Task.Delay(50);
                        }
                        throw new TimeoutException("The Listary watcher did not publish the expected Advanced Rename qualification version.");
                    }

                    // Exercise the stale human-wait boundary first so a prior close/reopen or Undo
                    // cannot accidentally supply the refusal we are trying to attribute to Advanced Rename.
                    var (staleIdentity, staleSearchMs) = await SearchExactAsync(liveRenameStaleSource);
                    var historyBeforeStale = window.OperationHistory.Count;
                    Click(window, "Rename");
                    var staleDialog = await FindDialogAsync(window, "Advanced Rename");
                    staleDialog.GetVisualDescendants().OfType<TextBox>().First(box => box.Name == "RenamePrefix").Text = "Reviewed ";
                    Click(staleDialog, "Preview");
                    await WaitAsync(() => FindButton(staleDialog, "Apply rename")?.IsEnabled == true);
                    var staleFeedback = staleDialog.GetVisualDescendants().OfType<TextBlock>()
                        .Single(block => string.Equals(block.Text, "1 item(s) will be renamed.", StringComparison.Ordinal));
                    File.Delete(liveRenameStaleSource);
                    await File.WriteAllBytesAsync(liveRenameStaleSource, staleReplacementBytes);
                    var replacement = await WaitForIndexedVersionAsync(liveRenameStaleSource, current =>
                        current.ItemId != staleIdentity.ItemId ||
                        !string.Equals(current.StableFileId, staleIdentity.StableFileId, StringComparison.Ordinal) ||
                        current.IndexedSize != staleIdentity.IndexedSize ||
                        current.IndexedModifiedUnixMilliseconds != staleIdentity.IndexedModifiedUnixMilliseconds);
                    var staleApplyTimer = Stopwatch.StartNew();
                    string? staleFailure = null;
                    try
                    {
                        Click(staleDialog, "Apply rename");
                        await WaitAsync(() => !staleDialog.IsVisible ||
                            !string.Equals(staleFeedback.Text, "1 item(s) will be renamed.", StringComparison.Ordinal));
                        staleApplyTimer.Stop();
                        staleFailure = staleDialog.IsVisible ? staleFeedback.Text : window.StatusMessage;
                        if (output is not null && staleDialog.IsVisible)
                        {
                            await Task.Delay(80);
                            SaveFrame(staleDialog, Path.Combine(output, "opusmac-listary-advanced-rename-stale-refusal.png"), true);
                        }
                    }
                    finally
                    {
                        if (staleDialog.IsVisible)
                        {
                            Click(staleDialog, "Cancel");
                            await window.LastCommand;
                        }
                        else
                        {
                            await window.LastCommand;
                        }
                    }
                    var staleRejectedWithoutJournal = window.OperationHistory.Count == historyBeforeStale;
                    var replacementSurvived = File.Exists(liveRenameStaleSource) &&
                        (await File.ReadAllBytesAsync(liveRenameStaleSource)).SequenceEqual(staleReplacementBytes);
                    if (!staleRejectedWithoutJournal || !replacementSurvived)
                    {
                        if (window.CanUndoOperation)
                        {
                            Click(window, "Undo");
                            await window.LastCommand;
                        }
                        throw new InvalidOperationException(
                            "A same-path replacement entered Advanced Rename after preview instead of failing stale-item authority.");
                    }

                    var (sourceIdentity, firstResultMs) = await SearchExactAsync(liveRenameSource);
                    var historyBeforeRename = window.OperationHistory.Count;
                    Click(window, "Rename");
                    var renameDialog = await FindDialogAsync(window, "Advanced Rename");
                    renameDialog.GetVisualDescendants().OfType<TextBox>().First(box => box.Name == "RenamePrefix").Text = "Reviewed ";
                    Click(renameDialog, "Preview");
                    await WaitAsync(() => FindButton(renameDialog, "Apply rename")?.IsEnabled == true);
                    if (output is not null)
                    {
                        await Task.Delay(80);
                        SaveFrame(renameDialog, Path.Combine(output, "opusmac-listary-advanced-rename-preview.png"), true);
                    }
                    var renameTimer = Stopwatch.StartNew();
                    Click(renameDialog, "Apply rename");
                    await window.LastCommand;
                    renameTimer.Stop();
                    var renameReceipt = window.LastOperation ?? throw new InvalidOperationException("Advanced Rename produced no operation receipt.");
                    var renamedSource = Path.Combine(Path.GetDirectoryName(liveRenameSource)!, "Reviewed " + Path.GetFileName(liveRenameSource));
                    Assert(renameReceipt.Succeeded && !renameReceipt.Canceled, "The authorized Advanced Rename operation did not succeed.");
                    Assert(renameReceipt.Changes.Count == 1 &&
                        string.Equals(renameReceipt.Changes[0].Source, liveRenameSource, StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(renameReceipt.Changes[0].Destination, renamedSource, StringComparison.OrdinalIgnoreCase) &&
                        renameReceipt.Changes[0].Action == "Rename",
                        "The Advanced Rename receipt did not preserve the exact indexed source and reviewed destination.");
                    Assert(!File.Exists(liveRenameSource) && File.Exists(renamedSource), "Advanced Rename did not publish the reviewed destination.");
                    Assert(window.OperationHistory.Count == historyBeforeRename + 1, "Advanced Rename did not enter the canonical operation journal.");

                    var operationId = renameReceipt.Id;
                    var reopenBeforeUndoTimer = Stopwatch.StartNew();
                    await window.CloseForDiagnosticsAsync();
                    window = new MainWindow(launch);
                    Application.Current!.RequestedThemeVariant = ThemeVariant.Dark;
                    window.Show();
                    await window.InitializeAsync();
                    reopenBeforeUndoTimer.Stop();
                    var reopenedRename = window.OperationHistory.SingleOrDefault(item => item.Id == operationId)
                        ?? throw new InvalidOperationException("The Advanced Rename operation journal entry did not survive reopen.");
                    Assert(reopenedRename.CanUndo && reopenedRename.Status == "Succeeded",
                        "Reopened Advanced Rename history did not preserve its Undo authority.");

                    var undoTimer = Stopwatch.StartNew();
                    Click(window, "Undo");
                    await window.LastCommand;
                    undoTimer.Stop();
                    var undoReceipt = window.LastOperation ?? throw new InvalidOperationException("Advanced Rename Undo produced no receipt.");
                    Assert(undoReceipt.Succeeded && File.Exists(liveRenameSource) && !File.Exists(renamedSource),
                        "Advanced Rename Undo did not restore the original source path.");
                    Assert((await File.ReadAllBytesAsync(liveRenameSource)).SequenceEqual(sourceBytes),
                        "Advanced Rename Undo restored different bytes.");

                    var reopenAfterUndoTimer = Stopwatch.StartNew();
                    await window.CloseForDiagnosticsAsync();
                    window = new MainWindow(launch);
                    Application.Current!.RequestedThemeVariant = ThemeVariant.Dark;
                    window.Show();
                    await window.InitializeAsync();
                    reopenAfterUndoTimer.Stop();
                    var reopenedUndone = window.OperationHistory.Single(item => item.Id == operationId);
                    Assert(!reopenedUndone.CanUndo && reopenedUndone.Status == "Undone",
                        "A second reopen did not preserve the completed Advanced Rename Undo state.");
                    var restoredIndexed = await WaitForIndexedVersionAsync(liveRenameSource, _ => true);
                    var (_, restoredSearchMs) = await SearchExactAsync(liveRenameSource);

                    if (output is not null)
                    {
                        SaveFrame(window, Path.Combine(output, "opusmac-listary-advanced-rename-restored.png"));
                        var receipt = new
                        {
                            schema = "opusmac_listary_advanced_rename_workflow_v1",
                            source = liveRenameSource,
                            stale_source = liveRenameStaleSource,
                            first_result_ms = firstResultMs,
                            rename_apply_to_receipt_ms = renameTimer.ElapsedMilliseconds,
                            reopen_before_undo_ms = reopenBeforeUndoTimer.ElapsedMilliseconds,
                            undo_ms = undoTimer.ElapsedMilliseconds,
                            reopen_after_undo_ms = reopenAfterUndoTimer.ElapsedMilliseconds,
                            restored_search_ms = restoredSearchMs,
                            stale_search_ms = staleSearchMs,
                            stale_apply_to_refusal_ms = staleApplyTimer.ElapsedMilliseconds,
                            source_identity = new { sourceIdentity.ItemId, sourceIdentity.RootId, sourceIdentity.StableFileId, sourceIdentity.EndpointIdentity },
                            restored_identity = new { restoredIndexed.ItemId, restoredIndexed.RootId, restoredIndexed.StableFileId, restoredIndexed.EndpointIdentity },
                            stale_identity = new { staleIdentity.ItemId, staleIdentity.RootId, staleIdentity.StableFileId, staleIdentity.EndpointIdentity },
                            replacement_identity = new { replacement.ItemId, replacement.RootId, replacement.StableFileId, replacement.EndpointIdentity },
                            rename = new { renameReceipt.Id, renameReceipt.Succeeded, renameReceipt.Canceled, changes = renameReceipt.Changes },
                            undo = new { undoReceipt.Id, undoReceipt.Succeeded, undoReceipt.Canceled, changes = undoReceipt.Changes },
                            reopened_before_undo = reopenedRename,
                            reopened_after_undo = reopenedUndone,
                            stale_rejected_without_journal = staleRejectedWithoutJournal,
                            stale_replacement_survived = replacementSurvived,
                            stale_failure = staleFailure,
                            utc = DateTimeOffset.UtcNow
                        };
                        await File.WriteAllTextAsync(
                            Path.Combine(output, "listary-advanced-rename-workflow.json"),
                            JsonSerializer.Serialize(receipt, new JsonSerializerOptions { WriteIndented = true }));
                    }
                });
            }
            var liveArchiveSource = Environment.GetEnvironmentVariable("SPIFFY_LISTARY_ARCHIVE_QUALIFICATION_SOURCE");
            var liveArchiveStaleSource = Environment.GetEnvironmentVariable("SPIFFY_LISTARY_ARCHIVE_QUALIFICATION_STALE_SOURCE");
            if (!string.IsNullOrWhiteSpace(liveArchiveSource) && !string.IsNullOrWhiteSpace(liveArchiveStaleSource))
            {
                await CheckAsync("Live Listary Create ZIP revalidates after prompt and recovers through reopen", async () =>
                {
                    liveArchiveSource = Path.GetFullPath(liveArchiveSource);
                    liveArchiveStaleSource = Path.GetFullPath(liveArchiveStaleSource);
                    Assert(File.Exists(liveArchiveSource), "The live Create ZIP qualification source is missing.");
                    Assert(File.Exists(liveArchiveStaleSource), "The live stale Create ZIP qualification source is missing.");
                    window.Width = 1400;
                    window.Height = 860;
                    Application.Current!.RequestedThemeVariant = ThemeVariant.Dark;
                    await Task.Delay(80);
                    var sourceBytes = await File.ReadAllBytesAsync(liveArchiveSource);
                    var staleReplacementBytes = Encoding.UTF8.GetBytes("same-path Create ZIP replacement " + Guid.NewGuid().ToString("N"));
                    var proofDirectory = Path.GetDirectoryName(liveArchiveSource)!;
                    var staleTarget = Path.Combine(proofDirectory, "stale Create ZIP proof Ω.zip");
                    var acceptedTarget = Path.Combine(proofDirectory, "accepted Create ZIP proof Ω.zip");
                    if (File.Exists(staleTarget)) File.Delete(staleTarget);
                    if (File.Exists(acceptedTarget)) File.Delete(acceptedTarget);

                    async Task<(ListarySearchResult Identity, long ElapsedMs)> SearchExactAsync(string exactPath)
                    {
                        var queryBox = window.GetVisualDescendants().OfType<TextBox>().First(box => box.Name == "FileSearch");
                        var scopeBox = window.GetVisualDescendants().OfType<ComboBox>().First(box => box.Name == "SearchScope");
                        scopeBox.SelectedIndex = 2;
                        queryBox.Text = Path.GetFileName(Path.TrimEndingDirectorySeparator(exactPath));
                        var timer = Stopwatch.StartNew();
                        Click(window, "Find");
                        await window.LastCommand;
                        await WaitAsync(() => window.ActivePane.VisibleEntries.Any(entry =>
                            string.Equals(Path.TrimEndingDirectorySeparator(entry.FullPath),
                                Path.TrimEndingDirectorySeparator(exactPath),
                                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)));
                        timer.Stop();
                        var entry = window.ActivePane.VisibleEntries.Single(entry =>
                            string.Equals(Path.TrimEndingDirectorySeparator(entry.FullPath),
                                Path.TrimEndingDirectorySeparator(exactPath),
                                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal));
                        var identity = window.ActivePane.IndexedIdentity(entry)
                            ?? throw new InvalidOperationException("The live Create ZIP result lost its Listary identity.");
                        window.ActivePane.SelectPaths([entry.FullPath]);
                        return (identity, timer.ElapsedMilliseconds);
                    }

                    async Task<ListarySearchResult> WaitForIndexedVersionAsync(string exactPath, Func<ListarySearchResult, bool> predicate)
                    {
                        using var witness = new ListaryClient();
                        var timer = Stopwatch.StartNew();
                        while (timer.Elapsed < TimeSpan.FromSeconds(12))
                        {
                            var page = await witness.SearchAsync(Path.GetFileName(Path.TrimEndingDirectorySeparator(exactPath)), 32);
                            var match = page.Results.FirstOrDefault(result =>
                                string.Equals(Path.TrimEndingDirectorySeparator(result.Entry.FullPath),
                                    Path.TrimEndingDirectorySeparator(exactPath),
                                    OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal));
                            if (match is not null && predicate(match)) return match;
                            await Task.Delay(50);
                        }
                        throw new TimeoutException("The Listary watcher did not publish the expected Create ZIP qualification version.");
                    }

                    var (staleIdentity, staleSearchMs) = await SearchExactAsync(liveArchiveStaleSource);
                    Click(window, "Archive");
                    var stalePrompt = await FindDialogAsync(window, "Create ZIP");
                    stalePrompt.GetVisualDescendants().OfType<TextBox>().First().Text = staleTarget;
                    if (output is not null)
                    {
                        await Task.Delay(80);
                        var promptPath = Path.Combine(output, "opusmac-listary-create-zip-prompt.png");
                        Dispatcher.UIThread.RunJobs();
                        using var promptFrame = stalePrompt.CaptureRenderedFrame()
                            ?? throw new InvalidOperationException("The Create ZIP prompt renderer returned no bitmap.");
                        promptFrame.Save(promptPath);
                        Assert(new FileInfo(promptPath).Length > 1000, "The Create ZIP prompt image is unexpectedly empty.");
                    }
                    File.Delete(liveArchiveStaleSource);
                    await File.WriteAllBytesAsync(liveArchiveStaleSource, staleReplacementBytes);
                    var replacement = await WaitForIndexedVersionAsync(liveArchiveStaleSource, current =>
                        current.ItemId != staleIdentity.ItemId ||
                        !string.Equals(current.StableFileId, staleIdentity.StableFileId, StringComparison.Ordinal) ||
                        current.IndexedSize != staleIdentity.IndexedSize ||
                        current.IndexedModifiedUnixMilliseconds != staleIdentity.IndexedModifiedUnixMilliseconds);
                    var staleConfirmTimer = Stopwatch.StartNew();
                    Click(stalePrompt, "Create ZIP");
                    await window.LastCommand;
                    staleConfirmTimer.Stop();
                    var staleFailure = window.StatusMessage;
                    var staleRejectedBeforeOutput = !File.Exists(staleTarget);
                    var replacementSurvived = File.Exists(liveArchiveStaleSource) &&
                        (await File.ReadAllBytesAsync(liveArchiveStaleSource)).SequenceEqual(staleReplacementBytes);
                    if (!staleRejectedBeforeOutput || !replacementSurvived)
                    {
                        if (File.Exists(staleTarget)) File.Delete(staleTarget);
                        throw new InvalidOperationException(
                            "A same-path replacement entered Create ZIP after the destination prompt instead of failing stale-item authority.");
                    }
                    if (output is not null)
                    {
                        await Task.Delay(80);
                        SaveFrame(window, Path.Combine(output, "opusmac-listary-create-zip-stale-refusal.png"));
                    }

                    var (sourceIdentity, firstResultMs) = await SearchExactAsync(liveArchiveSource);
                    var createTimer = Stopwatch.StartNew();
                    Click(window, "Archive");
                    var createPrompt = await FindDialogAsync(window, "Create ZIP");
                    createPrompt.GetVisualDescendants().OfType<TextBox>().First().Text = acceptedTarget;
                    Click(createPrompt, "Create ZIP");
                    await window.LastCommand;
                    createTimer.Stop();
                    Assert(File.Exists(acceptedTarget), "The authorized Create ZIP output is missing.");
                    byte[] archivedBytes;
                    using (var zip = ZipFile.OpenRead(acceptedTarget))
                    {
                        var entry = zip.Entries.Single(item => string.Equals(item.FullName, Path.GetFileName(liveArchiveSource), StringComparison.Ordinal));
                        await using var stream = entry.Open();
                        using var memory = new MemoryStream();
                        await stream.CopyToAsync(memory);
                        archivedBytes = memory.ToArray();
                    }
                    Assert(archivedBytes.SequenceEqual(sourceBytes), "The authorized ZIP contains different source bytes.");

                    var reopenTimer = Stopwatch.StartNew();
                    await window.CloseForDiagnosticsAsync();
                    window = new MainWindow(launch);
                    Application.Current!.RequestedThemeVariant = ThemeVariant.Dark;
                    window.Show();
                    await window.InitializeAsync();
                    reopenTimer.Stop();
                    Assert(File.Exists(acceptedTarget), "Reopening the task-owned window changed the committed ZIP output.");
                    var (reopenedIdentity, reopenSearchMs) = await SearchExactAsync(liveArchiveSource);
                    Assert(reopenedIdentity.ItemId == sourceIdentity.ItemId &&
                        reopenedIdentity.RootId == sourceIdentity.RootId &&
                        string.Equals(reopenedIdentity.StableFileId, sourceIdentity.StableFileId, StringComparison.Ordinal) &&
                        string.Equals(reopenedIdentity.EndpointIdentity, sourceIdentity.EndpointIdentity, StringComparison.Ordinal),
                        "Create ZIP reopen did not retain the same canonical Listary source identity.");

                    if (output is not null)
                    {
                        SaveFrame(window, Path.Combine(output, "opusmac-listary-create-zip-restored.png"));
                        var receipt = new
                        {
                            schema = "opusmac_listary_create_zip_workflow_v1",
                            source = liveArchiveSource,
                            stale_source = liveArchiveStaleSource,
                            accepted_target = acceptedTarget,
                            stale_target = staleTarget,
                            first_result_ms = firstResultMs,
                            create_zip_ms = createTimer.ElapsedMilliseconds,
                            reopen_ms = reopenTimer.ElapsedMilliseconds,
                            reopen_search_ms = reopenSearchMs,
                            stale_search_ms = staleSearchMs,
                            stale_confirm_to_refusal_ms = staleConfirmTimer.ElapsedMilliseconds,
                            source_identity = new { sourceIdentity.ItemId, sourceIdentity.RootId, sourceIdentity.StableFileId, sourceIdentity.EndpointIdentity },
                            reopened_identity = new { reopenedIdentity.ItemId, reopenedIdentity.RootId, reopenedIdentity.StableFileId, reopenedIdentity.EndpointIdentity },
                            stale_identity = new { staleIdentity.ItemId, staleIdentity.RootId, staleIdentity.StableFileId, staleIdentity.EndpointIdentity },
                            replacement_identity = new { replacement.ItemId, replacement.RootId, replacement.StableFileId, replacement.EndpointIdentity },
                            stale_rejected_before_output = staleRejectedBeforeOutput,
                            stale_replacement_survived = replacementSurvived,
                            stale_failure = staleFailure,
                            archived_bytes_match = archivedBytes.SequenceEqual(sourceBytes),
                            utc = DateTimeOffset.UtcNow
                        };
                        await File.WriteAllTextAsync(
                            Path.Combine(output, "listary-create-zip-workflow.json"),
                            JsonSerializer.Serialize(receipt, new JsonSerializerOptions { WriteIndented = true }));
                    }
                });
            }
            var liveExtractSource = Environment.GetEnvironmentVariable("SPIFFY_LISTARY_EXTRACT_QUALIFICATION_SOURCE");
            var liveExtractStaleSource = Environment.GetEnvironmentVariable("SPIFFY_LISTARY_EXTRACT_QUALIFICATION_STALE_SOURCE");
            if (!string.IsNullOrWhiteSpace(liveExtractSource) && !string.IsNullOrWhiteSpace(liveExtractStaleSource))
            {
                await CheckAsync("Live Listary ZIP extraction revalidates reviewed archive before extract and recovers through reopen", async () =>
                {
                    liveExtractSource = Path.GetFullPath(liveExtractSource);
                    liveExtractStaleSource = Path.GetFullPath(liveExtractStaleSource);
                    Assert(File.Exists(liveExtractSource), "The live ZIP extraction qualification source is missing.");
                    Assert(File.Exists(liveExtractStaleSource), "The live stale ZIP extraction qualification source is missing.");
                    window.Width = 1400;
                    window.Height = 860;
                    Application.Current!.RequestedThemeVariant = ThemeVariant.Dark;
                    await Task.Delay(80);
                    var proofDirectory = Path.GetDirectoryName(liveExtractSource)!;
                    var staleDestination = Path.Combine(proofDirectory, "stale extract proof Ω");
                    var acceptedDestination = Path.Combine(proofDirectory, "accepted extract proof Ω");
                    if (Directory.Exists(staleDestination)) Directory.Delete(staleDestination, true);
                    if (Directory.Exists(acceptedDestination)) Directory.Delete(acceptedDestination, true);

                    static async Task<(string Name, byte[] Bytes)> ReadSingleArchiveFileAsync(string path)
                    {
                        using var zip = ZipFile.OpenRead(path);
                        var entry = zip.Entries.Single(item => !string.IsNullOrEmpty(item.Name));
                        await using var stream = entry.Open();
                        using var memory = new MemoryStream();
                        await stream.CopyToAsync(memory);
                        return (entry.FullName, memory.ToArray());
                    }

                    async Task<(ListarySearchResult Identity, long ElapsedMs)> SearchExactAsync(string exactPath)
                    {
                        var queryBox = window.GetVisualDescendants().OfType<TextBox>().First(box => box.Name == "FileSearch");
                        var scopeBox = window.GetVisualDescendants().OfType<ComboBox>().First(box => box.Name == "SearchScope");
                        scopeBox.SelectedIndex = 2;
                        queryBox.Text = Path.GetFileName(Path.TrimEndingDirectorySeparator(exactPath));
                        var timer = Stopwatch.StartNew();
                        Click(window, "Find");
                        await window.LastCommand;
                        await WaitAsync(() => window.ActivePane.VisibleEntries.Any(entry =>
                            string.Equals(Path.TrimEndingDirectorySeparator(entry.FullPath),
                                Path.TrimEndingDirectorySeparator(exactPath),
                                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)));
                        timer.Stop();
                        var entry = window.ActivePane.VisibleEntries.Single(item =>
                            string.Equals(Path.TrimEndingDirectorySeparator(item.FullPath),
                                Path.TrimEndingDirectorySeparator(exactPath),
                                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal));
                        var identity = window.ActivePane.IndexedIdentity(entry)
                            ?? throw new InvalidOperationException("The live ZIP extraction result lost its Listary identity.");
                        window.ActivePane.SelectPaths([entry.FullPath]);
                        return (identity, timer.ElapsedMilliseconds);
                    }

                    async Task<ListarySearchResult> WaitForIndexedVersionAsync(string exactPath, Func<ListarySearchResult, bool> predicate)
                    {
                        using var witness = new ListaryClient();
                        var timer = Stopwatch.StartNew();
                        while (timer.Elapsed < TimeSpan.FromSeconds(12))
                        {
                            var page = await witness.SearchAsync(Path.GetFileName(Path.TrimEndingDirectorySeparator(exactPath)), 32);
                            var match = page.Results.FirstOrDefault(result =>
                                string.Equals(Path.TrimEndingDirectorySeparator(result.Entry.FullPath),
                                    Path.TrimEndingDirectorySeparator(exactPath),
                                    OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal));
                            if (match is not null && predicate(match)) return match;
                            await Task.Delay(50);
                        }
                        throw new TimeoutException("The Listary watcher did not publish the expected ZIP extraction qualification version.");
                    }

                    var staleReviewed = await ReadSingleArchiveFileAsync(liveExtractStaleSource);
                    var (staleIdentity, staleSearchMs) = await SearchExactAsync(liveExtractStaleSource);
                    window.Dispatch("extract");
                    var staleDialog = await FindDialogAsync(window, "ZIP — " + Path.GetFileName(liveExtractStaleSource));
                    staleDialog.GetVisualDescendants().OfType<TextBox>().First().Text = staleDestination;
                    if (output is not null)
                    {
                        await Task.Delay(80);
                        SaveFrame(staleDialog, Path.Combine(output, "opusmac-listary-extract-reviewed.png"), true);
                    }

                    var staleReplacementPayload = Encoding.UTF8.GetBytes("same-path ZIP extraction replacement " + Guid.NewGuid().ToString("N"));
                    File.Delete(liveExtractStaleSource);
                    using (var replacementZip = ZipFile.Open(liveExtractStaleSource, ZipArchiveMode.Create))
                    {
                        var entry = replacementZip.CreateEntry("replacement Ω.txt");
                        await using var stream = entry.Open();
                        await stream.WriteAsync(staleReplacementPayload);
                    }
                    var staleReplacementArchiveBytes = await File.ReadAllBytesAsync(liveExtractStaleSource);
                    var replacement = await WaitForIndexedVersionAsync(liveExtractStaleSource, current =>
                        current.ItemId != staleIdentity.ItemId ||
                        !string.Equals(current.StableFileId, staleIdentity.StableFileId, StringComparison.Ordinal) ||
                        current.IndexedSize != staleIdentity.IndexedSize ||
                        current.IndexedModifiedUnixMilliseconds != staleIdentity.IndexedModifiedUnixMilliseconds);
                    var staleExtractTimer = Stopwatch.StartNew();
                    Click(staleDialog, "Extract all");
                    await WaitAsync(() => FindButton(staleDialog, "Extract all")?.IsEnabled == true);
                    staleExtractTimer.Stop();
                    var staleRejectedBeforeOutput = !Directory.Exists(staleDestination);
                    var replacementSurvived = File.Exists(liveExtractStaleSource) &&
                        (await File.ReadAllBytesAsync(liveExtractStaleSource)).SequenceEqual(staleReplacementArchiveBytes);
                    var staleFailure = staleDialog.GetVisualDescendants().OfType<TextBlock>()
                        .Select(block => block.Text)
                        .FirstOrDefault(text => text?.Contains("selected indexed file changed", StringComparison.OrdinalIgnoreCase) == true)
                        ?? string.Join(" | ", staleDialog.GetVisualDescendants().OfType<TextBlock>().Select(block => block.Text).Where(text => !string.IsNullOrWhiteSpace(text)));
                    if (!staleRejectedBeforeOutput || !replacementSurvived)
                    {
                        if (Directory.Exists(staleDestination)) Directory.Delete(staleDestination, true);
                        if (staleDialog.IsVisible) Click(staleDialog, "Close");
                        throw new InvalidOperationException(
                            "A same-path replacement ZIP was extracted after its reviewed listing instead of failing stale-item authority.");
                    }
                    if (output is not null)
                    {
                        await Task.Delay(80);
                        SaveFrame(staleDialog, Path.Combine(output, "opusmac-listary-extract-stale-refusal.png"), true);
                    }
                    Click(staleDialog, "Close");

                    var authorized = await ReadSingleArchiveFileAsync(liveExtractSource);
                    var (sourceIdentity, firstResultMs) = await SearchExactAsync(liveExtractSource);
                    var extractTimer = Stopwatch.StartNew();
                    window.Dispatch("extract");
                    var extractDialog = await FindDialogAsync(window, "ZIP — " + Path.GetFileName(liveExtractSource));
                    extractDialog.GetVisualDescendants().OfType<TextBox>().First().Text = acceptedDestination;
                    Click(extractDialog, "Extract all");
                    var extractedPath = Path.Combine(acceptedDestination, authorized.Name.Replace('/', Path.DirectorySeparatorChar));
                    await WaitAsync(() => FindButton(extractDialog, "Extract all")?.IsEnabled == true && File.Exists(extractedPath));
                    extractTimer.Stop();
                    Assert((await File.ReadAllBytesAsync(extractedPath)).SequenceEqual(authorized.Bytes), "The authorized ZIP extraction produced different bytes.");
                    if (output is not null)
                    {
                        await Task.Delay(80);
                        SaveFrame(extractDialog, Path.Combine(output, "opusmac-listary-extract-complete.png"), true);
                    }
                    Click(extractDialog, "Close");

                    var reopenTimer = Stopwatch.StartNew();
                    await window.CloseForDiagnosticsAsync();
                    window = new MainWindow(launch);
                    Application.Current!.RequestedThemeVariant = ThemeVariant.Dark;
                    window.Show();
                    await window.InitializeAsync();
                    reopenTimer.Stop();
                    var (reopenedIdentity, reopenSearchMs) = await SearchExactAsync(liveExtractSource);
                    Assert(reopenedIdentity.ItemId == sourceIdentity.ItemId &&
                        reopenedIdentity.RootId == sourceIdentity.RootId &&
                        string.Equals(reopenedIdentity.StableFileId, sourceIdentity.StableFileId, StringComparison.Ordinal) &&
                        string.Equals(reopenedIdentity.EndpointIdentity, sourceIdentity.EndpointIdentity, StringComparison.Ordinal),
                        "ZIP extraction reopen did not retain the same canonical Listary archive identity.");

                    if (output is not null)
                    {
                        SaveFrame(window, Path.Combine(output, "opusmac-listary-extract-restored.png"));
                        var receipt = new
                        {
                            schema = "opusmac_listary_extract_zip_workflow_v1",
                            source = liveExtractSource,
                            stale_source = liveExtractStaleSource,
                            accepted_destination = acceptedDestination,
                            stale_destination = staleDestination,
                            reviewed_entry = staleReviewed.Name,
                            authorized_entry = authorized.Name,
                            first_result_ms = firstResultMs,
                            extract_zip_ms = extractTimer.ElapsedMilliseconds,
                            reopen_ms = reopenTimer.ElapsedMilliseconds,
                            reopen_search_ms = reopenSearchMs,
                            stale_search_ms = staleSearchMs,
                            stale_extract_to_refusal_ms = staleExtractTimer.ElapsedMilliseconds,
                            source_identity = new { sourceIdentity.ItemId, sourceIdentity.RootId, sourceIdentity.StableFileId, sourceIdentity.EndpointIdentity },
                            reopened_identity = new { reopenedIdentity.ItemId, reopenedIdentity.RootId, reopenedIdentity.StableFileId, reopenedIdentity.EndpointIdentity },
                            stale_identity = new { staleIdentity.ItemId, staleIdentity.RootId, staleIdentity.StableFileId, staleIdentity.EndpointIdentity },
                            replacement_identity = new { replacement.ItemId, replacement.RootId, replacement.StableFileId, replacement.EndpointIdentity },
                            stale_rejected_before_output = staleRejectedBeforeOutput,
                            stale_replacement_survived = replacementSurvived,
                            stale_failure = staleFailure,
                            extracted_bytes_match = (await File.ReadAllBytesAsync(extractedPath)).SequenceEqual(authorized.Bytes),
                            utc = DateTimeOffset.UtcNow
                        };
                        await File.WriteAllTextAsync(
                            Path.Combine(output, "listary-extract-zip-workflow.json"),
                            JsonSerializer.Serialize(receipt, new JsonSerializerOptions { WriteIndented = true }));
                    }
                });
            }
            var liveCreateDirectory = Environment.GetEnvironmentVariable("SPIFFY_LISTARY_CREATE_ENTRY_QUALIFICATION_DIRECTORY");
            var liveCreateStaleDirectory = Environment.GetEnvironmentVariable("SPIFFY_LISTARY_CREATE_ENTRY_QUALIFICATION_STALE_DIRECTORY");
            if (!string.IsNullOrWhiteSpace(liveCreateDirectory) && !string.IsNullOrWhiteSpace(liveCreateStaleDirectory))
            {
                await CheckAsync("Live Listary indexed folder revalidates after create prompt and creation survives restart undo", async () =>
                {
                    liveCreateDirectory = Path.GetFullPath(liveCreateDirectory);
                    liveCreateStaleDirectory = Path.GetFullPath(liveCreateStaleDirectory);
                    Assert(Directory.Exists(liveCreateDirectory), "The live create-entry qualification directory is missing.");
                    Assert(Directory.Exists(liveCreateStaleDirectory), "The live stale create-entry qualification directory is missing.");
                    window.Width = 1400;
                    window.Height = 860;
                    Application.Current!.RequestedThemeVariant = ThemeVariant.Dark;
                    await Task.Delay(80);
                    var createdName = "authorized create Ω.txt";
                    var staleCreatedName = "stale create Ω.txt";
                    var createdPath = Path.Combine(liveCreateDirectory, createdName);
                    var staleCreatedPath = Path.Combine(liveCreateStaleDirectory, staleCreatedName);
                    if (File.Exists(createdPath)) File.Delete(createdPath);
                    if (File.Exists(staleCreatedPath)) File.Delete(staleCreatedPath);

                    async Task<(ListarySearchResult Identity, long ElapsedMs)> SearchExactAsync(string exactPath)
                    {
                        var queryBox = window.GetVisualDescendants().OfType<TextBox>().First(box => box.Name == "FileSearch");
                        var scopeBox = window.GetVisualDescendants().OfType<ComboBox>().First(box => box.Name == "SearchScope");
                        scopeBox.SelectedIndex = 2;
                        queryBox.Text = Path.GetFileName(Path.TrimEndingDirectorySeparator(exactPath));
                        var timer = Stopwatch.StartNew();
                        Click(window, "Find");
                        await window.LastCommand;
                        await WaitAsync(() => window.ActivePane.VisibleEntries.Any(entry =>
                            string.Equals(Path.TrimEndingDirectorySeparator(entry.FullPath),
                                Path.TrimEndingDirectorySeparator(exactPath),
                                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)));
                        timer.Stop();
                        var entry = window.ActivePane.VisibleEntries.Single(entry =>
                            string.Equals(Path.TrimEndingDirectorySeparator(entry.FullPath),
                                Path.TrimEndingDirectorySeparator(exactPath),
                                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal));
                        var identity = window.ActivePane.IndexedIdentity(entry)
                            ?? throw new InvalidOperationException("The live create-entry directory result lost its Listary identity.");
                        Assert(identity.Entry.IsDirectory, "The create-entry qualification result is not a directory.");
                        window.ActivePane.SelectPaths([entry.FullPath]);
                        return (identity, timer.ElapsedMilliseconds);
                    }

                    async Task OpenIndexedDirectoryAsync(string exactPath, ListarySearchResult identity)
                    {
                        window.Dispatch("open");
                        await window.LastCommand;
                        await WaitAsync(() => string.Equals(
                            Path.TrimEndingDirectorySeparator(window.ActivePane.CurrentDirectory),
                            Path.TrimEndingDirectorySeparator(exactPath),
                            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal));
                        Assert(window.ActivePane.CurrentDirectoryIndexedIdentity is { } current &&
                            current.ItemId == identity.ItemId && current.RootId == identity.RootId &&
                            string.Equals(current.StableFileId, identity.StableFileId, StringComparison.Ordinal),
                            "Opening the indexed directory did not retain canonical Listary identity for create authority.");
                    }

                    async Task<ListarySearchResult> WaitForIndexedVersionAsync(string exactPath, Func<ListarySearchResult, bool> predicate)
                    {
                        using var witness = new ListaryClient();
                        var timer = Stopwatch.StartNew();
                        while (timer.Elapsed < TimeSpan.FromSeconds(12))
                        {
                            var page = await witness.SearchAsync(Path.GetFileName(Path.TrimEndingDirectorySeparator(exactPath)), 32);
                            var match = page.Results.FirstOrDefault(result =>
                                string.Equals(Path.TrimEndingDirectorySeparator(result.Entry.FullPath),
                                    Path.TrimEndingDirectorySeparator(exactPath),
                                    OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal));
                            if (match is not null && predicate(match)) return match;
                            await Task.Delay(50);
                        }
                        throw new TimeoutException("The Listary watcher did not publish the expected create-entry directory version.");
                    }

                    var (staleIdentity, staleSearchMs) = await SearchExactAsync(liveCreateStaleDirectory);
                    await OpenIndexedDirectoryAsync(liveCreateStaleDirectory, staleIdentity);
                    var historyBeforeStale = window.OperationHistory.Count;
                    window.Dispatch("new-file");
                    var stalePrompt = await FindDialogAsync(window, "New Text File");
                    stalePrompt.GetVisualDescendants().OfType<TextBox>().Single().Text = staleCreatedName;
                    if (output is not null)
                    {
                        await Task.Delay(80);
                        using var promptFrame = stalePrompt.CaptureRenderedFrame();
                        if (promptFrame is not null)
                            promptFrame.Save(Path.Combine(output, "opusmac-listary-create-entry-prompt.png"));
                    }
                    Directory.Delete(liveCreateStaleDirectory);
                    Directory.CreateDirectory(liveCreateStaleDirectory);
                    var replacement = await WaitForIndexedVersionAsync(liveCreateStaleDirectory, current =>
                        current.ItemId != staleIdentity.ItemId ||
                        !string.Equals(current.StableFileId, staleIdentity.StableFileId, StringComparison.Ordinal));
                    var staleConfirmTimer = Stopwatch.StartNew();
                    Click(stalePrompt, "Create");
                    await window.LastCommand;
                    staleConfirmTimer.Stop();
                    var staleRejectedBeforeCreate = !File.Exists(staleCreatedPath) && window.OperationHistory.Count == historyBeforeStale;
                    var staleFailure = window.StatusMessage;
                    if (!staleRejectedBeforeCreate)
                    {
                        if (File.Exists(staleCreatedPath)) File.Delete(staleCreatedPath);
                        throw new InvalidOperationException(
                            "A same-path replacement directory received a created file after the naming prompt instead of failing indexed-directory authority.");
                    }
                    if (output is not null)
                    {
                        await Task.Delay(80);
                        SaveFrame(window, Path.Combine(output, "opusmac-listary-create-entry-stale-refusal.png"));
                    }

                    var (sourceIdentity, firstResultMs) = await SearchExactAsync(liveCreateDirectory);
                    await OpenIndexedDirectoryAsync(liveCreateDirectory, sourceIdentity);
                    var createTimer = Stopwatch.StartNew();
                    window.Dispatch("new-file");
                    var createPrompt = await FindDialogAsync(window, "New Text File");
                    createPrompt.GetVisualDescendants().OfType<TextBox>().Single().Text = createdName;
                    Click(createPrompt, "Create");
                    await window.LastCommand;
                    createTimer.Stop();
                    var createReceipt = window.LastOperation ?? throw new InvalidOperationException("Create file produced no operation receipt.");
                    Assert(createReceipt.Succeeded && !createReceipt.Canceled && File.Exists(createdPath), "The authorized create-file action did not commit.");
                    var operationId = createReceipt.Id;

                    var reopenBeforeUndoTimer = Stopwatch.StartNew();
                    await window.CloseForDiagnosticsAsync();
                    window = new MainWindow(launch);
                    Application.Current!.RequestedThemeVariant = ThemeVariant.Dark;
                    window.Show();
                    await window.InitializeAsync();
                    reopenBeforeUndoTimer.Stop();
                    var reopenedCreated = window.OperationHistory.Single(item => item.Id == operationId);
                    Assert(reopenedCreated.CanUndo && reopenedCreated.Status == "Succeeded", "Reopen did not preserve the create operation as undoable.");

                    var undoTimer = Stopwatch.StartNew();
                    Click(window, "Undo");
                    await window.LastCommand;
                    undoTimer.Stop();
                    var undoReceipt = window.LastOperation ?? throw new InvalidOperationException("Create-file Undo produced no receipt.");
                    Assert(undoReceipt.Succeeded && !File.Exists(createdPath), "Undo did not remove the authorized created file.");

                    var reopenAfterUndoTimer = Stopwatch.StartNew();
                    await window.CloseForDiagnosticsAsync();
                    window = new MainWindow(launch);
                    Application.Current!.RequestedThemeVariant = ThemeVariant.Dark;
                    window.Show();
                    await window.InitializeAsync();
                    reopenAfterUndoTimer.Stop();
                    var reopenedUndone = window.OperationHistory.Single(item => item.Id == operationId);
                    Assert(!reopenedUndone.CanUndo && reopenedUndone.Status == "Undone", "Second reopen did not preserve completed create-file Undo state.");
                    var (reopenedIdentity, reopenSearchMs) = await SearchExactAsync(liveCreateDirectory);
                    Assert(reopenedIdentity.ItemId == sourceIdentity.ItemId && reopenedIdentity.RootId == sourceIdentity.RootId &&
                        string.Equals(reopenedIdentity.StableFileId, sourceIdentity.StableFileId, StringComparison.Ordinal) &&
                        string.Equals(reopenedIdentity.EndpointIdentity, sourceIdentity.EndpointIdentity, StringComparison.Ordinal),
                        "Create-entry reopen did not retain the same canonical Listary directory identity.");

                    if (output is not null)
                    {
                        SaveFrame(window, Path.Combine(output, "opusmac-listary-create-entry-restored.png"));
                        var receipt = new
                        {
                            schema = "opusmac_listary_create_entry_workflow_v1",
                            directory = liveCreateDirectory,
                            stale_directory = liveCreateStaleDirectory,
                            created_name = createdName,
                            first_result_ms = firstResultMs,
                            create_file_ms = createTimer.ElapsedMilliseconds,
                            reopen_before_undo_ms = reopenBeforeUndoTimer.ElapsedMilliseconds,
                            undo_ms = undoTimer.ElapsedMilliseconds,
                            reopen_after_undo_ms = reopenAfterUndoTimer.ElapsedMilliseconds,
                            reopen_search_ms = reopenSearchMs,
                            stale_search_ms = staleSearchMs,
                            stale_confirm_to_refusal_ms = staleConfirmTimer.ElapsedMilliseconds,
                            source_identity = new { sourceIdentity.ItemId, sourceIdentity.RootId, sourceIdentity.StableFileId, sourceIdentity.EndpointIdentity },
                            reopened_identity = new { reopenedIdentity.ItemId, reopenedIdentity.RootId, reopenedIdentity.StableFileId, reopenedIdentity.EndpointIdentity },
                            stale_identity = new { staleIdentity.ItemId, staleIdentity.RootId, staleIdentity.StableFileId, staleIdentity.EndpointIdentity },
                            replacement_identity = new { replacement.ItemId, replacement.RootId, replacement.StableFileId, replacement.EndpointIdentity },
                            create = new { createReceipt.Id, createReceipt.Succeeded, createReceipt.Canceled, changes = createReceipt.Changes },
                            undo = new { undoReceipt.Id, undoReceipt.Succeeded, undoReceipt.Canceled, changes = undoReceipt.Changes },
                            reopened_before_undo = reopenedCreated,
                            reopened_after_undo = reopenedUndone,
                            stale_rejected_before_create = staleRejectedBeforeCreate,
                            stale_failure = staleFailure,
                            utc = DateTimeOffset.UtcNow
                        };
                        await File.WriteAllTextAsync(
                            Path.Combine(output, "listary-create-entry-workflow.json"),
                            JsonSerializer.Serialize(receipt, new JsonSerializerOptions { WriteIndented = true }));
                    }
                });
            }
            if (output is not null)
            {
                await CheckAsync("Skia renders real controls at large and compact sizes", async () =>
                {
                    window.Width = 1400; window.Height = 860;
                    Application.Current!.RequestedThemeVariant = ThemeVariant.Light;
                    window.ActivePane.SelectPaths([Path.Combine(left, "Project notes.txt")]);
                    await Task.Delay(80); SaveFrame(window, Path.Combine(output, "opusmac-light-dual.png"));
                    Application.Current.RequestedThemeVariant = ThemeVariant.Dark;
                    window.ActivePane.SelectPaths([Path.Combine(left, "Project notes.txt")]);
                    await Task.Delay(80); SaveFrame(window, Path.Combine(output, "opusmac-dark-dual.png"));
                    var panes = new[] { window.ActivePane, window.OtherPane };
                    var savedColumns = panes.Select(pane => pane.CurrentColumnLayout
                        .Select(column => (column.Id, column.Visible, column.WidthMode, column.Width)).ToArray()).ToArray();
                    window.Width = 1000; window.Height = 650;
                    Assert(window.ActivePane.FileGrid.Columns[0].MinWidth >= 160, "The filename column can collapse at compact widths.");
                    await Task.Delay(80); window.UpdateLayout();
                    for (var index = 0; index < panes.Length; index++)
                    {
                        var pane = panes[index];
                        Assert(pane.FileGrid.Columns.Count >= 5 && pane.Bounds.Width < 420,
                            $"The compact side-by-side pane did not reproduce the narrow Details layout ({pane.Bounds.Width:F0}px).");
                        Assert(pane.FileGrid.Columns[0].IsVisible && pane.FileGrid.Columns[1].IsVisible &&
                               pane.FileGrid.Columns.Skip(2).All(column => !column.IsVisible),
                            "A narrow Details pane still shows truncated secondary columns instead of prioritizing Name and Size.");
                        Assert(pane.CurrentColumnLayout.Select(column => (column.Id, column.Visible, column.WidthMode, column.Width))
                                   .SequenceEqual(savedColumns[index]),
                            "Compact rendering changed the saved column order, widths or visibility.");
                        Assert(AutomationProperties.GetHelpText(pane.FileGrid)?.Contains("Narrow", StringComparison.Ordinal) == true,
                            "Compact column behavior lacks an accessible explanation.");
                    }
                    SaveFrame(window, Path.Combine(output, "opusmac-compact.png"));
                    Assert(window.ActivePane.FileGrid.Columns[0].ActualWidth >= 160, "Filenames disappeared at compact width.");
                    Assert(window.ActivePane.Bounds.Width >= 290 && window.OtherPane.Bounds.Width >= 290,
                        $"Compact dual pane widths collapsed: source={window.ActivePane.Bounds.Width:F0}, destination={window.OtherPane.Bounds.Width:F0}.");
                    Assert(window.ViewerVisible, "Compact sizing silently hid the user's Viewer choice.");
                    window.Dispatch("viewer"); await window.LastCommand;
                    Assert(!window.ViewerVisible, "F7/Viewer control could not hide the compact preview.");
                    window.Dispatch("viewer"); await window.LastCommand;
                    Assert(window.ViewerVisible, "F7/Viewer control could not restore the compact preview.");
                    window.Width = 2176; window.Height = 1190;
                    await Task.Delay(80); window.UpdateLayout();
                    Assert(window.ActivePane.Bounds.Width >= 420 && window.OtherPane.Bounds.Width >= 420 &&
                           window.ActivePane.FileGrid.Columns.All(column => column.IsVisible) &&
                           window.OtherPane.FileGrid.Columns.All(column => column.IsVisible),
                        "Widening the window did not restore the user's full Details column layout.");
                    for (var index = 0; index < panes.Length; index++)
                        Assert(panes[index].CurrentColumnLayout.Select(column => (column.Id, column.Visible, column.WidthMode, column.Width))
                                   .SequenceEqual(savedColumns[index]),
                            "Widening the window altered the user's saved Details column definitions.");
                    SaveFrame(window, Path.Combine(output, "opusmac-wide-restored.png"));
                    Assert(window.ActivePane.Bounds.Width > 290 && window.OtherPane.Bounds.Width > 290,
                        "Restoring the wide window did not restore usable file panes.");
                    // The compact view must respect a custom folder format that hides
                    // Size and prefers the next visible column, not invent Size.
                    var custom = window.ActivePane.CurrentColumnLayout.Select(column => column.Copy()).ToList();
                    custom.Single(column => column.Id == "Size").Visible = false;
                    window.ActivePane.ApplyCurrentColumnLayout(custom);
                    window.Width = 1000; window.Height = 650;
                    await Task.Delay(80); window.UpdateLayout();
                    var compactColumns = window.ActivePane.FileGrid.Columns;
                    Assert(window.ActivePane.Bounds.Width >= 270 && window.ActivePane.Bounds.Width < 420 &&
                           compactColumns.Count >= 4 && compactColumns[0].IsVisible && compactColumns[1].IsVisible &&
                           compactColumns.Skip(2).All(column => !column.IsVisible) &&
                           !window.ActivePane.CurrentColumnLayout.Single(column => column.Id == "Size").Visible,
                        "Compact Details ignored the next user-visible column when Size was hidden.");
                    window.Width = 2176; window.Height = 1190;
                    await Task.Delay(80); window.UpdateLayout();
                    Assert(compactColumns.All(column => column.IsVisible) &&
                           !window.ActivePane.CurrentColumnLayout.Single(column => column.Id == "Size").Visible,
                        "Restoring width resurrected a hidden Size column or dropped custom columns.");
                });
            }
        }
        catch (Exception error) { checks.Add(new Check("Application startup", false, error.ToString())); Console.Error.WriteLine(error); }
        finally
        {
            if (window is not null) await window.CloseForDiagnosticsAsync();
            Environment.SetEnvironmentVariable("SPIFFY_LISTARY_STATE_DIR", priorAgentState);
            if (onlyCheck == malformedAppearanceCheck)
                Environment.SetEnvironmentVariable("SS_WORKFLOW_SETTINGS_ROOT", priorAppearanceRoot);
           if (rootFixture is not null) await rootFixture.DisposeAsync();
            if (!string.IsNullOrWhiteSpace(onlyCheck) && checks.Count == 0)
                checks.Add(new Check(onlyCheck, false, "No matching diagnostic was executed. Check the name and fixture requirements."));
            if (suite is not null) suite.AddRange(checks);
            else
            {
                var report = new { Passed = checks.Count(check => check.Passed), Failed = checks.Count(check => !check.Passed), ElapsedMilliseconds = stopwatch.ElapsedMilliseconds,
                    Platform = System.Runtime.InteropServices.RuntimeInformation.OSDescription, Headless = true, NativeMacWorkflowsTested = false,
                    Skipped = skipped, Checks = checks };
                var json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
                if (output is not null) await File.WriteAllTextAsync(Path.Combine(output, "ui-checks.json"), json);
                Console.WriteLine(json);
            }
            if (preserveDiagnosticRoot)
            {
                // The indexed Undo failure snapshot points back into this
                // task-owned tree. Keep the source, destination, journal and
                // owner markers together until the failure has been inspected.
                Console.Error.WriteLine("Retained task-owned indexed diagnostic fixture for failure diagnosis: " + root);
            }
            else if (Directory.Exists(root))
            {
                try { Directory.Delete(root, true); }
                catch (IOException error) { Console.Error.WriteLine("Fixture cleanup: " + error.Message); }
            }
        }
        return checks.Any(check => !check.Passed) ? 1 : 0;
    }

    private static Button? FindButton(Window window, string label) => window.GetVisualDescendants().OfType<Button>()
        .FirstOrDefault(candidate => candidate.IsVisible &&
            (candidate.Content is string text && text == label ||
             string.Equals(AutomationProperties.GetName(candidate), label switch { "Clear" => "Clear search", _ => label }, StringComparison.Ordinal)));

    private static MenuItem? FindCommandMenuItem(Window window, string label)
    {
        var heading = label switch
        {
            "Archive" => "Create ZIP…",
            "History" => "Operation History",
            "Rename" => "Advanced Rename…",
            "Delete" => "Move to Trash",
            _ => label
        };
        static IEnumerable<MenuItem> Commands(MenuItem group)
        {
            foreach (var item in group.Items.OfType<MenuItem>())
            {
                yield return item;
                foreach (var child in Commands(item)) yield return child;
            }
        }
        return window.GetVisualDescendants().OfType<Menu>()
            .Where(menu => menu.IsVisible)
            .SelectMany(menu => menu.Items.OfType<MenuItem>())
            .SelectMany(Commands)
            .FirstOrDefault(item => string.Equals(item.Header?.ToString(), heading, StringComparison.Ordinal) && item.IsEnabled);
    }

    private static async Task<Window> FindDialogAsync(Window owner, string title)
    {
        await WaitAsync(() => owner.OwnedWindows.Any(window => window.Title == title && window.IsVisible));
        var dialog = owner.OwnedWindows.First(window => window.Title == title && window.IsVisible);
        using (dialog.CaptureRenderedFrame()) { }
        return dialog;
    }

    private static void Click(Window window, string label)
    {
        if (FindButton(window, label) is { } button)
        {
            Assert(button.IsEnabled, $"Button is disabled: {label}");
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            return;
        }
        // The installed-reference layout keeps file operations in the visible
        // command menus instead of the old permanently exposed button strip.
        var item = FindCommandMenuItem(window, label)
            ?? throw new InvalidOperationException($"Visible button or command-menu item not found: {label}");
        item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
    }

    private static void ClickNamedButton(Window window, string name)
    {
        var button = window.GetVisualDescendants().OfType<Button>()
            .FirstOrDefault(candidate => candidate.Name == name && candidate.IsVisible)
            ?? throw new InvalidOperationException($"Visible button not found: {name}");
        Assert(button.IsEnabled, $"Button is disabled: {name}");
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();
    }

    private static void DragSplitter(MainWindow window, string name, Vector delta)
    {
        window.UpdateLayout();
        var splitter = window.GetVisualDescendants().OfType<GridSplitter>().Single(control => control.Name == name);
        var origin = splitter.TranslatePoint(new Point(splitter.Bounds.Width / 2, splitter.Bounds.Height / 2), window)
            ?? throw new InvalidOperationException($"The {name} splitter is not arranged.");
        var end = origin + delta;
        window.MouseDown(origin, MouseButton.Left, RawInputModifiers.None);
        window.MouseMove(end, RawInputModifiers.LeftMouseButton);
        window.MouseUp(end, MouseButton.Left, RawInputModifiers.None);
    }

    private static void MoveFolderFormatColumnToSecond(Window dialog, string columnId)
    {
        for (var attempt = 0; attempt < 12; attempt++)
        {
            var rows = dialog.GetVisualDescendants().OfType<StackPanel>()
                .Single(panel => panel.Name == "FolderFormatRows");
            var index = rows.Children.Select((row, position) => (row, position))
                .Where(pair => pair.row.GetVisualDescendants().OfType<CheckBox>()
                    .Any(check => check.Name == "FolderFormatVisible_" + columnId))
                .Select(pair => pair.position)
                .Single();
            if (index == 1) return;
            Assert(index > 1, $"Cannot move {columnId} below Name in Folder Format.");
            ClickNamedButton(dialog, "FolderFormatUp_" + columnId);
        }
        throw new InvalidOperationException($"Folder Format did not move {columnId} into second place.");
    }

    private static bool ColumnLayoutsMatch(IReadOnlyList<FileColumnSettings> left, IReadOnlyList<FileColumnSettings> right) =>
        left.Count == right.Count && left.Zip(right).All(pair => pair.First.Id == pair.Second.Id &&
            pair.First.Visible == pair.Second.Visible && pair.First.WidthMode == pair.Second.WidthMode &&
            Math.Abs(pair.First.Width - pair.Second.Width) < 1);

    private static void Press(Window window, PhysicalKey key)
    {
        window.KeyPressQwerty(key, RawInputModifiers.None); window.KeyReleaseQwerty(key, RawInputModifiers.None);
    }

    private static void SaveFrame(Window window, string path, bool dialog = false)
    {
        Dispatcher.UIThread.RunJobs();
        using var frame = window.CaptureRenderedFrame() ?? throw new InvalidOperationException("The headless renderer returned no bitmap.");
        Assert(frame.PixelSize.Width >= (dialog ? 400 : 1000) && frame.PixelSize.Height >= (dialog ? 180 : 600), "Rendered frame has an unexpected size.");
        frame.Save(path);
        Assert(new FileInfo(path).Length > 10000, "The rendered image is unexpectedly empty.");
    }

    private static async Task WaitAsync(Func<bool> ready)
    {
        var timer = Stopwatch.StartNew();
        while (!ready())
        {
            if (timer.Elapsed > TimeSpan.FromSeconds(12)) throw new TimeoutException("The expected UI state did not arrive.");
            await Task.Delay(25);
        }
    }

    private static void Assert(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private sealed record Check(string Name, bool Passed, string? Error);
}
