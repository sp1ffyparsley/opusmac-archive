using System.Text;
using System.Security.Cryptography;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using OpusMac.Core;
using OpusMac.Core.Browsing;
using OpusMac.Core.Integration;
using OpusMac.Core.Operations;
using OpusMac.Core.Persistence;
using OpusMac.Core.Search;
using OpusMac.Core.Tools;

namespace OpusMac.App;

// The production reader always observes Windows' actual clipboard formats.
// Headless UI diagnostics can supply a private snapshot without changing the
// desktop clipboard; the same Paste validation and operation owner still run.
internal interface IWindowsClipboardIntentReader
{
    WindowsClipboardDropSnapshot? Capture();
    uint CurrentSequence { get; }
}

internal sealed class NativeWindowsClipboardIntentReader : IWindowsClipboardIntentReader
{
    internal static readonly NativeWindowsClipboardIntentReader Instance = new();
    public WindowsClipboardDropSnapshot? Capture() => WindowsClipboardDropIntent.TryCapture();
    public uint CurrentSequence => WindowsClipboardDropIntent.CurrentSequence;
}

public sealed partial class MainWindow
{
    private IWindowsClipboardIntentReader _windowsClipboardIntentReader = NativeWindowsClipboardIntentReader.Instance;

    internal IDisposable UseClipboardIntentReaderForDiagnostics(IWindowsClipboardIntentReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);
        var previous = _windowsClipboardIntentReader;
        _windowsClipboardIntentReader = reader;
        return new ClipboardIntentReaderRestore(this, previous);
    }

    private sealed class ClipboardIntentReaderRestore(MainWindow owner, IWindowsClipboardIntentReader previous) : IDisposable
    {
        public void Dispose() => owner._windowsClipboardIntentReader = previous;
    }

    private const int MaxContentFingerprintItems = 16;
    private FileOperationResult? _lastOperation;
    private IReadOnlyList<(string Path, string Identity)> _reselectItems = [];
    // Positional identities retain each clipboard occurrence, including rows
    // that came from ordinary folder browsing rather than the Listary index.
    private IReadOnlyList<ListarySearchResult?> _clipboardIndexed = [];
    private IReadOnlyList<string> _clipboardNativeIds = [];
    private readonly SemaphoreSlim _clipboardPublishGate = new(1, 1);
    private long _clipboardRequestGeneration;

    private async Task ExecuteCommandAsync(string command)
    {
        EnsureAcceptingWork();
        var pane = ActivePane;
        var directoryAtStart = pane.CurrentDirectory;
        if (command is "copy-files" or "move-files" or "hard-links" or "paste" or "new-folder" or "new-file" or "duplicate" or "archive" or "extract" or "compare" or "terminal") EnsurePaneReady(pane);
        switch (command)
        {
            case "open-folder":
                var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Open folder", AllowMultiple = false });
                if (folders.FirstOrDefault()?.TryGetLocalPath() is { } folder) await pane.NavigateAsync(folder);
                break;
            case "open":
                foreach (var entry in pane.SelectedEntries) await OpenEntryAsync(pane, entry);
                break;
            case "new-tab-selected":
                foreach (var entry in await ResolveSelectionAsync(pane))
                    await pane.AddTabAsync(entry.IsDirectory ? entry.FullPath : Path.GetDirectoryName(entry.FullPath)!);
                break;
            case "new-tab": await pane.AddTabAsync(pane.CurrentDirectory); break;
            case "close-tab": pane.CloseCurrentTab(); break;
            case "next-tab": pane.NextTab(1); break;
            case "previous-tab": pane.NextTab(-1); break;
            case "lock-tab": pane.ToggleTabLock(); break;
            case "back": await pane.HistoryAsync(-1); break;
            case "forward": await pane.HistoryAsync(1); break;
            case "parent": await pane.ParentAsync(); break;
            case "folder-sizes": await CalculateFolderSizesAsync(pane); break;
            case "folder-size-settings": await FolderSizeSettingsAsync(); break;
            case "home": await pane.NavigateAsync(ResolveHomeFolder(pane)); break;
            case "home-new-tab": await pane.AddTabAsync(ResolveHomeFolder(pane)); break;
            case "home-set-current": await SetHomeFolderAsync(directoryAtStart); break;
            case "home-reset": await SetHomeFolderAsync(null); break;
            case "home-settings": await HomeFolderSettingsAsync(); break;
            case "drive-root":
                if (Path.GetPathRoot(pane.CurrentDirectory) is { Length: > 0 } driveRoot)
                    await pane.NavigateAsync(driveRoot);
                break;
            case "refresh": await pane.RefreshAsync(); break;
            case "switch-pane": if (_settings.DualPane) { SetActive(OtherPane); ActivePane.FocusList(); } break;
            case "dual":
                if (!_settings.DualPane && (await ResolveFolderPairAsync(directoryAtStart, pair => pair.DefaultDual)) is { } dualPair &&
                    await OpenFolderPairAsync(pane, directoryAtStart, dualPair.Target,
                        dualPair.Pair.PrimaryOnLeft ? (dualPair.FromPrimary ? 0 : 1) : null, dualPair.Pair.LinkPairedTabs))
                {
                    if (dualPair.Pair.LockOnDual) SetNavigationLock(true);
                    break;
                }
                _settings.DualPane = !_settings.DualPane;
                ApplyLayout();
                break;
            case "vertical": _settings.DualPane = true; _settings.SplitOrientation = "Vertical"; ApplyLayout(); break;
            case "horizontal": _settings.DualPane = true; _settings.SplitOrientation = "Horizontal"; ApplyLayout(); break;
            case "style-commander": ApplyListerStyle(ListerStyle.Commander); break;
            case "style-explorer": ApplyListerStyle(ListerStyle.Explorer); break;
            case "style-single": ApplyListerStyle(ListerStyle.Single); break;
            case "style-images": ApplyListerStyle(ListerStyle.Images); break;
            case "style-dual-vertical": ApplyListerStyle(ListerStyle.DualVertical); break;
            case "style-dual-horizontal": ApplyListerStyle(ListerStyle.DualHorizontal); break;
            case "style-filmstrip": ApplyListerStyle(ListerStyle.Filmstrip); break;
            case "import-lister-style":
                var styles = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
                {
                    Title = "Import Directory Opus Lister Style",
                    FileTypeFilter = [new FilePickerFileType("Directory Opus Lister Style") { Patterns = ["*.osy"] }]
                });
                if (styles.Count != 1) break;
                await using (var stream = await styles[0].OpenReadAsync())
                    ApplyListerStyle(await ListerStyleImport.ReadAsync(stream));
                break;
            case "viewer": _settings.ShowViewer = !_settings.ShowViewer; ApplyLayout(); break;
            case "navigation-lock":
                if (!_navigationLocked && (await ResolveFolderPairAsync(directoryAtStart, pair => pair.DefaultNavigationLock)) is { } lockPair)
                {
                    if (await OpenFolderPairAsync(pane, directoryAtStart, lockPair.Target, linkTabs: lockPair.Pair.LinkPairedTabs)) SetNavigationLock(true);
                    break;
                }
                SetNavigationLock(!_navigationLocked);
                if (!_navigationLocked) _message.Text = "Navigation Lock off";
                break;
            case "folder-aliases": await ManageFolderAliasesAsync(); break;
            case "folder-pairs": await ManageFolderPairsAsync(); break;
            case "navigation-lock-reset":
                if (_navigationLocked) SetNavigationLock(true);
                break;
            case "tree": _settings.ShowTree = !_settings.ShowTree; ApplyLayout(); break;
            case "destination-tree":
                if (!_settings.DualPane) break;
                _settings.IndependentFolderTrees = !_settings.IndependentFolderTrees;
                ApplyLayout();
                break;
            case "favorites-bar":
                _settings.ShowFavoritesBar = !_settings.ShowFavoritesBar;
                UpdateFavoritesBar();
                break;
            case "hidden": _settings.ShowHidden = !_settings.ShowHidden; await RefreshPanesAsync(); BuildTree(); break;
            case "folder-format": await FolderFormatAsync(pane); break;
            case "edit-default-format": await FolderFormatAsync(pane, editDefault: true); break;
            case "manage-folder-formats": await ManageFolderFormatsAsync(pane); break;
            case "lock-format":
                EnsurePaneReady(pane);
                pane.ToggleFormatLock();
                _message.Text = pane.IsFormatLocked ? "Folder format locked" : "Folder format unlocked";
                break;
            case "reset-folder-format":
                EnsurePaneReady(pane);
                pane.ResetFolderFormat();
                _message.Text = "Folder format reset";
                break;
            case "reset-default-format":
                EnsurePaneReady(pane);
                var factory = new TabSettings();
                pane.ResetFolderFormat(_settings.UserDefaultFolderFormat ??
                    new LockedFolderFormat(factory.ViewMode, factory.SortColumn, factory.Descending, factory.Columns));
                _message.Text = "User Default format applied";
                break;
            case "select-all": pane.SelectAll(); break;
            case "select-pattern": await SelectByPatternAsync(pane); break;
            case "select-duplicates": await SelectDuplicatesAsync(pane); break;
            case "invert": pane.InvertSelection(); break;
            case "checkbox-mode": EnsurePaneReady(pane); pane.SetCheckboxMode(!pane.CheckboxMode); break;
            case "toggle-checkbox": EnsurePaneReady(pane); pane.ToggleFocusedCheckbox(); break;
            case "selection-to-checkboxes":
                EnsurePaneReady(pane);
                var selectedPaths = pane.SelectedEntries.Select(entry => entry.FullPath).ToArray();
                pane.SetCheckboxMode(true);
                pane.SelectActionPaths(selectedPaths);
                break;
            case "checkboxes-to-selection":
                EnsurePaneReady(pane);
                if (pane.CheckboxMode) pane.SelectPaths(pane.ActionEntries.Select(entry => entry.FullPath));
                break;
            case "reselect-files": await ReselectFilesAsync(pane); break;
            case "select-same-extensions":
                EnsurePaneReady(pane);
                var extensions = pane.ActionEntries.Where(entry => !entry.IsDirectory)
                    .Select(entry => entry.Extension.Normalize()).ToHashSet(StringComparer.OrdinalIgnoreCase);
                if (extensions.Count != 0)
                    pane.SelectActionPaths(pane.VisibleEntries.Where(entry => !entry.IsDirectory &&
                        extensions.Contains(entry.Extension.Normalize())).Select(entry => entry.FullPath));
                break;
            case "select-random":
                EnsurePaneReady(pane);
                if (pane.VisibleEntries.Count > 0)
                    pane.SelectActionPaths([pane.VisibleEntries[Random.Shared.Next(pane.VisibleEntries.Count)].FullPath]);
                break;
            case "select-empty-folders": await SelectFoldersByContentsAsync(pane, zeroBytes: false); break;
            case "select-zero-folders": await SelectFoldersByContentsAsync(pane, zeroBytes: true); break;
            case "select-source-selection":
            case "select-source-all":
            case "select-source-stems":
            case "select-source-missing": SelectAcrossPanes(pane, command); break;
            case "details-view": EnsurePaneReady(pane); pane.SetDetailsThumbnails(false); break;
            case "details-thumbnails": EnsurePaneReady(pane); pane.SetDetailsThumbnails(true); break;
            case "tiles-view": EnsurePaneReady(pane); pane.SetViewMode("Tiles"); break;
            case "focus-filter": pane.FocusFilter(); break;
            case "focus-search": _search.Focus(); _search.SelectAll(); break;
            case "search": await SearchAsync(); break;
            case "copy-files": await TransferSelectionAsync(false); break;
            case "move-files": await TransferSelectionAsync(true); break;
            case "duplicate":
                var duplicate = await RequireSelectionAsync(pane);
                await RunOperationAsync(new FileOperationRequest(FileOperationKind.Copy, duplicate.Select(x => x.FullPath).ToArray(), directoryAtStart, Collision: CollisionPolicy.KeepBoth));
                break;
            case "cut": await PutSelectionOnClipboardAsync(true); break;
            case "copy-clipboard": await PutSelectionOnClipboardAsync(false); break;
            case "hard-links": await TransferSelectionAsync(move: false, hardLink: true); break;
            case "update-all": await TransferSelectionAsync(move: false, update: CopyUpdateMode.All); break;
            case "update-existing": await TransferSelectionAsync(move: false, update: CopyUpdateMode.Existing); break;
            case "paste": await PasteAsync(); break;
            case "copy-paths":
                await CopyPathsAsync(pane);
                break;
            case "copy-names":
                await CopyPathsAsync(pane, namesOnly: true);
                break;
            case "rename": await RenameAsync(); break;
            case "advanced-rename": await AdvancedRenameAsync(); break;
            case "new-folder": await CreateEntryAsync(true); break;
            case "new-file": await CreateEntryAsync(false); break;
            case "trash":
                var trashSelection = pane.ActionEntries
                    .Select(entry => (Entry: entry, Identity: pane.IndexedIdentity(entry)))
                    .ToArray();
                var trash = await ResolveCapturedSelectionAsync(pane, trashSelection);
                if (trash.Count == 0) throw new InvalidOperationException("Select a file or folder first.");
                if (await Dialogs.ConfirmAsync(this, "Move to Trash", $"Move {trash.Count:N0} selected item(s) to the system Trash?\n\n" + string.Join("\n", trash.Take(8).Select(x => x.Name)), "Move to Trash"))
                {
                    var currentTrash = await ResolveCapturedSelectionAsync(pane, trashSelection);
                    await RunOperationAsync(new FileOperationRequest(
                        FileOperationKind.Trash,
                        currentTrash.Select(entry => entry.FullPath).ToArray()));
                }
                break;
            case "undo": await RunUndoAsync(); break;
            case "properties": await ShowPropertiesAsync(); break;
            case "quick-look": await _platform.QuickLookAsync((await RequireSelectionAsync(pane)).Select(x => x.FullPath).ToArray(), _shutdownCancellation.Token); break;
            case "reveal": await _platform.RevealAsync((await RequireSelectionAsync(pane))[0].FullPath, _shutdownCancellation.Token); break;
            case "terminal": await _platform.OpenTerminalAsync(pane.CurrentDirectory, _shutdownCancellation.Token); break;
            case "archive": await CreateArchiveAsync(); break;
            case "extract": await BrowseArchiveAsync(); break;
            case "duplicates": ShowDuplicatePanel(); break;
            case "duplicate-scan": if (_scanDuplicates is { } scanDuplicates) await scanDuplicates(); break;
            case "duplicate-panel-select": if (_duplicatePanelPane is { } duplicatePane) await SelectDuplicatesAsync(duplicatePane); break;
            case "print-folder": await PrintFolderAsync(); break;
            case "join-files": await JoinFilesAsync(pane); break;
            case "split-file": await SplitFileAsync(pane); break;
            case "file-type-summary": await ShowFileTypeSummaryAsync(pane); break;
            case "compare": await CompareFoldersAsync(); break;
            case "history": await ShowHistoryAsync(); break;
            case "settings": await PreferencesAsync(); break;
            case "favorite":
                var name = await Dialogs.PromptAsync(this, "Add Favorite", "Favorite name", Path.GetFileName(Path.TrimEndingDirectorySeparator(pane.CurrentDirectory)), "Add");
                if (!string.IsNullOrWhiteSpace(name))
                {
                    _settings.Favorites.RemoveAll(x => x.Path == pane.CurrentDirectory);
                    _settings.Favorites.Add(new FavoriteFolder(name.Trim(), pane.CurrentDirectory));
                    BuildTree(); RefreshFavoritesMenu(); RefreshBookmarkStrip();
                }
                break;
            case "save-layout":
            case "save-lister-layout": await SaveListerLayoutAsAsync(); break;
            case "lister-layouts": await ManageListerLayoutsAsync(); break;
            case "cancel-work": CancelWork(); break;
            case "quit": Close(); break;
            default: throw new ArgumentException($"Unknown command: {command}");
        }
        ScheduleSave();
    }

    private async Task<FileEntry> ResolveEntryAsync(PaneView pane, FileEntry entry)
    {
        EnsurePaneReady(pane);
        var identity = pane.IndexedIdentity(entry);
        return identity is null ? await Task.Run(() => DirectoryBrowser.ReadEntry(entry.FullPath)).WaitAsync(_shutdownCancellation.Token)
            : await _listary.RevalidateAsync(identity, _shutdownCancellation.Token);
    }

    private async Task<IReadOnlyList<FileEntry>> ResolveSelectionAsync(PaneView pane, bool useChecks = true)
    {
        var selected = (useChecks ? pane.ActionEntries : pane.SelectedEntries)
            .Select(entry => (Entry: entry, Identity: pane.IndexedIdentity(entry)))
            .ToArray();
        return await ResolveCapturedSelectionAsync(pane, selected);
    }

    private async Task<IReadOnlyList<FileEntry>> ResolveCapturedSelectionAsync(
        PaneView pane,
        IReadOnlyList<(FileEntry Entry, ListarySearchResult? Identity)> selected)
    {
        EnsureAcceptingWork(); EnsurePaneReady(pane);
        if (selected.All(item => item.Identity is null))
            return await Task.Run(() => selected.Select(item => DirectoryBrowser.ReadEntry(item.Entry.FullPath)).ToArray()).WaitAsync(_shutdownCancellation.Token);
        var entries = new List<FileEntry>(selected.Count);
        foreach (var (entry, identity) in selected)
            entries.Add(identity is null
                ? await Task.Run(() => DirectoryBrowser.ReadEntry(entry.FullPath)).WaitAsync(_shutdownCancellation.Token)
                : await _listary.RevalidateAsync(identity, _shutdownCancellation.Token));
        return entries;
    }

    private async Task<IReadOnlyList<FileEntry>> RequireSelectionAsync(PaneView pane)
    {
        var entries = await ResolveSelectionAsync(pane);
        EnsureAcceptingWork(); EnsurePaneReady(pane);
        if (entries.Count == 0) throw new InvalidOperationException("Select a file or folder first.");
        return entries;
    }

    private async Task OpenEntryAsync(PaneView pane, FileEntry entry)
    {
        var identity = pane.IndexedIdentity(entry);
        entry = await ResolveEntryAsync(pane, entry);
        EnsureAcceptingWork();
        if (entry.IsBrowsable)
            await pane.NavigateAsync(entry.FullPath, indexedDirectoryIdentity: identity is null ? null : identity with { Entry = entry });
        else if (entry.Extension.Equals(".zip", StringComparison.OrdinalIgnoreCase)) await BrowseArchiveAsync(entry, identity);
        else await _platform.OpenAsync(entry.FullPath, _shutdownCancellation.Token);
        if (identity is not null) await _listary.RecordActivationAsync(identity, _shutdownCancellation.Token);
    }

    private async Task UpdatePreviewAsync()
    {
        var generation = ++_previewGeneration;
        if (!_settings.ShowViewer) return;
        var pane = ActivePane;
        var entry = pane.SelectedEntries.FirstOrDefault();
        if (pane.IsLoading) return;
        if (entry is null) { _preview.Show(null); return; }
        try
        {
            entry = await ResolveEntryAsync(pane, entry);
            if (generation == _previewGeneration && !_disposing) _preview.Show(entry);
        }
        catch (Exception error)
        {
            if (generation == _previewGeneration) { _preview.Show(null); ShowError(error); }
        }
    }

    private async Task TransferSelectionAsync(bool move, bool hardLink = false, CopyUpdateMode? update = null)
    {
        var pane = ActivePane;
        EnsurePaneReady(pane);
        var destinationPane = _settings.DualPane ? OtherPane : null;
        if (destinationPane is not null) EnsurePaneReady(destinationPane);
        var sourceDirectoryAtStart = pane.CurrentDirectory;
        var destinationAtStart = destinationPane?.CurrentDirectory;
        var destinationIdentityAtStart = destinationPane?.CurrentDirectoryIndexedIdentity;
        var selected = pane.ActionEntries
            .Select(entry => (Entry: entry, Identity: pane.IndexedIdentity(entry)))
            .ToArray();
        if (selected.Length == 0) throw new InvalidOperationException("Select a file or folder first.");
        // Hold the actual ordinary-folder objects before any destination or
        // collision UI. Indexed items additionally retain their Listary owner.
        var reviewed = await ResolveCapturedSelectionAsync(pane, selected);
        RequireIndependentTransferSources(reviewed.Select(entry => entry.FullPath).ToArray());
        var sourceComparer = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var originalIds = reviewed.Select(entry => ListaryClient.ReadNativeItemIdentity(entry.FullPath)).ToArray();
        if (originalIds.Any(string.IsNullOrWhiteSpace))
            throw new IOException("The selected file identity is unavailable. Refresh the folder before copying or moving.");
        var hashSelectedContent = reviewed.Count <= MaxContentFingerprintItems;
        var originalVersions = await Task.Run(() => reviewed.Select(entry =>
            CaptureTransferFileVersion(entry.FullPath, hashSelectedContent)).ToArray()).WaitAsync(_shutdownCancellation.Token);
        var destinationId = destinationAtStart is null ? null : ListaryClient.ReadNativeItemIdentity(destinationAtStart);
        if (destinationAtStart is not null && string.IsNullOrWhiteSpace(destinationId))
            throw new IOException("The destination folder identity is unavailable. Refresh it before copying or moving.");
        var title = update switch
        {
            CopyUpdateMode.All => "Update All",
            CopyUpdateMode.Existing => "Update Existing",
            _ => hardLink ? "Create Hard Links" : move ? "Move Files" : "Copy Files"
        };
        var destination = destinationAtStart ?? await Dialogs.PromptAsync(this,
            title, "Destination folder", pane.CurrentDirectory, "Continue");
        if (destination is null) return;

        async Task<IReadOnlyList<string>> ResolveSelectedPathsAsync()
        {
            EnsureAcceptingWork(); EnsurePaneReady(pane);
            var paths = new List<string>(selected.Length);
            for (var index = 0; index < selected.Length; index++)
            {
                var (entry, identity) = selected[index];
                var current = identity is null
                    ? await Task.Run(() => DirectoryBrowser.ReadEntry(entry.FullPath)).WaitAsync(_shutdownCancellation.Token)
                    : await _listary.RevalidateAsync(identity, _shutdownCancellation.Token);
                var before = reviewed[index];
                if (!sourceComparer.Equals(current.FullPath, before.FullPath) ||
                    current.IsDirectory != before.IsDirectory || current.IsSymbolicLink != before.IsSymbolicLink ||
                    current.Length != before.Length || current.ModifiedUtc != before.ModifiedUtc ||
                    !string.Equals(originalIds[index], ListaryClient.ReadNativeItemIdentity(current.FullPath), StringComparison.Ordinal))
                    throw new IOException("A selected file changed during transfer confirmation. Refresh and select the current file before continuing.");
                paths.Add(current.FullPath);
            }
            var refreshedVersions = await Task.Run(() => paths.Select(path =>
                CaptureTransferFileVersion(path, hashSelectedContent)).ToArray())
                .WaitAsync(_shutdownCancellation.Token);
            if (!originalVersions.SequenceEqual(refreshedVersions))
                throw new IOException("A selected file changed during transfer confirmation. Refresh and select the current file before continuing.");
            EnsureAcceptingWork(); EnsurePaneReady(pane);
            return RequireIndependentTransferSources(paths);
        }

        async Task<string> ResolveDestinationAsync()
        {
            EnsureAcceptingWork();
            var resolved = DirectoryBrowser.ResolvePath(destination, sourceDirectoryAtStart);
            if (destinationPane is null) return resolved;
            EnsurePaneReady(destinationPane);
            var comparer = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
                ? StringComparer.OrdinalIgnoreCase
                : StringComparer.Ordinal;
            if (!comparer.Equals(destinationPane.CurrentDirectory, destinationAtStart))
                throw new IOException("The destination pane changed while the transfer was being confirmed. Run the operation again.");
            var current = destinationIdentityAtStart is null
                ? await Task.Run(() => DirectoryBrowser.ReadEntry(destinationAtStart!))
                    .WaitAsync(_shutdownCancellation.Token)
                : await _listary.RevalidateAsync(destinationIdentityAtStart, _shutdownCancellation.Token);
            if (!current.IsDirectory || !comparer.Equals(current.FullPath, resolved) ||
                !string.Equals(destinationId, ListaryClient.ReadNativeItemIdentity(current.FullPath), StringComparison.Ordinal))
                throw new IOException("The destination folder changed while the transfer was being confirmed. Run the operation again.");
            return current.FullPath;
        }

        var sources = await ResolveSelectedPathsAsync();
        var resolvedDestination = await ResolveDestinationAsync();
        if (update is not null)
        {
            if (sources.Any(path => !sourceComparer.Equals(Path.GetDirectoryName(path), sourceDirectoryAtStart)))
                throw new IOException("Open the containing folder before updating selected files.");
            var comparisonService = new FolderComparisonService();
            var names = sources.Select(path => Path.GetFileName(path)).ToArray();
            var comparison = await RunToolAsync("Comparing update timestamps", (token, progress) =>
                comparisonService.CompareAsync(sourceDirectoryAtStart, resolvedDestination,
                    new FolderComparisonOptions(CompareContents: false, IncludeHidden: true, TimestampTolerance: TimeSpan.Zero),
                    progress, token, names));
            var plan = comparisonService.BuildCopyPlan(comparison, ComparisonDirection.LeftToRight, update: update);
            if (plan.Requests.Count == 0)
            {
                _message.Text = "No selected files need updating. " + (plan.Warnings.FirstOrDefault() ?? "");
                return;
            }
            if (!await ConfirmCopyPlanAsync(this, plan, sourceDirectoryAtStart, resolvedDestination, title)) return;
            _ = await ResolveSelectedPathsAsync();
            _ = await ResolveDestinationAsync();
            try
            {
                await RunToolAsync(title, async (token, progress) =>
                {
                    var completed = 0;
                    var createdFolders = new Dictionary<string, string>(sourceComparer);
                    foreach (var request in plan.Requests)
                    {
                        token.ThrowIfCancellationRequested();
                        var expectedParent = request.ReviewedDestinationParentIdentity ??
                            createdFolders.GetValueOrDefault(request.DestinationDirectory!) ??
                            throw new IOException($"The update folder has no verified identity: {request.DestinationDirectory}");
                        var result = await RunOperationAsync(request with { ReviewedDestinationParentIdentity = expectedParent },
                            token, refresh: false, rememberSelection: false);
                        if (result.Canceled) throw new OperationCanceledException(token);
                        if (!result.Succeeded) throw new IOException($"Stopped after {completed} update operation(s). {result.Errors.FirstOrDefault()}");
                        foreach (var change in result.Changes)
                            if (change.NativeIdentity is { } identity && change.Destination is { } path)
                                createdFolders.Add(path, identity);
                        progress.Report(new ToolProgress(title, ++completed, plan.Requests.Count));
                    }
                    return completed;
                });
                _reselectItems = sources.Select((path, index) => (path, originalIds[index]!)).ToArray();
                _message.Text = $"{title} completed · {plan.Requests.Count:N0} operation(s)";
            }
            finally { await RefreshPanesAsync(afterFileOperation: true); }
            return;
        }
        if (hardLink)
        {
            await RunOperationAsync(new FileOperationRequest(FileOperationKind.HardLink, sources, resolvedDestination));
            return;
        }
        await TransferPathsAsync(
            sources,
            resolvedDestination,
            move,
            revalidateSources: ResolveSelectedPathsAsync,
            revalidateDestination: ResolveDestinationAsync);
    }

    private string ResolveHomeFolder(PaneView pane) => _settings.HomeFolder ?? pane.InitialDirectory;

    internal async Task TransferDroppedPathsAsync(PaneView target, IReadOnlyList<string> paths, bool move, bool toHome)
    {
        EnsurePaneReady(target);
        string CurrentDropDestination() => toHome ? ResolveHomeFolder(target) : target.CurrentDirectory;
        var destination = CurrentDropDestination();
        var destinationIdentity = ListaryClient.ReadNativeItemIdentity(destination);
        if (string.IsNullOrWhiteSpace(destinationIdentity))
            throw new IOException("The drop destination identity is unavailable. Refresh before dropping files.");
        var captured = RequireIndependentTransferSources(paths);
        var identities = captured.Select(ListaryClient.ReadNativeItemIdentity).ToArray();
        if (identities.Any(string.IsNullOrWhiteSpace))
            throw new IOException("A dropped item identity is unavailable. Select the current files and try again.");
        var hashDroppedContent = captured.Length <= MaxContentFingerprintItems;
        var versions = await Task.Run(() => captured.Select(path =>
            CaptureTransferFileVersion(path, hashDroppedContent)).ToArray())
            .WaitAsync(_shutdownCancellation.Token);
        var comparer = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

        async Task<IReadOnlyList<string>> ValidateSources()
        {
            EnsurePaneReady(target);
            var current = await Task.Run(() => captured.Select(path => (
                Identity: ListaryClient.ReadNativeItemIdentity(path),
                Version: CaptureTransferFileVersion(path, hashDroppedContent))).ToArray())
                .WaitAsync(_shutdownCancellation.Token);
            EnsureAcceptingWork(); EnsurePaneReady(target);
            for (var i = 0; i < captured.Length; i++)
                if (!string.Equals(identities[i], current[i].Identity, StringComparison.Ordinal) ||
                    versions[i] != current[i].Version)
                    throw new IOException("A dropped item changed during confirmation. Select it again.");
            return captured;
        }
        Task<string> ValidateDestination()
        {
            EnsurePaneReady(target);
            if (!comparer.Equals(CurrentDropDestination(), destination) ||
                !string.Equals(destinationIdentity, ListaryClient.ReadNativeItemIdentity(destination), StringComparison.Ordinal))
                throw new IOException("The drop destination changed during confirmation. Drop the items again.");
            return Task.FromResult(destination);
        }
        await TransferPathsAsync(captured, destination, move, ValidateSources, ValidateDestination);
    }

    private async Task<FileOperationResult?> TransferPathsAsync(
        IReadOnlyList<string> sources,
        string destination,
        bool move,
        Func<Task<IReadOnlyList<string>>>? revalidateSources = null,
        Func<Task<string>>? revalidateDestination = null)
    {
        EnsureAcceptingWork();
        if (sources.Count == 0) return null;
        destination = DirectoryBrowser.ResolvePath(destination, ActivePane.CurrentDirectory);
        static string DestinationFor(string source, string destinationDirectory) =>
            Path.Combine(destinationDirectory, Path.GetFileName(Path.TrimEndingDirectorySeparator(source)));
        var conflictComparer = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;
        string[] ConflictDestinations(IReadOnlyList<string> currentSources, string destinationDirectory)
        {
            return currentSources
                .Select(source => DestinationFor(source, destinationDirectory))
                .Where(target => File.Exists(target) || Directory.Exists(target))
                .Distinct(conflictComparer)
                .ToArray();
        }

        var conflicts = await Task.Run(() => ConflictDestinations(sources, destination));
        var hashConflictContent = conflicts.Length <= MaxContentFingerprintItems;
        // An existing collision target is part of the reviewed decision.
        // Replacing or editing it while the dialog is open invalidates that
        // decision, even if the same destination filename still exists.
        var reviewedConflicts = await Task.Run(() => conflicts.ToDictionary(
            path => path,
            path => (Identity: ListaryClient.ReadNativeItemIdentity(path),
                Version: CaptureTransferFileVersion(path, hashConflictContent)),
            conflictComparer));
        EnsureAcceptingWork();
        var collision = conflicts.Length == 0 ? CollisionPolicy.Fail : await Dialogs.CollisionAsync(this, conflicts, destination);
        if (collision is null) return null;
        if (revalidateDestination is not null)
        {
            var refreshedDestination = await revalidateDestination();
            if (!conflictComparer.Equals(
                Path.TrimEndingDirectorySeparator(refreshedDestination),
                Path.TrimEndingDirectorySeparator(destination)))
                throw new IOException("The destination changed while the transfer was being confirmed. Run the operation again.");
        }
        if (revalidateSources is not null)
        {
            var refreshed = await revalidateSources();
            RequireTransferSourcesUnchanged(sources, refreshed, conflictComparer);
            var refreshedConflicts = await Task.Run(() => ConflictDestinations(refreshed, destination));
            if (!conflicts.ToHashSet(conflictComparer).SetEquals(refreshedConflicts))
                throw new IOException("The selected items or destination changed while the collision decision was pending. Run the operation again.");
            sources = refreshed;
        }
        if (conflicts.Length > 0)
        {
            var currentConflicts = await Task.Run(() => ConflictDestinations(sources, destination));
            var currentReviews = await Task.Run(() => currentConflicts.ToDictionary(
                path => path,
                path => (Identity: ListaryClient.ReadNativeItemIdentity(path),
                    Version: CaptureTransferFileVersion(path, hashConflictContent)),
                conflictComparer)).WaitAsync(_shutdownCancellation.Token);
            EnsureAcceptingWork();
            if (!conflicts.ToHashSet(conflictComparer).SetEquals(currentConflicts) ||
                currentConflicts.Any(path => !reviewedConflicts.TryGetValue(path, out var approved) ||
                    !currentReviews.TryGetValue(path, out var current) ||
                    !string.Equals(approved.Identity, current.Identity, StringComparison.Ordinal) ||
                    approved.Version != current.Version))
                throw new IOException("A collision target changed during confirmation. Review the destination again.");
        }
        return await RunOperationAsync(new FileOperationRequest(move ? FileOperationKind.Move : FileOperationKind.Copy, sources, destination, Collision: collision.Value));
    }

    internal static void RequireTransferSourcesUnchanged(IReadOnlyList<string> approved,
        IReadOnlyList<string> revalidated, StringComparer comparer)
    {
        if (revalidated.Count != approved.Count)
            throw new IOException("The selected items changed while the transfer was being confirmed. Run the operation again.");
        if (!revalidated.SequenceEqual(approved, comparer))
            throw new IOException("A selected file moved or changed path during transfer confirmation. Run the operation again.");
    }

    private IClipboard RequireClipboard() => Clipboard ?? throw new InvalidOperationException("The system clipboard is unavailable.");

    private async Task PutSelectionOnClipboardAsync(bool cut)
    {
        var generation = ++_clipboardRequestGeneration;
        var pane = ActivePane;
        var selected = pane.ActionEntries.Select(entry => (Entry: entry, Identity: pane.IndexedIdentity(entry))).ToArray();
        if (selected.Length == 0) throw new InvalidOperationException("Select a file or folder first.");
        var entries = await ResolveCapturedSelectionAsync(pane, selected);
        RequireIndependentTransferSources(entries.Select(entry => entry.FullPath).ToArray());
        var reselect = await CaptureReselectItemsAsync(entries.Select(entry => entry.FullPath));
        var indexed = selected.Select((item, index) => item.Identity is null
            ? null : item.Identity with { Entry = entries[index] }).ToArray();
        var nativeIds = await Task.Run(() => entries.Select(entry =>
            ListaryClient.ReadNativeItemIdentity(entry.FullPath)).ToArray()).WaitAsync(_shutdownCancellation.Token);
        if (nativeIds.Any(string.IsNullOrWhiteSpace))
            throw new IOException("A selected file identity is unavailable. Refresh and copy the selection again.");
        var transfer = new DataTransfer();
        foreach (var entry in entries)
        {
            IStorageItem? item = entry.IsDirectory ? await StorageProvider.TryGetFolderFromPathAsync(entry.FullPath)
                : await StorageProvider.TryGetFileFromPathAsync(entry.FullPath);
            if (item is null) { ((IDisposable)transfer).Dispose(); throw new IOException($"The selected file could not be placed on the clipboard: {entry.FullPath}"); }
            transfer.Add(DataTransferItem.CreateFile(item));
        }
        transfer.Add(DataTransferItem.CreateText(string.Join(Environment.NewLine, entries.Select(x => x.FullPath))));
        await _clipboardPublishGate.WaitAsync(_shutdownCancellation.Token);
        try
        {
            EnsureAcceptingWork();
            if (generation != _clipboardRequestGeneration) { ((IDisposable)transfer).Dispose(); return; }
            await RequireClipboard().SetDataAsync(transfer);
            _clipboardPaths = entries.Select(x => x.FullPath).ToList();
            _clipboardCut = cut; _clipboardTransfer = transfer; _clipboardIndexed = indexed;
            _clipboardNativeIds = nativeIds.Select(id => id!).ToArray();
            _reselectItems = reselect;
            _message.Text = $"{entries.Count:N0} item(s) {(cut ? "cut; Paste in this app moves them" : "copied to the system clipboard")}";
        }
        finally { _clipboardPublishGate.Release(); }
    }

    internal void ApplyListerStyle(ListerStyle style)
    {
        _settings.IndependentFolderTrees = style is ListerStyle.DualHorizontal or ListerStyle.DualVertical;
        _settings.DualPane = style == ListerStyle.Commander || _settings.IndependentFolderTrees;
        if (_settings.DualPane) _settings.SplitOrientation = style == ListerStyle.DualHorizontal ? "Horizontal" : "Vertical";
        _settings.ShowTree = _settings.IndependentFolderTrees || style is ListerStyle.Explorer or ListerStyle.Images;
        _settings.ShowViewer = style is ListerStyle.Images or ListerStyle.Filmstrip;
        ApplyLayout();
        var viewMode = style switch { ListerStyle.Images => "Tiles", ListerStyle.Filmstrip => "Filmstrip", _ => null };
        if (viewMode is not null) ActivePane.SetViewMode(viewMode);
        else if (ActivePane.CurrentViewMode == "Filmstrip") ActivePane.SetViewMode("Details");
        ActivePane.FocusList();
    }

    private async Task SelectByPatternAsync(PaneView pane)
    {
        EnsurePaneReady(pane);
        var revision = pane.ViewRevision;
        var input = new TextBox { Name = "SelectionPattern", Watermark = "*.txt or *.(jpg|png)", MaxLength = 512 };
        var action = new ComboBox { Name = "PatternSelectionAction", ItemsSource = new[] { "Select matching items", "Deselect matching items" }, SelectedIndex = 0 };
        var kind = new ComboBox { Name = "PatternSelectionKind", ItemsSource = new[] { "Files and folders", "Files only", "Folders only" }, SelectedIndex = 0 };
        var minimumSize = new NumericUpDown { Name = "PatternSelectionMinimumKiB", Minimum = 0, Maximum = 1_000_000_000, Value = 0 };
        var modifiedDays = new NumericUpDown { Name = "PatternSelectionModifiedDays", Minimum = 0, Maximum = 36500, Value = 0 };
        var partial = new CheckBox { Content = "Partial match" };
        var deselect = new CheckBox { Content = "Deselect on no match", IsChecked = true };
        action.SelectionChanged += (_, _) => deselect.IsEnabled = action.SelectedIndex == 0;
        var feedback = Ui.Text("", true);
        var panel = new StackPanel { Margin = new Thickness(16), Spacing = 8,
            Children = { Dialogs.Form(("Action", action), ("Item type", kind), ("Minimum file size (KiB)", minimumSize),
                ("Modified within days", modifiedDays)), Ui.Text("Use 0 to ignore a metadata criterion.", true),
                Ui.Text("Filename pattern", true), input, partial, deselect, feedback } };
        var dialog = Dialogs.Window("Select by Pattern", panel, 560, 435);
        panel.Children.Add(Ui.Row(Ui.Button("Cancel", () => dialog.Close(null)), Ui.Button("Select", () =>
        {
            var pattern = input.Text?.Trim();
            if (string.IsNullOrEmpty(pattern))
            {
                if ((minimumSize.Value ?? 0) <= 0 && (modifiedDays.Value ?? 0) <= 0)
                { feedback.Text = "Enter a filename pattern or a metadata criterion."; return; }
                pattern = "*";
            }
            var start = pattern.IndexOf('(');
            var end = pattern.IndexOf(')');
            string[] expressions = [pattern];
            if (start >= 0 || end >= 0 || pattern.Contains('|'))
            {
                if (start < 0 || end <= start || pattern.IndexOf('(', start + 1) >= 0 ||
                    pattern.IndexOf(')', end + 1) >= 0 || pattern[..start].Contains('|') || pattern[(end + 1)..].Contains('|'))
                { feedback.Text = "Use one group of alternatives, such as *.(jpg|png)."; return; }
                var options = pattern[(start + 1)..end].Split('|');
                if (options.Length is < 2 or > 32 || options.Any(string.IsNullOrWhiteSpace))
                { feedback.Text = "Enter two to 32 nonempty alternatives."; return; }
                expressions = options.Select(option => pattern[..start] + option + pattern[(end + 1)..]).ToArray();
            }
            if (partial.IsChecked == true)
                expressions = expressions.Select(expression => $"*{expression}*").ToArray();
            dialog.Close(expressions);
        })));
        dialog.Opened += (_, _) => input.Focus();
        if (await dialog.ShowDialog<string[]?>(this) is not { } patterns) return;
        EnsurePaneReady(pane);
        if (pane.ViewRevision != revision)
            throw new IOException("The folder or filter changed. Reopen Select by Pattern for the current file list.");
        var visible = pane.VisibleEntries;
        var itemType = kind.SelectedIndex;
        var minimumBytes = (minimumSize.Value ?? 0) * 1024;
        var days = modifiedDays.Value ?? 0;
        var modifiedSince = days > 0 ? DateTimeOffset.UtcNow.AddDays(-(double)days) : DateTimeOffset.MinValue;
        var matches = await Task.Run(() => visible.Where(entry =>
            (itemType == 0 || entry.IsDirectory == (itemType == 2)) &&
            (minimumBytes <= 0 || !entry.IsDirectory && entry.Length >= minimumBytes) &&
            entry.ModifiedUtc >= modifiedSince && patterns.Any(pattern =>
                System.IO.Enumeration.FileSystemName.MatchesSimpleExpression(pattern, entry.Name, ignoreCase: true)))
            .Select(entry => entry.FullPath).ToArray()).WaitAsync(_shutdownCancellation.Token);
        EnsurePaneReady(pane);
        if (pane.ViewRevision != revision)
            throw new IOException("The folder or filter changed before selection could be applied.");
        var selection = pane.ActionEntries.Select(entry => entry.FullPath);
        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        pane.SelectActionPaths(action.SelectedIndex == 1 ? selection.Except(matches, comparer) :
            deselect.IsChecked == true ? matches : selection.Concat(matches));
    }

    private void SelectAcrossPanes(PaneView source, string command)
    {
        if (!_settings.DualPane) throw new InvalidOperationException("Open a destination pane to compare selections.");
        var destination = OtherPane;
        EnsurePaneReady(source);
        EnsurePaneReady(destination);
        var stems = command == "select-source-stems";
        var missing = command == "select-source-missing";
        string Name(FileEntry entry) => (stems && !entry.IsDirectory ? Path.GetFileNameWithoutExtension(entry.Name) : entry.Name).Normalize();
        if (missing)
        {
            var destinationNames = destination.VisibleEntries.Select(Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            source.SelectActionPaths(source.VisibleEntries.Where(entry => !destinationNames.Contains(Name(entry))).Select(entry => entry.FullPath));
            return;
        }
        var sourceRows = command == "select-source-selection" ? source.ActionEntries : source.VisibleEntries;
        var sourceNames = sourceRows.Select(Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        destination.SelectActionPaths(destination.VisibleEntries.Where(entry => sourceNames.Contains(Name(entry))).Select(entry => entry.FullPath));
    }

    private async Task SelectFoldersByContentsAsync(PaneView pane, bool zeroBytes)
    {
        EnsurePaneReady(pane);
        var revision = pane.ViewRevision;
        var folders = pane.VisibleEntries.Where(entry => entry.IsBrowsable && !entry.IsSymbolicLink).ToArray();
        var result = await RunToolAsync(zeroBytes ? "Finding zero-byte folders" : "Finding empty folders", (token, progress) => Task.Run(() =>
        {
            var selected = new List<string>();
            var errors = new List<string>();
            for (var index = 0; index < folders.Length; index++)
            {
                token.ThrowIfCancellationRequested();
                var folder = folders[index];
                try
                {
                    var current = DirectoryBrowser.ReadEntry(folder.FullPath);
                    if (!current.IsBrowsable || current.IsSymbolicLink)
                        throw new IOException($"The folder changed during selection: {folder.FullPath}");
                    var match = zeroBytes
                        ? DirectoryBrowser.CalculateFolderSize(folder.FullPath, token) == 0
                        : !Directory.EnumerateFileSystemEntries(folder.FullPath).Any();
                    if (match) selected.Add(folder.FullPath);
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or OverflowException)
                { errors.Add(error.Message); }
                progress.Report(new ToolProgress("Checking folders", index + 1, folders.Length));
            }
            return (Selected: selected, Errors: errors);
        }, token));
        EnsurePaneReady(pane);
        if (pane.ViewRevision != revision) throw new IOException("The folder or filter changed while checking folder contents.");
        pane.SelectActionPaths(result.Selected);
        _message.Text = $"Selected {result.Selected.Count:N0} folders" + (result.Errors.Count > 0
            ? $" · {result.Errors.Count:N0} unavailable · {result.Errors[0]}" : "");
    }

    private async Task CopyPathsAsync(PaneView pane, bool namesOnly = false)
    {
        var generation = ++_clipboardRequestGeneration;
        var entries = await RequireSelectionAsync(pane);
        var reselect = await CaptureReselectItemsAsync(entries.Select(entry => entry.FullPath));
        await _clipboardPublishGate.WaitAsync(_shutdownCancellation.Token);
        try
        {
            EnsureAcceptingWork();
            if (generation != _clipboardRequestGeneration) return;
            await RequireClipboard().SetTextAsync(string.Join(Environment.NewLine,
                entries.Select(entry => namesOnly ? entry.Name : entry.FullPath)));
            _clipboardPaths.Clear(); _clipboardIndexed = []; _clipboardCut = false; _clipboardTransfer = null;
            _clipboardNativeIds = [];
            _reselectItems = reselect;
            _message.Text = namesOnly ? "Selected names copied" : "Selected paths copied";
        }
        finally { _clipboardPublishGate.Release(); }
    }

    private static Task<(string Path, string Identity)[]> CaptureReselectItemsAsync(IEnumerable<string> paths) => Task.Run(() =>
    {
        var remembered = new List<(string Path, string Identity)>();
        foreach (var path in paths)
        {
            try
            {
                if (ListaryClient.ReadNativeItemIdentity(path) is { } identity) remembered.Add((path, identity));
            }
            catch (Exception error) when (error is ArgumentException or IOException or UnauthorizedAccessException)
            { System.Diagnostics.Trace.TraceWarning("Could not remember selection for {0}: {1}", path, error.Message); }
        }
        return remembered.ToArray();
    });

    private async Task ReselectFilesAsync(PaneView pane)
    {
        EnsurePaneReady(pane);
        var revision = pane.ViewRevision;
        var previous = _reselectItems;
        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var visible = pane.VisibleEntries.Select(entry => entry.FullPath).ToHashSet(comparer);
        var paths = await Task.Run(() => previous.Where(item => visible.Contains(item.Path) &&
            ListaryClient.ReadNativeItemIdentity(item.Path) == item.Identity).Select(item => item.Path).ToArray());
        EnsurePaneReady(pane);
        if (revision != pane.ViewRevision || !ReferenceEquals(previous, _reselectItems))
            throw new IOException("The file display or previous command changed. Run Reselect Files again.");
        if (paths.Length == 0) { _message.Text = "No files from the previous command are visible."; return; }
        pane.SelectActionPaths(pane.ActionEntries.Select(entry => entry.FullPath).Concat(paths));
        _message.Text = $"Reselected {paths.Length:N0} item(s)";
    }

    private async Task PasteAsync()
    {
        var targetPane = ActivePane;
        EnsurePaneReady(targetPane);
        var destination = targetPane.CurrentDirectory;
        var destinationId = ListaryClient.ReadNativeItemIdentity(destination);
        if (string.IsNullOrWhiteSpace(destinationId))
            throw new IOException("The Paste destination identity is unavailable. Refresh the folder and try again.");
        var pendingTransfer = _clipboardTransfer;
        var pendingPaths = _clipboardPaths.ToArray();
        var pendingIndexed = _clipboardIndexed;
        var pendingNativeIds = _clipboardNativeIds.ToArray();
        var pendingCut = _clipboardCut;
        var clipboard = RequireClipboard();
        var inProcess = await clipboard.TryGetInProcessDataAsync();
        var own = pendingTransfer is not null && ReferenceEquals(pendingTransfer, inProcess);
        // Windows external file drops carry their Copy/Cut intent separately
        // from CF_HDROP. Capture it before Avalonia reads the file list so a
        // clipboard change during that read cannot silently change the action.
        var pendingExternalIntent = !own && OperatingSystem.IsWindows()
            ? _windowsClipboardIntentReader.Capture()
            : null;
        async Task<string[]> RevalidateOwnSourcesAsync() => await RevalidateClipboardSourcesAsync(
            pendingPaths, pendingIndexed, identity => _listary.RevalidateAsync(identity, _shutdownCancellation.Token),
            _shutdownCancellation.Token);
        var externalItems = own ? null : (await clipboard.TryGetFilesAsync())?.ToArray();
        var sources = own ? await RevalidateOwnSourcesAsync()
            : externalItems is null ? [] : RequireCompleteClipboardPaths(externalItems.Select(x => x.TryGetLocalPath()).ToArray());
        if (sources.Length == 0) throw new InvalidOperationException("The clipboard does not contain local files or folders.");
        var externalDecision = WindowsClipboardTransferDecision.Copy;
        if (!own && OperatingSystem.IsWindows())
        {
            if (pendingExternalIntent is null)
                throw new IOException("The Windows clipboard does not expose a stable file selection with an explicit Copy or Cut intent. Copy or Cut again in Explorer.");
            externalDecision = pendingExternalIntent.DecideFor(sources, _windowsClipboardIntentReader.CurrentSequence);
            if (externalDecision == WindowsClipboardTransferDecision.Refuse)
                throw new IOException("The Windows clipboard selection or its Copy/Cut intent changed while Paste was being prepared. Copy or Cut again in Explorer.");
        }
        var capturedNativeIds = own ? pendingNativeIds : await Task.Run(() =>
            sources.Select(ListaryClient.ReadNativeItemIdentity).Select(id => id ?? "").ToArray())
            .WaitAsync(_shutdownCancellation.Token);
        if (capturedNativeIds.Length != sources.Length || capturedNativeIds.Any(string.IsNullOrWhiteSpace))
            throw new IOException("The clipboard contains an unavailable or changed file. Copy the selection again.");
        var hashClipboardContent = sources.Length <= MaxContentFingerprintItems;
        var capturedVersions = await Task.Run(() => sources.Select(path =>
            CaptureTransferFileVersion(path, hashClipboardContent)).ToArray())
            .WaitAsync(_shutdownCancellation.Token);
        var comparer = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        async Task<IReadOnlyList<string>> RevalidatePasteSourcesAsync()
        {
            EnsureAcceptingWork();
            if (own)
            {
                if (!ReferenceEquals(pendingTransfer, await clipboard.TryGetInProcessDataAsync()))
                    throw new IOException("The clipboard selection changed during Paste confirmation. Paste the current selection again.");
            }
            else
            {
                var currentClipboardItems = (await clipboard.TryGetFilesAsync())?.ToArray();
                if (currentClipboardItems is null)
                    throw new IOException("The clipboard selection changed during Paste confirmation. Paste the current selection again.");
                var currentClipboardPaths = RequireCompleteClipboardPaths(
                    currentClipboardItems.Select(item => item.TryGetLocalPath()).ToArray());
                if (!currentClipboardPaths.SequenceEqual(sources, OperatingSystem.IsWindows() ? StringComparer.Ordinal : comparer))
                    throw new IOException("The clipboard selection changed during Paste confirmation. Paste the current selection again.");
                if (OperatingSystem.IsWindows())
                {
                    var currentIntent = _windowsClipboardIntentReader.Capture();
                    var currentDecision = pendingExternalIntent?.DecideIfCurrent(currentIntent)
                        ?? WindowsClipboardTransferDecision.Refuse;
                    if (currentDecision == WindowsClipboardTransferDecision.Refuse || currentDecision != externalDecision)
                        throw new IOException("The Windows clipboard Copy/Cut intent or source selection changed during Paste confirmation. Paste the current selection again.");
                }
            }
            var current = own ? await RevalidateOwnSourcesAsync() : sources;
            RequireTransferSourcesUnchanged(sources, current, comparer);
            var identities = await Task.Run(() =>
                current.Select(ListaryClient.ReadNativeItemIdentity).ToArray())
                .WaitAsync(_shutdownCancellation.Token);
            if (identities.Length != capturedNativeIds.Length ||
                identities.Where((id, index) => !string.Equals(id, capturedNativeIds[index], StringComparison.Ordinal)).Any())
                throw new IOException("A clipboard file changed during Paste confirmation. Copy the selection again.");
            var versions = await Task.Run(() => current.Select(path =>
                CaptureTransferFileVersion(path, hashClipboardContent)).ToArray())
                .WaitAsync(_shutdownCancellation.Token);
            if (!versions.SequenceEqual(capturedVersions))
                throw new IOException("A clipboard file changed during Paste confirmation. Copy the selection again.");
            return current;
        }
        Task<string> RevalidatePasteDestinationAsync()
        {
            EnsureAcceptingWork(); EnsurePaneReady(targetPane);
            if (!ReferenceEquals(ActivePane, targetPane) ||
                !comparer.Equals(targetPane.CurrentDirectory, destination) ||
                !string.Equals(destinationId, ListaryClient.ReadNativeItemIdentity(destination), StringComparison.Ordinal))
                throw new IOException("The Paste destination changed during confirmation. Select it again.");
            return Task.FromResult(destination);
        }
        _ = await RevalidatePasteSourcesAsync();
        var moved = own ? pendingCut : externalDecision == WindowsClipboardTransferDecision.Move;
        var result = await TransferPathsAsync(sources, destination, moved,
            revalidateSources: RevalidatePasteSourcesAsync,
            revalidateDestination: RevalidatePasteDestinationAsync);
        if (own && moved && result is not null && ReferenceEquals(_clipboardTransfer, pendingTransfer))
        {
            // Consume only this app's cut state. Clearing the OS clipboard after asynchronous work
            // could erase newer clipboard content from another application.
            var committed = result.Changes.Where(change => change.Action == "Move").Select(change => change.Source).ToHashSet(comparer);
            var remaining = Enumerable.Range(0, pendingPaths.Length).Where(index => !committed.Contains(sources[index])).ToArray();
            _clipboardPaths = remaining.Select(index => pendingPaths[index]).ToList();
            _clipboardIndexed = remaining.Select(index => pendingIndexed[index]).ToArray();
            _clipboardNativeIds = remaining.Select(index => pendingNativeIds[index]).ToArray();
            _clipboardCut = _clipboardPaths.Count > 0;
        }
    }

    internal static string[] RequireCompleteClipboardPaths(IReadOnlyList<string?> paths)
    {
        if (paths.Any(string.IsNullOrWhiteSpace))
            throw new IOException("The clipboard selection contains an item without a local file path. No items were pasted.");
        return RequireIndependentTransferSources(paths.Select(path => path!).ToArray());
    }

    // File identity, length and modification time alone cannot detect a same-size
    // in-place edit whose timestamp is restored during a collision dialog. Hash
    // small files at the existing review boundaries, with a maximum of 16 MiB
    // per captured batch so large videos and selections avoid full-content scans.
    // Small Windows directory trees also retain a bounded child manifest so
    // a nested edit cannot invalidate only part of a reviewed Move batch.
    [StructLayout(LayoutKind.Sequential)]
    private struct WindowsBasicFileInfo
    {
        public long CreationTime;
        public long LastAccessTime;
        public long LastWriteTime;
        public long ChangeTime;
        public uint FileAttributes;
    }

    [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TryGetWindowsBasicFileInfo(SafeFileHandle handle, int infoClass,
        out WindowsBasicFileInfo info, int size);

    private static long? WindowsChangeTime(string path)
    {
        if (!OperatingSystem.IsWindows()) return null;
        // FileBasicInfo (0) gives Windows' change-time field. Unlike mtime,
        // this records in-place metadata/content edits even when applications
        // restore the last-write timestamp. Unavailable fields retain the
        // existing size/mtime/hash contract (for example on other filesystems).
        using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        return TryGetWindowsBasicFileInfo(handle, 0, out var info, Marshal.SizeOf<WindowsBasicFileInfo>())
            ? info.ChangeTime : null;
    }

    private static string? CaptureSmallDirectoryFingerprint(string root)
    {
        if (!OperatingSystem.IsWindows() ||
            (File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
            return null;

        const int maxEntries = 256;
        const int maxDepth = 16;
        // Breadth-first inspection gives sibling folders a chance to be
        // checked before a single large subtree consumes the bounded budget.
        var pending = new Queue<(string Path, int Depth)>();
        var records = new List<string>();
        var truncated = false;
        pending.Enqueue((root, 0));
        while (pending.Count > 0 && !truncated)
        {
            var (directory, depth) = pending.Dequeue();
            foreach (var child in Directory.EnumerateFileSystemEntries(directory))
            {
                if (records.Count >= maxEntries || depth >= maxDepth)
                {
                    // Losing the whole fingerprint at the first overflow left
                    // even the already-inspected children unprotected during
                    // collision review. Keep a plainly marked partial digest
                    // instead, with the same strict I/O and recursion bounds.
                    truncated = true;
                    break;
                }
                var attributes = File.GetAttributes(child);
                var relative = Path.GetRelativePath(root, child);
                var directoryChild = (attributes & FileAttributes.Directory) != 0;
                var reparsePoint = (attributes & FileAttributes.ReparsePoint) != 0;
                if (directoryChild)
                {
                    var target = reparsePoint ? new DirectoryInfo(child).LinkTarget : null;
                    // Paths, attributes and empty directory contents do not
                    // distinguish a directory replaced at the same path while
                    // a collision decision is pending. Keep its native file
                    // identity alongside the bounded manifest entry.
                    var nativeIdentity = ListaryClient.ReadNativeItemIdentity(child);
                    records.Add($"D:{relative.Length}:{relative}:{(int)attributes}:{target?.Length ?? -1}:{target}:{nativeIdentity}");
                    if (!reparsePoint) pending.Enqueue((child, depth + 1));
                }
                else if (reparsePoint)
                {
                    // A link is part of the selected directory, but its target
                    // may be missing or outside the folder. Capture the link
                    // itself without opening, hashing or statting that target.
                    var target = new FileInfo(child).LinkTarget;
                    records.Add($"L:{relative.Length}:{relative}:{(int)attributes}:{target?.Length ?? -1}:{target}");
                }
                else
                {
                    var info = new FileInfo(child);
                    records.Add($"F:{relative.Length}:{relative}:{(int)attributes}:{info.Length}:{info.LastWriteTimeUtc.Ticks}:{WindowsChangeTime(child)}");
                }
            }
        }
        records.Sort(StringComparer.Ordinal);
        var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", records))));
        return truncated ? "partial:" + fingerprint : fingerprint;
    }

    private static (long Length, DateTime ModifiedUtc, string? SmallContentSha256, long? WindowsChangeTime, string? DirectoryFingerprint)? CaptureTransferFileVersion(
        string path, bool hashSmallContent)
    {
        var attributes = File.GetAttributes(path);
        if ((attributes & FileAttributes.Directory) != 0)
            return (0, DateTime.MinValue, null, null, CaptureSmallDirectoryFingerprint(path));
        if ((attributes & FileAttributes.ReparsePoint) != 0)
        {
            // Never inspect a link's outside target as if its content were
            // the selected file. Its native identity is checked separately.
            var linkTarget = new FileInfo(path).LinkTarget;
            return (0, DateTime.MinValue, linkTarget, null, null);
        }
        var file = new FileInfo(path);
        var length = file.Length;
        var modifiedUtc = file.LastWriteTimeUtc;
        string? digest = null;
        if (hashSmallContent && length <= 1024 * 1024)
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete, 64 * 1024, FileOptions.SequentialScan);
            if (stream.Length != length)
                throw new IOException("The selected file size changed during transfer verification.");
            digest = Convert.ToHexString(SHA256.HashData(stream));
            file.Refresh();
            if (file.Length != length || file.LastWriteTimeUtc != modifiedUtc)
                throw new IOException("The selected file changed during transfer verification.");
        }
        return (length, modifiedUtc, digest, WindowsChangeTime(path), null);
    }

    // All three UI transfer routes use this lexical, bounded admission check
    // before presenting collision decisions. Moving a directory and a selected
    // child as two separate operations can otherwise produce a partial batch.
    internal static string[] RequireIndependentTransferSources(IReadOnlyList<string> paths)
    {
        var normalized = paths.Select(path =>
        {
            if (string.IsNullOrWhiteSpace(path))
                throw new IOException("A transfer source has no local file path. No items were transferred.");
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        }).ToArray();
        var comparer = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var selected = normalized.ToHashSet(comparer);
        if (selected.Count != normalized.Length)
            throw new IOException("The clipboard selection contains duplicate file paths. No items were pasted.");
        foreach (var path in normalized)
        {
            var parent = Path.GetDirectoryName(path);
            while (parent is not null)
            {
                if (selected.Contains(parent))
                    throw new IOException("The transfer selection contains overlapping folder and child paths. Select the parent folder once.");
                var next = Path.GetDirectoryName(parent);
                if (string.IsNullOrEmpty(next) || comparer.Equals(next, parent)) break;
                parent = next;
            }
        }
        return normalized;
    }

    internal static async Task<string[]> RevalidateClipboardSourcesAsync(
        IReadOnlyList<string> capturedPaths,
        IReadOnlyList<ListarySearchResult?> indexedByPosition,
        Func<ListarySearchResult, Task<FileEntry>> revalidateIndexed,
        CancellationToken cancellationToken = default)
    {
        if (capturedPaths.Count != indexedByPosition.Count)
            throw new IOException("Clipboard file identities no longer match the captured selection. Copy the files again.");
        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var resolved = new string[capturedPaths.Count];
        for (var index = 0; index < capturedPaths.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = Path.GetFullPath(capturedPaths[index]);
            var identity = indexedByPosition[index];
            if (identity is null) { resolved[index] = path; continue; }
            if (!comparer.Equals(Path.GetFullPath(identity.Entry.FullPath), path))
                throw new IOException("Clipboard indexed identity does not match its captured file. Copy the files again.");
            var current = await revalidateIndexed(identity);
            cancellationToken.ThrowIfCancellationRequested();
            resolved[index] = Path.GetFullPath(current.FullPath);
        }
        if (resolved.Distinct(comparer).Count() != resolved.Length)
            throw new IOException("Clipboard file identities now resolve to duplicate sources. Copy the files again.");
        return RequireIndependentTransferSources(resolved);
    }

    private async Task RenameAsync()
    {
        var pane = ActivePane;
        var selected = await RequireSelectionAsync(pane);
        if (selected.Count > 1) { await AdvancedRenameAsync(); return; }
        if (!pane.CheckboxMode && pane.BeginInlineRename()) return;
        var entry = selected[0];
        var name = await Dialogs.PromptAsync(this, "Rename", "New name", entry.Name, "Rename");
        if (name is not null) await RenameEntryAsync(pane, entry, name);
    }

    private async Task RenameEntryAsync(PaneView pane, FileEntry entry, string name)
    {
        entry = await ResolveEntryAsync(pane, entry);
        EnsureAcceptingWork();
        if (name == entry.Name) return;
        var result = await RunOperationAsync(new FileOperationRequest(FileOperationKind.Rename, [entry.FullPath], NewName: name));
        if (result.Succeeded) pane.SelectPaths([Path.Combine(Path.GetDirectoryName(entry.FullPath)!, name)]);
    }

    private async Task CreateEntryAsync(bool directory)
    {
        var pane = ActivePane;
        EnsurePaneReady(pane);
        var destination = pane.CurrentDirectory;
        var destinationIdentity = pane.CurrentDirectoryIndexedIdentity;
        var destinationNativeIdentity = ListaryClient.ReadNativeItemIdentity(destination);
        if (string.IsNullOrWhiteSpace(destinationNativeIdentity))
            throw new IOException("The destination folder identity is unavailable. Refresh before creating a file or folder.");
        var name = await Dialogs.PromptAsync(this, directory ? "New Folder" : "New Text File", "Name", directory ? "New folder" : "New file.txt", "Create");
        if (name is null) return;
        EnsureAcceptingWork(); EnsurePaneReady(pane);
        var comparer = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;
        if (!comparer.Equals(pane.CurrentDirectory, destination))
            throw new IOException("The destination folder changed while the create prompt was open. Run the operation again.");
        var currentDestination = destinationIdentity is null
            ? await Task.Run(() => DirectoryBrowser.ReadEntry(destination)).WaitAsync(_shutdownCancellation.Token)
            : await _listary.RevalidateAsync(destinationIdentity, _shutdownCancellation.Token);
        if (!currentDestination.IsDirectory || !comparer.Equals(currentDestination.FullPath, destination) ||
            !string.Equals(destinationNativeIdentity, ListaryClient.ReadNativeItemIdentity(currentDestination.FullPath), StringComparison.Ordinal))
            throw new IOException("The destination folder changed while the create prompt was open. Run the operation again.");
        var result = await RunOperationAsync(new FileOperationRequest(
            directory ? FileOperationKind.CreateDirectory : FileOperationKind.CreateFile,
            [], currentDestination.FullPath, name));
        if (result.Succeeded) pane.SelectPaths([Path.Combine(currentDestination.FullPath, name)]);
    }

    internal async Task<FileOperationResult> RunOperationAsync(FileOperationRequest request, CancellationToken cancellationToken = default,
        bool refresh = true, bool rememberSelection = true)
    {
        EnsureAcceptingWork();
        cancellationToken.ThrowIfCancellationRequested();
        var id = Guid.NewGuid().ToString("N");
        var job = new ActiveJob(); _jobs.Add(id, job);
        using var cancellationRegistration = cancellationToken.Register(job.Cancellation.Cancel);
        _cancel.IsEnabled = true; _jobProgress.IsIndeterminate = true;
        _jobText.Text = $"{request.Kind} · waiting for operation service";
        UpdateJobPanelVisibility();
        try
        {
            var progress = new Progress<FileOperationProgress>(value =>
            {
                if (_disposing || !_jobs.ContainsKey(id)) return;
                _jobText.Text = $"{value.Description} · {value.CompletedItems:N0} / {value.TotalItems:N0}";
                _jobProgress.IsIndeterminate = value.TotalItems == 0;
                if (value.TotalItems > 0) _jobProgress.Value = 100d * value.CompletedItems / value.TotalItems;
            });
            var reviewed = rememberSelection
                ? await CaptureReselectItemsAsync(request.Renames?.Select(mapping => mapping.Source) ?? request.Sources) : [];
            var result = await _operations.ExecuteAsync(request, progress, job.Cancellation.Token);
            if (reviewed.Length > 0 && result.Changes.Any(change => !change.Action.StartsWith("Skipped", StringComparison.Ordinal)))
            {
                var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
                var changedPaths = result.Changes.Where(change => change.Source.Length > 0 &&
                    !change.Action.StartsWith("Skipped", StringComparison.Ordinal))
                    .ToLookup(change => change.Source, comparer);
                _reselectItems = reviewed.Where(item => changedPaths.Contains(item.Path)).Select(item =>
                {
                    var moved = changedPaths[item.Path].LastOrDefault(change => change.Action is "Move" or "Rename");
                    return (moved?.Destination ?? item.Path, item.Identity);
                }).ToArray();
            }
            ShowOperationResult(result, request.Kind.ToString());
            if (refresh) await RefreshPanesAsync(afterFileOperation: true);
            return result;
        }
        finally
        {
            _jobs.Remove(id); job.Completion.TrySetResult(); job.Cancellation.Dispose();
            if (_jobs.Count == 0) { _jobProgress.IsIndeterminate = false; _jobProgress.Value = 0; _jobText.Text = "No file operations running"; }
            _cancel.IsEnabled = _jobs.Count > 0 || _toolCancellation is not null;
            UpdateJobPanelVisibility();
        }
    }

    private async Task RunUndoAsync()
    {
        if (!_operations.CanUndo) { _message.Text = "There is no undoable operation"; return; }
        var id = Guid.NewGuid().ToString("N"); var job = new ActiveJob(); _jobs.Add(id, job);
        _jobText.Text = "Undoing operation…"; _jobProgress.IsIndeterminate = true; _cancel.IsEnabled = true;
        UpdateJobPanelVisibility();
        try { ShowOperationResult(await _operations.UndoAsync(job.Cancellation.Token), "Undo"); await RefreshPanesAsync(afterFileOperation: true); }
        finally
        {
            _jobs.Remove(id); job.Completion.TrySetResult(); job.Cancellation.Dispose();
            _jobProgress.IsIndeterminate = false; _jobText.Text = "No file operations running";
            _cancel.IsEnabled = _jobs.Count > 0 || _toolCancellation is not null;
            UpdateJobPanelVisibility();
        }
    }

    private void ShowOperationResult(FileOperationResult result, string action)
    {
        _lastOperation = result;
        _message.Text = result.Succeeded ? $"{action} completed · {result.Changes.Count:N0} recorded change(s)"
            : $"{action} {(result.Canceled ? "canceled" : "incomplete")} · {result.Changes.Count:N0} recorded change(s) · {string.Join("; ", result.Errors.Take(2))} · History has details";
    }

    private Task RefreshPanesAsync(bool afterFileOperation = false) =>
        Task.WhenAll(_panes.Where(pane => !pane.IsSearchResults).Select(pane => pane.RefreshAsync(afterFileOperation)));

    private async Task ShowPropertiesAsync()
    {
        var entries = await RequireSelectionAsync(ActivePane);
        var output = new StringBuilder($"{entries.Count:N0} selected item(s)\n\n");
        foreach (var entry in entries.Take(100))
        {
            output.AppendLine(entry.FullPath).AppendLine($"Type: {entry.Kind}")
                .AppendLine($"Size: {(entry.IsDirectory ? "Folder contents not totaled" : entry.Length.ToString("N0") + " bytes")}")
                .AppendLine($"Modified: {entry.ModifiedUtc.ToLocalTime():F}")
                .AppendLine($"Created: {entry.CreatedUtc.ToLocalTime():F}")
                .AppendLine($"Attributes: {entry.Attributes}").AppendLine();
        }
        if (entries.Count > 100) output.AppendLine("First 100 items shown.");
        await Dialogs.MessageAsync(this, "Properties", output.ToString());
    }

    private async Task ShowHistoryAsync()
    {
        var review = await _operations.ReviewStorageAsync(_shutdownCancellation.Token);
        var summary = Ui.Text("", true);
        summary.TextWrapping = TextWrapping.Wrap;
        var warnings = Ui.Text("", true);
        warnings.TextWrapping = TextWrapping.Wrap;
        var history = new TextBox
        {
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap
        };
        ScrollViewer.SetHorizontalScrollBarVisibility(history, Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled);
        var panel = new Grid
        {
            Margin = new Thickness(16),
            RowDefinitions = new RowDefinitions("Auto,*,Auto,Auto"),
            RowSpacing = 10
        };
        panel.Children.Add(summary);
        Grid.SetRow(history, 1); panel.Children.Add(history);
        Grid.SetRow(warnings, 2); panel.Children.Add(warnings);

        var dialog = Dialogs.Window("Operation History", panel, 760, 540);
        var discard = Ui.Button("Discard Undo History…", () => { });
        var close = Ui.Button("Close", () => dialog.Close());
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 7,
            Children = { discard, close }
        };
        Grid.SetRow(buttons, 3); panel.Children.Add(buttons);
        var discarding = false;

        void Refresh(OperationStorageReview current)
        {
            var discardable = current.Records.Where(record => record.CanDiscard).ToArray();
            summary.Text =
                $"Undo available: {current.UndoableRecords:N0} · Recovery protected: {current.RecoveryProtectedRecords:N0} · " +
                $"Ready to discard: {discardable.Length:N0} · Reclaimable: {FormatStorageBytes(current.ReclaimableBytes)} in {current.ReclaimableFiles:N0} file(s)";
            history.Text = BuildOperationHistoryText();
            warnings.Text = current.Errors.Count == 0
                ? "Recovery and ownership checks are clear."
                : "Review notes: " + string.Join(" · ", current.Errors.Take(4));
            discard.IsVisible = discardable.Length > 0;
            discard.IsEnabled = !discarding && discardable.Length > 0;
        }

        discard.Click += async (_, _) =>
        {
            if (discarding) return;
            var reviewedIds = review.Records.Where(record => record.CanDiscard).Select(record => record.OperationId).ToArray();
            if (reviewedIds.Length == 0) { Refresh(review); return; }
            var confirmed = await Dialogs.ConfirmAsync(
                dialog,
                "Discard Undo History",
                $"Discard Undo for {reviewedIds.Length:N0} reviewed operation(s)?\n\n" +
                $"This will reclaim up to {FormatStorageBytes(review.ReclaimableBytes)} in {review.ReclaimableFiles:N0} app-owned file(s). " +
                "Those operations can no longer be undone. Recovery-protected records and system Trash items are retained.",
                "Discard Undo History");
            if (!confirmed) return;

            discarding = true;
            discard.IsEnabled = false;
            close.IsEnabled = false;
            try
            {
                var result = await _operations.DiscardUndoHistoryAsync(reviewedIds, _shutdownCancellation.Token);
                review = await _operations.ReviewStorageAsync(_shutdownCancellation.Token);
                var status = $"Undo history cleanup · {result.DiscardedOperationIds.Count:N0} discarded · " +
                    $"{FormatStorageBytes(result.ReclaimedBytes)} reclaimed from {result.ReclaimedFiles:N0} file(s)";
                if (result.RefusedOperationIds.Count > 0) status += $" · {result.RefusedOperationIds.Count:N0} refused after recheck";
                if (result.PendingOperationIds.Count > 0) status += $" · {result.PendingOperationIds.Count:N0} cleanup pending";
                if (result.MissingOperationIds.Count > 0) status += $" · {result.MissingOperationIds.Count:N0} already absent";
                if (result.Errors.Count > 0) status += " · " + string.Join("; ", result.Errors.Take(2));
                _message.Text = status;
            }
            catch (OperationCanceledException)
            {
                _message.Text = "Undo history cleanup canceled before confirmation was committed";
            }
            catch (Exception error)
            {
                ShowError(error);
            }
            finally
            {
                discarding = false;
                close.IsEnabled = true;
                try { review = await _operations.ReviewStorageAsync(_shutdownCancellation.Token); }
                catch (OperationCanceledException) { }
                Refresh(review);
            }
        };
        dialog.KeyDown += (_, e) => { if (e.Key == Key.Escape && !discarding) dialog.Close(); };
        Refresh(review);
        await dialog.ShowDialog(this);
    }

    private string BuildOperationHistoryText()
    {
        var output = new StringBuilder();
        foreach (var item in _operations.History.Reverse())
            output.AppendLine($"{item.StartedAt.ToLocalTime():g}  {item.Kind}  {item.Status}{(item.CanUndo ? "  [Undo available]" : "")}")
                .AppendLine(item.Summary).AppendLine();
        if (output.Length == 0) output.AppendLine("No recorded file operations.");
        if (_lastOperation is not null)
        {
            output.AppendLine("Latest operation details:");
            foreach (var error in _lastOperation.Errors) output.AppendLine(error);
            foreach (var change in _lastOperation.Changes.Take(200))
                output.AppendLine($"{change.Action}: {change.Source} → {change.Destination}");
        }
        return output.ToString();
    }

    private static string FormatStorageBytes(long bytes) => bytes switch
    {
        >= 1024L * 1024 * 1024 => $"{bytes / (1024d * 1024 * 1024):0.##} GB",
        >= 1024L * 1024 => $"{bytes / (1024d * 1024):0.##} MB",
        >= 1024 => $"{bytes / 1024d:0.##} KB",
        _ => $"{bytes:N0} bytes"
    };

    private async Task SaveListerLayoutAsAsync()
    {
        var initial = Path.GetFileName(Path.TrimEndingDirectorySeparator(ActivePane.CurrentDirectory));
        if (string.IsNullOrWhiteSpace(initial)) initial = "Lister Layout";
        var entered = await Dialogs.PromptAsync(this, "Save Lister Layout", "Layout name", initial, "Save Layout");
        if (entered is null) return;
        var name = ValidateListerName(entered);
        var existing = FindListerIndex(name);
        if (existing < 0 && _settings.SavedListers.Count >= 100)
            throw new InvalidOperationException("The saved Lister layout library already contains 100 layouts. Delete one before saving another.");
        if (existing >= 0 && !await Dialogs.ConfirmAsync(this, "Replace Lister Layout",
                $"A saved Lister layout named “{_settings.SavedListers[existing].Name}” already exists. Replace it with the current folders, tabs, and layout?",
                "Replace Layout"))
            return;

        var snapshot = CaptureListerLayout(name);
        await MutateListerLibraryAsync(() =>
        {
            if (existing >= 0) _settings.SavedListers[existing] = snapshot;
            else _settings.SavedListers.Add(snapshot);
        });
        _message.Text = existing >= 0
            ? $"Lister layout replaced · {name}"
            : $"Lister layout saved · {name}";
    }

    private async Task ManageListerLayoutsAsync()
    {
        var rows = Ui.Grid(("Name", nameof(SavedLister.Name), -1), ("Saved", nameof(SavedLister.SavedAtUtc), 170), ("Display", nameof(SavedLister.DualPane), 95));
        rows.Name = "ListerLayoutsList";
        rows.SelectionMode = DataGridSelectionMode.Single;
        rows.Columns[0] = Ui.ReadableColumn<SavedLister>("Name", layout => layout.Name);
        rows.Columns[1] = Ui.ReadableColumn<SavedLister>("Saved", layout => layout.SavedAtUtc.ToLocalTime().ToString("g"), 170);
        rows.Columns[2] = Ui.ReadableColumn<SavedLister>("Display", layout => layout.DualPane ? "Dual" : "Single", 95);
        var details = Ui.Text("Select a saved Lister layout.", true);
        details.Name = "ListerLayoutsFeedback";
        details.TextWrapping = TextWrapping.Wrap;
        var grid = new Grid { Margin = new Thickness(16), RowDefinitions = new RowDefinitions("*,Auto,Auto"), RowSpacing = 10 };
        grid.Children.Add(rows);
        Grid.SetRow(details, 1); grid.Children.Add(details);
        var dialog = Dialogs.Window("Lister Layouts", grid, 760, 520);
        var load = Ui.Button("Load", () => { });
        var rename = Ui.Button("Rename…", () => { });
        var delete = Ui.Button("Delete…", () => { });
        var close = Ui.Button("Close", () => dialog.Close());
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 7,
            Children = { rename, delete, load, close }
        };
        Grid.SetRow(buttons, 2); grid.Children.Add(buttons);
        var busy = false;

        SavedLister? Selected() => rows.SelectedItem as SavedLister;
        void UpdateSelection()
        {
            var selected = Selected();
            load.IsEnabled = !busy && selected is not null;
            rename.IsEnabled = !busy && selected is not null;
            delete.IsEnabled = !busy && selected is not null;
            close.IsEnabled = !busy;
            if (selected is null)
            {
                details.Text = _settings.SavedListers.Count == 0
                    ? "No saved Lister layouts yet. Use Settings → Save Lister Layout As… to create one."
                    : "Select a saved Lister layout.";
                return;
            }
            var tabs = selected.Panes.Sum(pane => pane.Tabs?.Count ?? 0);
            details.Text = $"{(selected.DualPane ? "Dual display" : "Single display")} · {selected.SplitOrientation.ToLowerInvariant()} split · " +
                $"{tabs:N0} tab(s) · tree {(selected.ShowTree ? "shown" : "hidden")} · viewer {(selected.ShowViewer ? "shown" : "hidden")} · " +
                $"saved {selected.SavedAtUtc.ToLocalTime():g}";
        }
        void RefreshRows(string? selectName = null)
        {
            rows.ItemsSource = _settings.SavedListers.ToArray();
            rows.SelectedItem = selectName is null ? null :
                _settings.SavedListers.FirstOrDefault(layout => string.Equals(layout.Name, selectName, StringComparison.OrdinalIgnoreCase));
            UpdateSelection();
        }
        void SetBusy(bool value)
        {
            busy = value;
            rows.IsEnabled = !value;
            UpdateSelection();
        }

        rows.SelectionChanged += (_, _) => UpdateSelection();
        load.Click += async (_, _) =>
        {
            var selected = Selected();
            if (selected is null || busy) return;
            SetBusy(true);
            details.Text = $"Loading {selected.Name}…";
            try
            {
                await LoadListerLayoutAsync(CloneListerLayout(selected));
                dialog.Close();
            }
            catch (Exception error)
            {
                details.Text = error.Message;
                _message.Text = $"Lister layout: {error.Message}";
            }
            finally { if (dialog.IsVisible) SetBusy(false); }
        };
        rename.Click += async (_, _) =>
        {
            var selected = Selected();
            if (selected is null || busy) return;
            var entered = await Dialogs.PromptAsync(dialog, "Rename Lister Layout", "New layout name", selected.Name, "Rename");
            if (entered is null) return;
            string name;
            try { name = ValidateListerName(entered); }
            catch (Exception error) { details.Text = error.Message; return; }
            if (string.Equals(name, selected.Name, StringComparison.Ordinal)) return;

            var selectedIndex = FindListerIndex(selected.Name);
            var conflictIndex = FindListerIndex(name);
            if (selectedIndex < 0) { RefreshRows(); return; }
            if (conflictIndex >= 0 && conflictIndex != selectedIndex &&
                !await Dialogs.ConfirmAsync(dialog, "Replace Lister Layout",
                    $"A saved Lister layout named “{_settings.SavedListers[conflictIndex].Name}” already exists. Replace it with “{selected.Name}” and use the new name?",
                    "Replace and Rename"))
                return;

            SetBusy(true);
            try
            {
                await MutateListerLibraryAsync(() =>
                {
                    var currentIndex = FindListerIndex(selected.Name);
                    if (currentIndex < 0) throw new InvalidOperationException("The selected Lister layout no longer exists.");
                    var conflict = FindListerIndex(name);
                    if (conflict >= 0 && conflict != currentIndex)
                    {
                        _settings.SavedListers.RemoveAt(conflict);
                        if (conflict < currentIndex) currentIndex--;
                    }
                    _settings.SavedListers[currentIndex].Name = name;
                });
                _message.Text = $"Lister layout renamed · {name}";
                RefreshRows(name);
            }
            catch (Exception error) { details.Text = error.Message; }
            finally { if (dialog.IsVisible) SetBusy(false); }
        };
        delete.Click += async (_, _) =>
        {
            var selected = Selected();
            if (selected is null || busy) return;
            if (!await Dialogs.ConfirmAsync(dialog, "Delete Lister Layout",
                    $"Delete the saved Lister layout “{selected.Name}”? This does not delete any files or folders.",
                    "Delete Layout"))
                return;
            SetBusy(true);
            try
            {
                var selectedName = selected.Name;
                await MutateListerLibraryAsync(() =>
                {
                    var index = FindListerIndex(selectedName);
                    if (index >= 0) _settings.SavedListers.RemoveAt(index);
                });
                _message.Text = $"Lister layout deleted · {selectedName}";
                RefreshRows();
            }
            catch (Exception error) { details.Text = error.Message; }
            finally { if (dialog.IsVisible) SetBusy(false); }
        };
        dialog.KeyDown += (_, e) => { if (e.Key == Key.Escape && !busy) dialog.Close(); };
        RefreshRows();
        await dialog.ShowDialog(this);
    }

    private async Task LoadListerLayoutAsync(SavedLister source)
    {
        if (_jobs.Count > 0 || _toolCancellation is not null)
            throw new InvalidOperationException("Finish or cancel the active file work before loading a Lister layout.");
        foreach (var pane in _panes) EnsurePaneReady(pane);
        var target = CloneListerLayout(source);
        await ValidateListerLayoutAsync(target);
        var previous = CaptureListerLayout("__rollback__");
        _saveDebounce.Stop();
        _suppressScheduledSave = true;
        try
        {
            await _panes[0].ApplySnapshotAsync(target.Panes[0]);
            await _panes[1].ApplySnapshotAsync(target.Panes[1]);
            ApplyListerWindowState(target);
            await SaveSettingsAsync();
            _message.Text = $"Lister layout loaded · {target.Name}";
        }
        catch (Exception loadError)
        {
            try
            {
                await _panes[0].ApplySnapshotAsync(previous.Panes[0]);
                await _panes[1].ApplySnapshotAsync(previous.Panes[1]);
                ApplyListerWindowState(previous);
                await SaveSettingsAsync();
            }
            catch (Exception rollbackError)
            {
                throw new AggregateException($"Lister layout “{target.Name}” failed to load and the previous layout could not be fully restored.",
                    loadError, rollbackError);
            }
            throw new IOException($"Lister layout “{target.Name}” was not loaded. The previous layout was restored. {loadError.Message}", loadError);
        }
        finally { _suppressScheduledSave = false; }
    }

    private async Task ValidateListerLayoutAsync(SavedLister layout)
    {
        if (layout.Panes.Count < 2) throw new InvalidOperationException("The saved Lister layout does not contain both file-display states.");
        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var paths = layout.Panes.SelectMany(pane => pane.Tabs ?? []).Select(tab => tab.Path)
            .Where(path => !string.IsNullOrWhiteSpace(path)).Distinct(comparer).ToArray();
        if (paths.Length == 0) throw new InvalidOperationException("The saved Lister layout contains no folder tabs.");
        foreach (var savedPath in paths)
        {
            var path = DirectoryBrowser.ResolvePath(savedPath);
            await Task.Run(() =>
            {
                _shutdownCancellation.Token.ThrowIfCancellationRequested();
                if (!Directory.Exists(path)) throw new DirectoryNotFoundException($"Saved Lister folder is unavailable: {path}");
                using var entries = Directory.EnumerateFileSystemEntries(path).GetEnumerator();
                _ = entries.MoveNext();
            }, _shutdownCancellation.Token).WaitAsync(_shutdownCancellation.Token);
        }
    }

    private SavedLister CaptureListerLayout(string name) => new()
    {
        Name = name,
        SavedAtUtc = DateTimeOffset.UtcNow,
        DualPane = _settings.DualPane,
        SplitOrientation = _settings.SplitOrientation,
        ShowTree = _settings.ShowTree,
        IndependentFolderTrees = _settings.IndependentFolderTrees,
        ShowViewer = _settings.ShowViewer,
        StackedPaneShare = _settings.StackedPaneShare,
        SideBySidePaneShare = _settings.SideBySidePaneShare,
        TreeWidth = _settings.TreeWidth,
        DestinationTreeWidth = _settings.DestinationTreeWidth,
        ViewerWidth = _settings.ViewerWidth,
        WindowWidth = Width,
        WindowHeight = Height,
        ActivePane = _activePane,
        Panes = _panes.Select(pane => pane.Capture()).ToList()
    };

    private void ApplyListerWindowState(SavedLister layout)
    {
        SetNavigationLock(false);
        _linkedNavigationLocks.Clear();
        _settings.DualPane = layout.DualPane;
        _settings.SplitOrientation = layout.SplitOrientation == "Horizontal" ? "Horizontal" : "Vertical";
        _settings.ShowTree = layout.ShowTree;
        _settings.IndependentFolderTrees = layout.IndependentFolderTrees;
        _settings.ShowViewer = layout.ShowViewer;
        _settings.StackedPaneShare = layout.StackedPaneShare;
        _settings.SideBySidePaneShare = layout.SideBySidePaneShare;
        _settings.TreeWidth = layout.TreeWidth;
        _settings.DestinationTreeWidth = layout.DestinationTreeWidth;
        _settings.ViewerWidth = layout.ViewerWidth;
        _settings.ActivePane = Math.Clamp(layout.ActivePane, 0, 1);
        _settings.Panes = layout.Panes.Select(ClonePaneSettings).ToList();
        Width = Math.Clamp(layout.WindowWidth, 800, 10000);
        Height = Math.Clamp(layout.WindowHeight, 500, 10000);
        _activePane = _settings.ActivePane;
        ApplyLayout();
        ActivePane.FocusList();
        UpdateSelection();
    }

    private async Task MutateListerLibraryAsync(Action mutation)
    {
        var before = _settings.SavedListers.Select(CloneListerLayout).ToList();
        try
        {
            mutation();
            await SaveSettingsAsync();
        }
        catch
        {
            _settings.SavedListers = before;
            throw;
        }
    }

    private int FindListerIndex(string name)
    {
        for (var index = 0; index < _settings.SavedListers.Count; index++)
            if (string.Equals(_settings.SavedListers[index].Name, name, StringComparison.OrdinalIgnoreCase)) return index;
        return -1;
    }

    private static string ValidateListerName(string value)
    {
        var name = value.Trim();
        if (name.Length == 0) throw new ArgumentException("Enter a name for the Lister layout.");
        if (name.Length > 100) throw new ArgumentException("Lister layout names are limited to 100 characters.");
        return name;
    }

    private static SavedLister CloneListerLayout(SavedLister source)
    {
        var panes = (source.Panes ?? []).Where(pane => pane is not null).Select(ClonePaneSettings).ToList();
        while (panes.Count < 2) panes.Add(new PaneSettings());
        if (panes.Count > 2) panes = panes.Take(2).ToList();
        return new SavedLister
        {
            Name = source.Name,
            SavedAtUtc = source.SavedAtUtc,
            DualPane = source.DualPane,
            SplitOrientation = source.SplitOrientation == "Horizontal" ? "Horizontal" : "Vertical",
            ShowTree = source.ShowTree,
            IndependentFolderTrees = source.IndependentFolderTrees,
            ShowViewer = source.ShowViewer,
            StackedPaneShare = source.StackedPaneShare,
            SideBySidePaneShare = source.SideBySidePaneShare,
            TreeWidth = source.TreeWidth,
            DestinationTreeWidth = source.DestinationTreeWidth,
            ViewerWidth = source.ViewerWidth,
            WindowWidth = source.WindowWidth,
            WindowHeight = source.WindowHeight,
            ActivePane = Math.Clamp(source.ActivePane, 0, 1),
            Panes = panes
        };
    }

    private static PaneSettings ClonePaneSettings(PaneSettings source)
    {
        var tabs = (source.Tabs ?? []).Where(tab => tab is not null).Select(tab => new TabSettings
        {
            Path = tab.Path,
            ViewMode = tab.ViewMode,
            CheckboxMode = tab.CheckboxMode,
            SortColumn = tab.SortColumn,
            Descending = tab.Descending,
            Locked = tab.Locked,
            LinkedTabGroup = tab.LinkedTabGroup,
            FormatLock = tab.FormatLock?.Copy(),
            Columns = (tab.Columns ?? []).Where(column => column is not null).Select(column => column.Copy()).ToList()
        }).ToList();
        if (tabs.Count == 0) tabs.Add(new TabSettings());
        return new PaneSettings { Tabs = tabs, ActiveTab = Math.Clamp(source.ActiveTab, 0, tabs.Count - 1) };
    }

    private async Task ManageFolderFormatsAsync(PaneView pane)
    {
        var list = new ListBox { Name = "SavedFolderFormats", Height = 250 };
        var panel = new StackPanel { Margin = new Thickness(16), Spacing = 10, Children = { list } };
        var dialog = Dialogs.Window("Manage Folder Formats", panel, 690, 440);
        var path = new TextBox { Name = "NewFolderFormatPath", Watermark = "Absolute path for a new rule" };
        var feedback = Ui.Text("", true);
        feedback.TextWrapping = TextWrapping.Wrap;
        var browse = Ui.Button("Browse…", () => { });
        browse.Click += async (_, _) =>
        {
            browse.IsEnabled = false;
            try
            {
                var folders = await dialog.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
                    { Title = "Choose folder for a format rule", AllowMultiple = false });
                if (folders.FirstOrDefault()?.TryGetLocalPath() is { } folder) path.Text = folder;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or NotSupportedException)
            {
                feedback.Text = $"Could not choose a folder: {error.Message}";
            }
            finally { browse.IsEnabled = true; }
        };
        var add = Ui.Button("Add Rule…", () =>
        {
            var candidate = path.Text?.Trim();
            if (string.IsNullOrEmpty(candidate) || !Path.IsPathFullyQualified(candidate) || candidate.Contains('\0'))
            {
                feedback.Text = "Enter an absolute folder path or choose a folder.";
                return;
            }
            try { dialog.Close(Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidate))); }
            catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException)
            {
                feedback.Text = $"Invalid folder path: {error.Message}";
            }
        });
        var edit = Ui.Button("Edit…", () => dialog.Close((list.SelectedItem as FolderColumnRule)?.Path));
        var toggle = Ui.Button("Enable / Disable", () => { });
        var remove = Ui.Button("Remove Rule", () => { });
        toggle.Click += (_, _) =>
        {
            if (list.SelectedItem is not FolderColumnRule rule) return;
            var index = _settings.FolderColumnRules.IndexOf(rule);
            if (index < 0) return;
            var updated = rule with { Disabled = !rule.Disabled };
            _settings.FolderColumnRules[index] = updated;
            RefreshFolderFormats();
            RefreshList(updated);
        };
        remove.Click += (_, _) =>
        {
            if (list.SelectedItem is not FolderColumnRule rule || !_settings.FolderColumnRules.Remove(rule)) return;
            RefreshFolderFormats();
            RefreshList(null);
        };
        list.ItemTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<FolderColumnRule>((rule, _) =>
        {
            if (rule is null) return new TextBlock();
            var label = Ui.Text($"{rule.Path} · {(rule.IncludeSubfolders ? "includes subfolders" : "this folder")} · {(rule.Disabled ? "disabled" : "enabled")}");
            label.TextTrimming = TextTrimming.CharacterEllipsis;
            ToolTip.SetTip(label, rule.Path);
            return label;
        });
        void RefreshList(FolderColumnRule? selected)
        {
            list.ItemsSource = _settings.FolderColumnRules.OrderBy(rule => rule.Path, StringComparer.OrdinalIgnoreCase).ToArray();
            list.SelectedItem = selected;
            UpdateActions();
        }
        void UpdateActions() => edit.IsEnabled = toggle.IsEnabled = remove.IsEnabled = list.SelectedItem is FolderColumnRule;
        list.SelectionChanged += (_, _) => UpdateActions();
        RefreshList(null);
        panel.Children.Add(Ui.Row(edit, toggle, remove, Ui.Button("Close", () => dialog.Close(null))));
        var pathRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"), ColumnSpacing = 8 };
        Grid.SetColumn(browse, 1);
        Grid.SetColumn(add, 2);
        pathRow.Children.Add(path);
        pathRow.Children.Add(browse);
        pathRow.Children.Add(add);
        panel.Children.Add(pathRow);
        panel.Children.Add(feedback);
        if (await dialog.ShowDialog<string?>(this) is { } selectedPath)
            await FolderFormatAsync(pane, managedPath: selectedPath);
    }

    private void RefreshFolderFormats()
    {
        foreach (var view in _panes.Where(view => !view.IsSearchResults))
        {
            view.ReloadFolderColumns(view.CurrentDirectory);
            view.RefreshSort();
            view.RefreshViewMode();
        }
        ScheduleSave();
    }

    private async Task FolderFormatAsync(PaneView pane, bool editDefault = false, string? managedPath = null)
    {
        EnsurePaneReady(pane);
        var directory = managedPath ?? Path.TrimEndingDirectorySeparator(Path.GetFullPath(pane.CurrentDirectory));
        bool MatchesDirectory(FolderColumnRule rule) => string.Equals(rule.Path, directory,
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        var savedRule = editDefault ? null : _settings.FolderColumnRules.FirstOrDefault(MatchesDirectory);
        var defaultFormat = editDefault ? _settings.UserDefaultFolderFormat : null;
        var originalColumns = (managedPath is null ? null : savedRule?.Columns)
            ?? defaultFormat?.Columns ?? pane.CurrentColumnLayout;
        var columns = originalColumns.Select(column => column.Copy()).ToList();
        var rows = new StackPanel { Name = "FolderFormatRows", Spacing = 5 };
        var feedback = Ui.Text("Choose which columns this tab shows and how they are sized.", true);
        feedback.Name = "FolderFormatFeedback";
        feedback.TextWrapping = TextWrapping.Wrap;
        var invalidWidths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var apply = Ui.Button("Apply", () => { });
        var reset = Ui.Button("Reset", () => { });

        void UpdateApplyState()
        {
            apply.IsEnabled = invalidWidths.Count == 0;
            feedback.Text = invalidWidths.Count == 0
                ? "Choose whether to apply these columns to the tab, this folder, or its subfolders."
                : "Fixed widths must be whole or decimal numbers within the allowed range.";
        }

        void RenderRows() => RenderFolderFormatRows(rows, columns, invalidWidths, UpdateApplyState);

        reset.Click += (_, _) =>
        {
            columns = FileColumnSettings.CreateDefaultLayout();
            RenderRows();
        };
        RenderRows();
        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("*,90,76,Auto,Auto"), ColumnSpacing = 6 };
        header.Children.Add(Ui.Text("Column", true));
        var sizingHeader = Ui.Text("Sizing", true); Grid.SetColumn(sizingHeader, 1); header.Children.Add(sizingHeader);
        var widthHeader = Ui.Text("Width", true); Grid.SetColumn(widthHeader, 2); header.Children.Add(widthHeader);
        var panel = new StackPanel { Margin = new Thickness(18), Spacing = 9 };
        panel.Children.Add(Ui.Text(editDefault ? "User Default Format" : "Folder Format", false));
        panel.Children.Add(feedback);
        panel.Children.Add(header);
        panel.Children.Add(rows);
        if (managedPath is not null)
        {
            var target = Ui.Text(directory, true);
            target.TextWrapping = TextWrapping.Wrap;
            panel.Children.Insert(1, target);
        }
        if (savedRule is { Disabled: true, InheritColumns: false })
        {
            columns = savedRule.Columns.Select(column => column.Copy()).ToList();
            RenderRows();
        }
        var scope = new ComboBox
        {
            Name = "FolderFormatScope",
            ItemsSource = new[] { "Current tab", "This folder", "This folder and subfolders" },
            SelectedIndex = savedRule?.IncludeSubfolders == true ? 2 : savedRule is not null || managedPath is not null ? 1 : 0
        };
        scope.IsVisible = !editDefault;
        if (managedPath is not null)
            scope.SelectionChanged += (_, _) => { if (scope.SelectedIndex == 0) scope.SelectedIndex = 1; };
        panel.Children.Add(scope);
        var enabled = new CheckBox
        {
            Name = "FolderFormatEnabled", Content = "Enable this folder rule",
            IsChecked = savedRule?.Disabled != true, IsEnabled = scope.SelectedIndex > 0
        };
        scope.SelectionChanged += (_, _) => enabled.IsEnabled = scope.SelectedIndex > 0;
        enabled.IsVisible = !editDefault;
        panel.Children.Add(enabled);
        var inherit = new CheckBox
        {
            Name = "FolderFormatInheritColumns",
            Content = "Inherit columns from the parent format",
            IsChecked = savedRule?.InheritColumns == true,
            IsEnabled = scope.SelectedIndex > 0
        };
        rows.IsEnabled = inherit.IsChecked != true;
        scope.SelectionChanged += (_, _) =>
        {
            inherit.IsEnabled = scope.SelectedIndex > 0;
            if (!inherit.IsEnabled) inherit.IsChecked = false;
        };
        inherit.IsCheckedChanged += (_, _) => rows.IsEnabled = inherit.IsChecked != true;
        inherit.IsVisible = !editDefault;
        panel.Children.Add(inherit);
        const string parentSort = "Inherit parent sort";
        const string parentView = "Inherit parent view";
        var sortColumn = new ComboBox
        {
            Name = "FolderFormatSortColumn",
            ItemsSource = editDefault ? new[] { "Name", "Size", "Type", "Modified", "Created", "Attr", "Path" } :
                new[] { "Keep current sort", "Name", "Size", "Type", "Modified", "Created", "Attr", "Path", parentSort },
            SelectedItem = editDefault ? defaultFormat?.SortColumn ?? pane.CurrentSortColumn :
                savedRule?.InheritSort == true ? parentSort : savedRule?.SortColumn ?? "Keep current sort"
        };
        var formatView = new ComboBox
        {
            Name = "FolderFormatViewMode",
            ItemsSource = editDefault ? new[] { "Details", "Tiles" } : new[] { "Keep tab view", "Details", "Tiles", parentView },
            SelectedItem = editDefault ? defaultFormat?.ViewMode ?? pane.CurrentViewMode :
                savedRule?.InheritView == true ? parentView : savedRule?.ViewMode ?? "Keep tab view"
        };
        panel.Children.Add(formatView);
        var checkboxMode = new ComboBox
        {
            Name = "FolderFormatCheckboxMode",
            ItemsSource = new[] { "Inherit checkbox setting", "Checkbox Mode on", "Checkbox Mode off" },
            SelectedIndex = (editDefault ? defaultFormat?.CheckboxMode : savedRule?.CheckboxMode) switch
            { true => 1, false => 2, null => 0 }
        };
        panel.Children.Add(Dialogs.Form(("Checkbox Mode", checkboxMode)));
        var direction = new ComboBox
        {
            Name = "FolderFormatSortDirection",
            ItemsSource = new[] { "Ascending", "Descending" },
            SelectedIndex = (defaultFormat?.Descending ?? savedRule?.SortDescending ?? pane.CurrentSortDescending) ? 1 : 0
        };
        panel.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8,
            Children = { Ui.Text("Sort", true), sortColumn, direction } });
        sortColumn.SelectionChanged += (_, _) =>
        {
            if (Equals(sortColumn.SelectedItem, parentSort) && scope.SelectedIndex == 0) scope.SelectedIndex = 1;
        };
        formatView.SelectionChanged += (_, _) =>
        {
            if (Equals(formatView.SelectedItem, parentView) && scope.SelectedIndex == 0) scope.SelectedIndex = 1;
        };
        scope.SelectionChanged += (_, _) =>
        {
            if (scope.SelectedIndex != 0) return;
            if (Equals(sortColumn.SelectedItem, parentSort)) sortColumn.SelectedIndex = 0;
            if (Equals(formatView.SelectedItem, parentView)) formatView.SelectedIndex = 0;
        };
        var dialog = Dialogs.Window(editDefault ? "User Default Format" : "Folder Format / Columns", new ScrollViewer { Content = panel }, 650, 560);
        var importedFormats = new ComboBox
        {
            Name = "ImportedFolderFormats", IsVisible = false, PlaceholderText = "Choose columns to preview"
        };
        var originalScope = scope.SelectedIndex;
        var originalSort = sortColumn.SelectedItem;
        var originalView = formatView.SelectedItem;
        var importStatus = Ui.Text("");
        importStatus.TextWrapping = TextWrapping.Wrap;
        panel.Children.Add(importedFormats);
        panel.Children.Add(importStatus);
        importedFormats.SelectionChanged += (_, _) =>
        {
            if (importedFormats.SelectedItem is not ImportedFolderColumns selected) return;
            columns = (selected.KeepCurrentColumns ? originalColumns : selected.Columns)
                .Select(column => column.Copy()).ToList();
            RenderRows();
            if (editDefault)
            {
                sortColumn.SelectedItem = selected.PrimarySortColumn ?? originalSort;
                formatView.SelectedItem = selected.ViewMode ?? originalView;
            }
            else
            {
                var previewScope = selected.IncludeSubfolders switch { true => 2, false => 1, null => originalScope };
                if (previewScope == 0 && (selected.KeepCurrentColumns || selected.InheritSort || selected.InheritView || selected.Disabled))
                    previewScope = 1;
                scope.SelectedIndex = previewScope;
                sortColumn.SelectedItem = selected.InheritSort ? parentSort : selected.PrimarySortColumn ?? "Keep current sort";
                formatView.SelectedItem = selected.InheritView ? parentView : selected.ViewMode ?? "Keep tab view";
            }
            inherit.IsChecked = !editDefault && selected.KeepCurrentColumns;
            enabled.IsChecked = !selected.Disabled;
            importStatus.Text = selected.Summary;
        };
        var import = Ui.Button("Import Opus columns…", () => { });
        import.Click += async (_, _) =>
        {
            import.IsEnabled = false;
            try
            {
                var files = await dialog.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
                {
                    Title = "Open Directory Opus folder format",
                    FileTypeFilter = [new FilePickerFileType("Directory Opus folder formats") { Patterns = ["*.off", "*.xml"] }]
                });
                if (files.Count != 1) return;
                await using var stream = await files[0].OpenReadAsync();
                var formats = await ImportedFolderColumns.ReadAsync(stream);
                if (editDefault && string.Equals(files[0].Name, "default.off", StringComparison.OrdinalIgnoreCase))
                {
                    var defaults = formats.Where(format => format.IsUserDefaultEntry && !format.Disabled).ToArray();
                    if (defaults.Length != 1)
                        throw new InvalidDataException("Expected exactly one enabled User Default entry in default.off.");
                    formats = defaults;
                }
                importedFormats.ItemsSource = formats;
                importedFormats.IsVisible = true;
                importStatus.Text = "Choose a format to preview. Apply saves the selected scope.";
                importedFormats.SelectedIndex = formats.Count == 1 ? 0 : -1;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or
                System.Xml.XmlException or InvalidDataException or NotSupportedException)
            {
                importStatus.Text = $"Could not import Opus columns: {error.Message}";
            }
            finally { import.IsEnabled = true; }
        };
        var cancel = Ui.Button("Cancel", () => dialog.Close(false));
        reset.Name = "FolderFormatReset";
        apply.Name = "FolderFormatApply";
        apply.Click += (_, _) => dialog.Close(true);
        panel.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
            Children = { import, reset, cancel, apply }
        });
        var tab = pane.ActiveTabIndex;
        var navigation = pane.ViewRevision.Navigation;
        if (!await dialog.ShowDialog<bool>(this)) return;
        if (managedPath is not null && scope.SelectedIndex == 0)
            throw new InvalidOperationException("Choose a folder scope when editing a saved rule.");
        if (pane.ActiveTabIndex != tab || pane.ViewRevision.Navigation != navigation || pane.IsLoading)
            throw new IOException("The folder view changed while editing its format. Reopen Folder Format for the current folder.");
        var inheritSort = Equals(sortColumn.SelectedItem, parentSort);
        var sort = sortColumn.SelectedIndex >= (editDefault ? 0 : 1) && !inheritSort ? sortColumn.SelectedItem as string : null;
        var descending = direction.SelectedIndex == 1;
        var inheritView = Equals(formatView.SelectedItem, parentView);
        var mode = formatView.SelectedIndex >= (editDefault ? 0 : 1) && !inheritView ? formatView.SelectedItem as string : null;
        bool? checkboxSetting = checkboxMode.SelectedIndex switch { 1 => true, 2 => false, _ => null };
        if (editDefault || savedRule is not null || scope.SelectedIndex > 0)
        {
            if (editDefault)
                _settings.UserDefaultFolderFormat = new LockedFolderFormat(mode ?? pane.CurrentViewMode,
                    sort ?? pane.CurrentSortColumn, descending, columns, checkboxSetting);
            else
            {
                _settings.FolderColumnRules.RemoveAll(MatchesDirectory);
                if (scope.SelectedIndex > 0)
                    _settings.FolderColumnRules.Add(new FolderColumnRule(directory, scope.SelectedIndex == 2, columns, sort,
                        descending, mode, inherit.IsChecked == true, inheritSort, inheritView, enabled.IsChecked != true, checkboxSetting));
            }
            RefreshFolderFormats();
        }
        if (!editDefault && scope.SelectedIndex == 0) pane.ApplyCurrentColumnLayout(columns);
        if (!editDefault && scope.SelectedIndex == 0 && sort is not null) pane.ApplySort(sort, descending);
        if (!editDefault && scope.SelectedIndex == 0 && mode is not null) pane.SetViewMode(mode);
        if (!editDefault && scope.SelectedIndex == 0 && checkboxSetting is { } localCheckbox) pane.SetCheckboxMode(localCheckbox);
        _message.Text = editDefault ? "User Default format saved" : scope.SelectedIndex > 0 && enabled.IsChecked != true
            ? "Folder format saved and disabled"
            : $"Folder format updated · {pane.VisibleColumnIds.Count:N0} columns shown";
    }

    private static string FolderColumnLabel(string id) => id switch
    {
        "Modified" => "Date Modified",
        "Created" => "Date Created",
        "Attr" => "Attr",
        _ => id
    };

    private static void RenderFolderFormatRows(StackPanel rows, List<FileColumnSettings> columns,
        HashSet<string> invalidWidths, Action updateApplyState, bool print = false)
    {
        rows.Children.Clear();
        invalidWidths.Clear();
        for (var index = 0; index < columns.Count; index++)
        {
            var rowIndex = index;
            var column = columns[index];
            var show = new CheckBox
            {
                Name = "FolderFormatVisible_" + column.Id,
                Content = FolderColumnLabel(column.Id),
                IsChecked = (!print && column.Id == "Name") || column.Visible,
                IsEnabled = print || column.Id != "Name",
                VerticalAlignment = VerticalAlignment.Center
            };
            show.Click += (_, _) =>
            {
                column.Visible = (!print && column.Id == "Name") || show.IsChecked == true;
                updateApplyState();
            };

            var sizing = new ComboBox
            {
                Name = "FolderFormatSizing_" + column.Id,
                ItemsSource = new[] { "Fixed", "Fill" },
                SelectedItem = column.WidthMode == "Star" ? "Fill" : "Fixed",
                Width = 84,
                IsVisible = !print
            };
            var width = new TextBox
            {
                Name = "FolderFormatWidth_" + column.Id,
                Text = column.WidthMode == "Star" ? "" : column.Width.ToString("0.##"),
                Width = 70,
                Watermark = "px",
                IsEnabled = column.WidthMode != "Star"
            };
            sizing.SelectionChanged += (_, _) =>
            {
                if (sizing.SelectedItem as string == "Fill")
                {
                    column.WidthMode = "Star";
                    column.Width = 1;
                    width.Text = "";
                    width.IsEnabled = false;
                    invalidWidths.Remove(column.Id);
                }
                else
                {
                    column.WidthMode = "Pixel";
                    if (column.Width < FolderColumnMinimumWidth(column.Id)) column.Width = FolderColumnDefaultPixelWidth(column.Id);
                    width.IsEnabled = true;
                    width.Text = column.Width.ToString("0.##");
                }
                updateApplyState();
            };
            width.TextChanged += (_, _) =>
            {
                if (column.WidthMode == "Star") { invalidWidths.Remove(column.Id); updateApplyState(); return; }
                if (double.TryParse(width.Text, out var parsed) && double.IsFinite(parsed) &&
                    parsed >= (print ? 42 : FolderColumnMinimumWidth(column.Id)) && parsed <= 1200)
                {
                    column.Width = parsed;
                    invalidWidths.Remove(column.Id);
                }
                else invalidWidths.Add(column.Id);
                updateApplyState();
            };

            var up = Ui.Button("Up", () =>
            {
                if (rowIndex <= 0) return;
                (columns[rowIndex - 1], columns[rowIndex]) = (columns[rowIndex], columns[rowIndex - 1]);
                RenderFolderFormatRows(rows, columns, invalidWidths, updateApplyState, print);
            });
            up.Name = "FolderFormatUp_" + column.Id;
            up.IsEnabled = index > 0;
            var down = Ui.Button("Down", () =>
            {
                if (rowIndex + 1 >= columns.Count) return;
                (columns[rowIndex + 1], columns[rowIndex]) = (columns[rowIndex], columns[rowIndex + 1]);
                RenderFolderFormatRows(rows, columns, invalidWidths, updateApplyState, print);
            });
            down.Name = "FolderFormatDown_" + column.Id;
            down.IsEnabled = index + 1 < columns.Count;

            var row = new Grid { ColumnDefinitions = new ColumnDefinitions(print ? "*,0,76,Auto,Auto" : "*,90,76,Auto,Auto"), ColumnSpacing = 6 };
            row.Children.Add(show);
            Grid.SetColumn(sizing, 1); row.Children.Add(sizing);
            Grid.SetColumn(width, 2); row.Children.Add(width);
            Grid.SetColumn(up, 3); row.Children.Add(up);
            Grid.SetColumn(down, 4); row.Children.Add(down);
            rows.Children.Add(row);
        }
        updateApplyState();
    }

    private static double FolderColumnMinimumWidth(string id) => id switch
    {
        "Name" => 180,
        "Modified" or "Created" => 110,
        "Attr" => 60,
        "Path" => 140,
        _ => 60
    };

    private static double FolderColumnDefaultPixelWidth(string id) => id switch
    {
        "Name" => 320,
        "Size" => 85,
        "Modified" or "Created" => 148,
        "Type" => 100,
        "Attr" => 70,
        "Path" => 260,
        _ => 100
    };

    private async Task PreferencesAsync()
    {
        ComboBox? theme = null;
        Control appearance;
        if (_appearance.UsesSharedProfile)
        {
            appearance = Ui.Text(_appearance.DisplayName + " · shared");
        }
        else
        {
            theme = new ComboBox { ItemsSource = new[] { "Dark", "Light" }, SelectedItem = _settings.Theme };
            appearance = theme;
        }
        var orientation = new ComboBox { ItemsSource = new[] { "Vertical", "Horizontal" }, SelectedItem = _settings.SplitOrientation };
        var dual = new CheckBox { Content = "Dual file display", IsChecked = _settings.DualPane };
        var tree = new CheckBox { Content = "Folder tree", IsChecked = _settings.ShowTree };
        var viewer = new CheckBox { Content = "Viewer pane", IsChecked = _settings.ShowViewer };
        var hidden = new CheckBox { Content = "Show hidden files", IsChecked = _settings.ShowHidden };
        var navWarning = new CheckBox
        {
            Content = "Warning message when out of sync", IsChecked = _settings.WarnWhenNavigationLockOutOfSync
        };
        var navCancel = new CheckBox
        {
            Content = "Cancel without warning when navigating away", IsChecked = _settings.CancelNavigationLockWhenNavigatingAway
        };
        var panel = new StackPanel
        {
            Margin = new Thickness(18), Spacing = 10,
            Children = { Dialogs.Form(("Appearance", appearance), ("Dual orientation", orientation)), dual, tree, viewer, hidden,
                Ui.Text("Navigation Lock", true), navWarning, navCancel }
        };
        if (_appearance.UsesSharedProfile)
        {
            var note = Ui.Text($"Shared appearance is managed by Spiffy Workspace and read from {_appearance.SettingsPath} at startup. Directory Opus does not write that file.", true);
            note.TextWrapping = TextWrapping.Wrap;
            panel.Children.Add(note);
        }
        var dialog = Dialogs.Window("Preferences", panel, 560, _appearance.UsesSharedProfile ? 478 : 438);
        panel.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8,
            Children = { Ui.Button("Cancel", () => dialog.Close(false)), Ui.Button("Apply", () => dialog.Close(true)) } });
        if (!await dialog.ShowDialog<bool>(this)) return;
        var selection = _panes.Select(pane => pane.SelectedEntries.Select(entry => entry.FullPath).ToArray()).ToArray();
        if (!_appearance.UsesSharedProfile)
            _settings.Theme = theme?.SelectedItem as string ?? "Dark";
        _settings.SplitOrientation = orientation.SelectedItem as string ?? "Vertical";
        _settings.DualPane = dual.IsChecked == true; _settings.ShowTree = tree.IsChecked == true;
        _settings.ShowViewer = viewer.IsChecked == true; _settings.ShowHidden = hidden.IsChecked == true;
        _settings.WarnWhenNavigationLockOutOfSync = navWarning.IsChecked == true;
        _settings.CancelNavigationLockWhenNavigatingAway = navCancel.IsChecked == true;
        if (!_settings.WarnWhenNavigationLockOutOfSync) _navigationLockWarning.IsVisible = false;
        if (!_appearance.UsesSharedProfile)
            Application.Current!.RequestedThemeVariant = _settings.Theme == "Light" ? ThemeVariant.Light : ThemeVariant.Dark;
        ApplyLayout(); BuildTree(); await RefreshPanesAsync();
        for (var index = 0; index < _panes.Length; index++) _panes[index].SelectPaths(selection[index]);
        UpdateSelection(); await SaveSettingsAsync();
    }
}
