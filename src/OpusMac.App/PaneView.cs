using Avalonia;
using Avalonia.Automation;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using OpusMac.Core;
using OpusMac.Core.Browsing;
using OpusMac.Core.Persistence;
using OpusMac.Core.Search;
using OpusMac.Core.Tools;

namespace OpusMac.App;

internal sealed class PaneView : UserControl, IDisposable
{
    private readonly DirectoryBrowser _browser;
    private IReadOnlyDictionary<string, DuplicateGroupHeader>? _duplicateGroups;
    private DuplicateResult? _activeDuplicateResult;
    internal DuplicateResult? CurrentDuplicateResult => IsSearchResults && !IsLoading &&
        ReferenceEquals(_activeDuplicateResult, CurrentTab.DuplicateResults?.Result) ? _activeDuplicateResult : null;
    private readonly Func<bool> _showHidden;
    private readonly List<PaneTab> _tabs = [];
    private readonly StackPanel _tabButtons = new() { Orientation = Orientation.Horizontal, Spacing = 2 };
    private readonly TextBlock _role = Ui.Text("Source");
    private readonly TextBlock _status = Ui.Text("Ready", true);
    private readonly Button _fileTypeStatus = new()
    {
        Name = "FileTypeSummaryStatus", IsVisible = false, Padding = new Thickness(1, 0),
        BorderThickness = new Thickness(0), Background = Brushes.Transparent,
        MaxWidth = 220, ClipToBounds = true, VerticalAlignment = VerticalAlignment.Center
    };
    private FileTypeSummary.Bucket[] _selectedFileTypes = [];
    private int _statusBadgeLimit = 3;
    private readonly TextBlock _freeSpaceStatus = Ui.Text("", true);
    private readonly ProgressBar _diskCapacity = new()
    {
        Name = "FolderDiskCapacity", Width = 46, Height = 10, MinWidth = 0, MinHeight = 0,
        VerticalAlignment = VerticalAlignment.Center, IsVisible = false,
        Foreground = new SolidColorBrush(Color.Parse("#A52F56")),
        Background = new SolidColorBrush(Color.Parse("#51404A"))
    };
    private readonly Button _formatStatus;
    private readonly MenuItem _formatLockAction = new();
    private readonly Border _roleBar;
    private readonly Border _filterHost;
    private readonly TextBox _path = new() { Watermark = "Folder path", HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly TextBlock _pathLabel = new() { TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
    private readonly Button _pathBreadcrumb = new() { Name = "LocationBreadcrumb", HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left, Padding = new Thickness(4, 0), MinWidth = 0 };
    private readonly StackPanel _breadcrumbSegments = new() { Orientation = Orientation.Horizontal, Spacing = 0, ClipToBounds = true };
    private string _breadcrumbSummary = "";
    private readonly TextBox _filter = new() { Watermark = "Filter this file list", MinWidth = 90 };
    private readonly ComboBox _viewMode = new() { ItemsSource = new[] { "Details", "Tiles", "Filmstrip" }, Width = 86, SelectedIndex = 0 };
    private readonly DataGrid _files;
    private readonly ListBox _tiles;
    private bool _filmstrip;
    private readonly Grid _listHost = new();
    private readonly Dictionary<string, Button> _headers = [];
    private readonly Dictionary<DataGridColumn, string> _columnIds = [];
    private readonly Button _back;
    private readonly Button _forward;
    private readonly Button _closeTab;
    private CancellationTokenSource? _readCancellation;
    private FileSystemWatcher? _watcher;
    private readonly DispatcherTimer _watchDebounce = new() { Interval = TimeSpan.FromMilliseconds(450) };
    private readonly SemaphoreSlim _filterGate = new(1, 1);
    private CancellationTokenSource? _filterCancellation;
    private long _filterGeneration;
    private bool _refreshPending;
    private long _watchGeneration;
    private long _watchUiDispatches;
    internal long WatcherUiDispatches => Interlocked.Read(ref _watchUiDispatches);
    private string _listingNotice = "";
    private string? _listedDirectory;
    private string _lastScheduledFilter = "";
    private int _activeIndex;
    private long _readGeneration;
    private bool _applying;
    private bool _applyingColumns;
    private bool _disposed;
    private readonly Dictionary<string, WeakReference<ContentControl>> _nameCells = new(PathComparer);
    private PointerPressedEventArgs? _dragPress;
    private Point _dragStart;
    private bool _dragging;
    private IReadOnlyList<FileEntry> _all = [];
    private IReadOnlyList<FileEntry> _visible = [];
    private Dictionary<string, ListarySearchResult> _indexed = new(PathComparer);
    private ListarySearchResult? _currentDirectoryIndexedIdentity;

    public event Action<PaneView>? Activated;
    public event Action<PaneView>? SelectionChanged;
    public event Action<PaneView>? Navigated;
    internal event Action<PaneView>? FolderCommitted;
    internal event Action<PaneView, Guid>? LinkedTabSelected;
    internal event Action<Guid>? UnlinkTabRequested;
    internal event Func<PaneView, int, bool>? ToggleTabLinkRequested;
    internal event Func<PaneView, int, bool>? ToggleLinkedNavigationRequested;
    internal Func<Guid, bool>? IsLinkedNavigationLocked { get; set; }
    internal Guid? ActiveTabLink => CurrentTab.Settings.LinkedTabGroup;
    internal bool CanLinkTabs { get; set; }
    internal object ActiveTabIdentity => CurrentTab;
    private object? _committedTab;
    private readonly Func<string, FolderSizeMode> _folderSizeMode;
    private readonly Func<bool> _cacheFolderSizesInHistory;
    private readonly Func<bool> _updateFolderSizesAfterOperations;
    private readonly Func<(bool FollowNested, bool SizeLinkedFolders)> _folderSizeLinks;
    internal bool LastNavigationSucceeded { get; private set; }
    public event Action<Exception>? Error;
    public event Func<PaneView, FileEntry, Task>? OpenRequested;
    public event Func<PaneView, IReadOnlyList<string>, bool, bool, Task>? FilesDropped;
    public event Action<PaneView, string>? CommandRequested;
    public event Func<PaneView, Task<IReadOnlyList<FileEntry>>>? ValidateSelectionRequested;
    public event Func<PaneView, FileEntry, string, Task>? RenameRequested;
    public string CurrentDirectory => CurrentTab.Settings.Path;
    internal string InitialDirectory => CurrentTab.InitialDirectory ?? CurrentDirectory;
    public string FilterText
    {
        get => _filter.Text ?? "";
        set
        {
            _filter.Text = value;
            // TextChanged may be queued by the control. Programmatic callers need
            // FilteringTask to describe this exact request before the setter returns.
            if (!_applying) QueueFilterAndSort();
        }
    }
    public bool IsSearchResults { get; private set; }
    public bool IsLoading { get; private set; }
    public Task LoadingTask { get; private set; } = Task.CompletedTask;
    public Task FilteringTask { get; private set; } = Task.CompletedTask;
    public (long Navigation, long Filter) ViewRevision => (_readGeneration, _filterGeneration);
    public IReadOnlyList<FileEntry> VisibleEntries => _visible;
    public DataGrid FileGrid => _files;
    public TextBox PathBox => _path;
    internal Button BreadcrumbButton => _pathBreadcrumb;
    internal string BreadcrumbText => _breadcrumbSummary;
    internal IReadOnlyList<Button> BreadcrumbAncestorButtons => _breadcrumbSegments.Children.OfType<Button>()
        .Where(button => button.Name == "LocationAncestor").ToArray();
    public int TabCount => _tabs.Count;
    internal int ActiveTabIndex => _activeIndex;
    private LockedFolderFormat? ActiveFormatLock => IsSearchResults ? null : CurrentTab.Settings.FormatLock;
    internal bool IsFormatLocked => CurrentTab.Settings.FormatLock is not null;
    internal string CurrentViewMode => ActiveFormatLock?.ViewMode ??
        (!IsSearchResults ? _folderViewMode : null) ?? CurrentTab.Settings.ViewMode;
    internal string CurrentSortColumn => ActiveFormatLock?.SortColumn ??
        (!IsSearchResults ? _folderSort?.Column : null) ?? CurrentTab.Settings.SortColumn;
    internal bool CurrentSortDescending => ActiveFormatLock?.Descending ??
        (!IsSearchResults ? _folderSort?.Descending : null) ?? CurrentTab.Settings.Descending;
    internal bool CurrentTabLocked => CurrentTab.Settings.Locked;
    internal IReadOnlyList<FileColumnSettings> CurrentColumnLayout
    {
        get
        {
            SyncCurrentColumnLayout();
            return EffectiveColumns.Select(column => column.Copy()).ToArray();
        }
    }
    internal IReadOnlyList<string> VisibleColumnIds => _files.Columns.OrderBy(column => column.DisplayIndex)
        .Select(column => _columnIds.GetValueOrDefault(column)).OfType<string>().ToArray();
    private PaneTab CurrentTab => _tabs[Math.Clamp(_activeIndex, 0, _tabs.Count - 1)];
    private static StringComparer PathComparer => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private readonly Func<string, Func<FolderColumnRule, bool>?, FolderColumnRule?> _folderColumns;
    private List<FileColumnSettings>? _resolvedColumns;
    private FolderColumnRule? _appliedFolderRule;
    private FolderColumnRule? _appliedColumnSource;
    private FolderColumnRule? _appliedSortSource;
    private FolderColumnRule? _appliedViewSource;
    private FolderColumnRule? _appliedCheckboxSource;
    private string? _folderRuleDirectory;
    private bool _temporaryUserDefault;
    private (string Column, bool Descending)? _folderSort;
    private string? _folderViewMode;
    private bool? _folderCheckboxMode;
    private PaneTab? _folderRuleTab;
    private List<FileColumnSettings> EffectiveColumns
    {
        get => ActiveFormatLock?.Columns ?? (IsSearchResults ? null : _resolvedColumns) ?? CurrentTab.Settings.Columns;
        set
        {
            if (ActiveFormatLock is { } format) CurrentTab.Settings.FormatLock = format with { Columns = value };
            else if (IsSearchResults || _resolvedColumns is null) CurrentTab.Settings.Columns = value;
            else _resolvedColumns = value;
        }
    }

    internal void ReloadFolderColumns(string directory, bool force = false)
    {
        var rule = _folderColumns(directory, null);
        var columnSource = rule?.InheritColumns == true ? _folderColumns(directory, static rule => !rule.InheritColumns) : rule;
        var sortSource = rule?.InheritSort == true ? _folderColumns(directory, static rule => !rule.InheritSort && rule.SortColumn is not null) : rule;
        var viewSource = rule?.InheritView == true ? _folderColumns(directory, static rule => !rule.InheritView && rule.ViewMode is not null) : rule;
        var checkboxSource = _folderColumns(directory, static rule => rule.CheckboxMode.HasValue);
        if (!force && Equals(rule, _appliedFolderRule) && Equals(columnSource, _appliedColumnSource) &&
            Equals(sortSource, _appliedSortSource) && Equals(viewSource, _appliedViewSource) && Equals(checkboxSource, _appliedCheckboxSource) &&
            ReferenceEquals(_folderRuleTab, CurrentTab) &&
            ((rule is null && !_temporaryUserDefault) || PathComparer.Equals(directory, _folderRuleDirectory))) return;
        _temporaryUserDefault = false;
        _folderRuleDirectory = directory;
        _folderRuleTab = CurrentTab;
        _appliedFolderRule = rule;
        _appliedColumnSource = columnSource;
        _appliedSortSource = sortSource;
        _appliedViewSource = viewSource;
        _appliedCheckboxSource = checkboxSource;
        _resolvedColumns = columnSource?.Columns.Select(column => column.Copy()).ToList();
        _folderSort = sortSource?.SortColumn is { } sort ? (sort, sortSource.SortDescending) : null;
        _folderViewMode = viewSource?.ViewMode;
        _folderCheckboxMode = checkboxSource?.CheckboxMode;
        ApplyColumnLayout();
        CheckboxesChanged?.Invoke();
    }

    public PaneView(DirectoryBrowser browser, PaneSettings saved, Func<bool> showHidden,
        Func<string, Func<FolderColumnRule, bool>?, FolderColumnRule?>? folderColumns = null,
        Func<string, string, Task<string>>? expandLocationAlias = null, Func<string, FolderSizeMode>? folderSizeMode = null,
        Func<bool>? cacheFolderSizesInHistory = null, Func<bool>? updateFolderSizesAfterOperations = null,
        Func<(bool FollowNested, bool SizeLinkedFolders)>? folderSizeLinks = null)
    {
        _browser = browser;
        _showHidden = showHidden;
        _folderColumns = folderColumns ?? ((_, _) => null);
        _folderSizeMode = folderSizeMode ?? (_ => FolderSizeMode.Off);
        _cacheFolderSizesInHistory = cacheFolderSizesInHistory ?? (() => false);
        _updateFolderSizesAfterOperations = updateFolderSizesAfterOperations ?? (() => false);
        _folderSizeLinks = folderSizeLinks ?? (() => (false, false));
        foreach (var settings in saved.Tabs)
            _tabs.Add(new PaneTab(settings));
        if (_tabs.Count == 0) _tabs.Add(new PaneTab(new TabSettings()));
        _activeIndex = Math.Clamp(saved.ActiveTab, 0, _tabs.Count - 1);

        // The installed Opus reference uses a subtle active-file-display cue rather
        // than a full-width Source/Destination banner. Keep the role in automation
        // state while projecting it visually as a narrow accent strip.
        _role.IsVisible = false;
        _roleBar = new Border { Height = 2, Background = new SolidColorBrush(Color.Parse("#B56A21")) };
        _back = Ui.IconButton("back", "Back", () => Observe(HistoryAsync(-1)));
        _forward = Ui.IconButton("forward", "Forward", () => Observe(HistoryAsync(1)));
        var navigation = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), ColumnSpacing = 3 };
        navigation.Children.Add(Ui.Row(_back, _forward,
            Ui.IconButton("up", "Parent folder (Backspace)", () => Observe(ParentAsync())),
            Ui.IconButton("refresh", "Refresh (F5)", () => Observe(RefreshAsync())),
            BuildHomeButton()));
        var location = new Grid { MinWidth = 0, ClipToBounds = true };
        _pathBreadcrumb.Content = _pathLabel;
        _pathBreadcrumb.MaxWidth = 166;
        AutomationProperties.SetName(_pathBreadcrumb, "Edit folder location");
        ToolTip.SetTip(_pathBreadcrumb, "Click to edit the full folder path (F4 or Ctrl+L)");
        _pathBreadcrumb.Click += (_, _) => FocusPath();
        _breadcrumbSegments.Children.Add(_pathBreadcrumb);
        location.Children.Add(_breadcrumbSegments);
        location.Children.Add(_path);
        _path.IsVisible = false;
        _path.LostFocus += (_, _) => EndPathEditing();
        Grid.SetColumn(location, 1);
        navigation.Children.Add(location);
        Grid.SetColumn(_viewMode, 2);
        navigation.Children.Add(_viewMode);
        _path.FontSize = 11;
        _path.BorderThickness = new Thickness(0);
        _path.Background = Brushes.Transparent;
        _path.Padding = new Thickness(4, 0);
        _path.KeyDown += async (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                _path.Text = CurrentDirectory;
                EndPathEditing();
                FocusList();
                e.Handled = true;
                return;
            }
            if (e.Key != Key.Enter) return;
            var input = _path.Text ?? "";
            e.Handled = true;
            var revision = _readGeneration;
            string target;
            try { target = expandLocationAlias is null ? input : await expandLocationAlias(input, InitialDirectory); }
            catch (OperationCanceledException) { return; }
            catch (Exception error) { if (!_disposed && _path.IsVisible && _path.Text == input) Error?.Invoke(error); return; }
            if (_disposed || revision != _readGeneration || !_path.IsVisible || _path.Text != input) return;
            Observe(NavigateAsync(target));
            EndPathEditing();
            FocusList();
        };

        _files = Ui.Grid();
        _files.Name = "FileList";
        ApplyColumnLayout();
        _files.ColumnReordered += (_, _) => OnLiveColumnLayoutChanged();
        _files.SelectionChanged += (_, _) => OnSelectionChanged();
        _files.DoubleTapped += async (_, e) =>
        {
            if (e.Source is Control control && control.FindAncestorOfType<DataGridColumnHeader>() is not null) return;
            if (_files.SelectedItem is FileEntry entry) await OpenAsync(entry);
        };
        _tiles = new ListBox
        {
            Name = "FileTiles",
            SelectionMode = SelectionMode.Multiple, IsVisible = false,
            ItemTemplate = new FuncDataTemplate<FileEntry>((entry, _) => CreateFileTile(entry, filmstrip: false))
        };
        _tiles.SelectionChanged += (_, _) => OnSelectionChanged();
        _tiles.DoubleTapped += async (_, _) => { if (_tiles.SelectedItem is FileEntry entry) await OpenAsync(entry); };
        _listHost.Children.Add(_files);
        _listHost.Children.Add(_tiles);
        _listHost.SizeChanged += (_, _) => ApplyCompactDetailsColumns();
        var contextMenu = new ContextMenu();
        foreach (var (label, command) in new[] { ("Open", "open"), ("Open in new tab", "new-tab-selected"), ("Quick Look", "quick-look"),
                     (OperatingSystem.IsMacOS() ? "Reveal in Finder" : "Reveal in Explorer", "reveal"),
                     ("Cut", "cut"), ("Copy", "copy-clipboard"), ("Paste", "paste"), ("Copy path", "copy-paths"),
                     ("Rename", "rename"), ("Move to Trash", "trash"), ("Properties", "properties") })
        {
            var item = new MenuItem { Header = label };
            item.Click += (_, _) => { Activate(); CommandRequested?.Invoke(this, command); };
            contextMenu.Items.Add(item);
        }
        _files.ContextMenu = contextMenu;
        _tiles.ContextMenu = contextMenu;
        foreach (var control in new Control[] { _files, _tiles })
        {
            control.AddHandler(PointerPressedEvent, OnPointerPressed, RoutingStrategies.Tunnel);
            control.PointerMoved += OnPointerMoved;
            control.PointerReleased += (_, _) =>
            {
                _dragPress = null;
                if (ReferenceEquals(control, _files)) OnLiveColumnLayoutChanged();
            };
            DragDrop.SetAllowDrop(control, true);
            control.AddHandler(DragDrop.DragOverEvent, OnDragOver);
            control.AddHandler(DragDrop.DropEvent, OnDrop);
        }

        _filterHost = Ui.Surface(_filter, padding: new Thickness(3, 0, 3, 2));
        _filterHost.IsVisible = !string.IsNullOrWhiteSpace(FilterText);
        _filter.TextChanged += (_, _) =>
        {
            if (!_applying && !string.Equals(FilterText, _lastScheduledFilter, StringComparison.Ordinal))
                QueueFilterAndSort(debounce: true);
        };
        _filter.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape) return;
            _filter.Text = "";
            _filterHost.IsVisible = false;
            FocusList();
            e.Handled = true;
        };
        _viewMode.SelectionChanged += (_, _) =>
        {
            if (_applying || _viewMode.SelectedItem is not string mode) return;
            SetViewMode(mode);
        };
        _closeTab = Ui.IconButton("close", "Close current tab", CloseCurrentTab);
        var tabsRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"), ColumnSpacing = 2 };
        tabsRow.Children.Add(new ScrollViewer { Content = _tabButtons, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto, VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled });
        var addTab = Ui.IconButton("add", "New tab", () => Observe(AddTabAsync(CurrentDirectory)));
        Grid.SetColumn(addTab, 1); tabsRow.Children.Add(addTab);
        Grid.SetColumn(_closeTab, 2); tabsRow.Children.Add(_closeTab);
        var root = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,Auto,*,Auto,Auto") };
        root.Children.Add(_roleBar);
        AddRow(root, Ui.Surface(navigation, padding: new Thickness(3, 1)), 1);
        AddRow(root, _filterHost, 2);
        AddRow(root, _listHost, 3);
        AddRow(root, Ui.Surface(tabsRow, padding: new Thickness(2, 1)), 4);
        void RequestFormatCommand(string command)
        {
            Activate();
            CommandRequested?.Invoke(this, command);
        }
        _formatStatus = Ui.IconButton("format-info", "Folder format", () => RequestFormatCommand("folder-format"));
        _formatStatus.Name = "FolderFormatStatus";
        var formatAction = new MenuItem { Header = "Folder Format…" };
        formatAction.Click += (_, _) => RequestFormatCommand("folder-format");
        _formatLockAction.Click += (_, _) => RequestFormatCommand("lock-format");
        var defaultFormat = new MenuItem { Header = "Edit User Default Format…" };
        defaultFormat.Click += (_, _) => RequestFormatCommand("edit-default-format");
        var manageFormats = new MenuItem { Header = "Manage Folder Formats…" };
        manageFormats.Click += (_, _) => RequestFormatCommand("manage-folder-formats");
        var resetFormat = new MenuItem { Header = "Reset to Folder's Format" };
        resetFormat.Click += (_, _) => RequestFormatCommand("reset-folder-format");
        var resetDefaultFormat = new MenuItem { Header = "Reset to User Default Format" };
        resetDefaultFormat.Click += (_, _) => RequestFormatCommand("reset-default-format");
        _formatStatus.ContextMenu = new ContextMenu
        {
            Items = { _formatLockAction, formatAction, manageFormats, defaultFormat, resetFormat, resetDefaultFormat }
        };
        var statusBar = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto,Auto,Auto"), ColumnSpacing = 4 };
        _status.TextTrimming = TextTrimming.CharacterEllipsis;
        _status.VerticalAlignment = VerticalAlignment.Center;
        _freeSpaceStatus.Name = "FolderFreeSpaceStatus";
        _freeSpaceStatus.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(_fileTypeStatus, 1);
        Grid.SetColumn(_formatStatus, 2);
        Grid.SetColumn(_freeSpaceStatus, 3);
        Grid.SetColumn(_diskCapacity, 4);
        _fileTypeStatus.Click += (_, _) =>
        {
            Activate();
            CommandRequested?.Invoke(this, "file-type-summary");
        };
        statusBar.SizeChanged += (_, change) =>
        {
            var limit = change.NewSize.Width < 390 ? 1 : change.NewSize.Width < 520 ? 2 : 3;
            if (limit == _statusBadgeLimit) return;
            _statusBadgeLimit = limit;
            if (_selectedFileTypes.Length > 0)
                _fileTypeStatus.Content = FileTypeSummary.StatusBadges(_selectedFileTypes, limit);
        };
        statusBar.Children.Add(_status);
        statusBar.Children.Add(_fileTypeStatus);
        statusBar.Children.Add(_formatStatus);
        statusBar.Children.Add(_freeSpaceStatus);
        statusBar.Children.Add(_diskCapacity);
        AddRow(root, Ui.Surface(statusBar, padding: new Thickness(5, 2)), 5);
        Content = root;
        GotFocus += (_, _) => Activate();
        _watchDebounce.Tick += (_, _) =>
        {
            _watchDebounce.Stop();
            if (_disposed || IsSearchResults) return;
            if (IsLoading) _refreshPending = true;
            else Observe(RefreshAsync(afterFileOperation: true));
        };
        RenderTabs();
    }

    private static void AddRow(Grid grid, Control control, int row) { Grid.SetRow(control, row); grid.Children.Add(control); }
    public void Activate() => Activated?.Invoke(this);
    public void SetActive(bool active)
    {
        _role.Text = active ? "Source" : "Destination";
        _roleBar.Background = new SolidColorBrush(Color.Parse(active ? "#B56A21" : "#2C2C2C"));
        AutomationProperties.SetName(this, active ? "Source file display" : "Destination file display");
    }
    public void FocusList() { if (_tiles.IsVisible) _tiles.Focus(); else _files.Focus(); }
    public void FocusPath()
    {
        _pathBreadcrumb.IsVisible = false;
        _breadcrumbSegments.IsVisible = false;
        _path.IsVisible = true;
        _path.Focus();
        _path.SelectAll();
    }
    private void EndPathEditing()
    {
        _path.IsVisible = false;
        _breadcrumbSegments.IsVisible = true;
        _pathBreadcrumb.IsVisible = true;
    }
    private void UpdateBreadcrumb(string path)
    {
        _breadcrumbSummary = FormatBreadcrumb(path);
        var root = Path.GetPathRoot(path) ?? "";
        var parts = path[root.Length..].Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
        var folders = new List<(string Label, string Path)>();
        if (!string.IsNullOrWhiteSpace(root)) folders.Add((root, root));
        var accumulated = root;
        foreach (var part in parts)
        {
            accumulated = Path.Combine(accumulated, part);
            folders.Add((part, accumulated));
        }

        _breadcrumbSegments.Children.Clear();
        if (folders.Count > 1)
        {
            var parent = folders.Count - 2;
            if (parent > 0) AddBreadcrumbAncestor("...", folders[parent - 1].Path);
            AddBreadcrumbAncestor(folders[parent].Label, folders[parent].Path);
        }
        _pathLabel.Text = folders.Count > 0 ? folders[^1].Label : path;
        _breadcrumbSegments.Children.Add(_pathBreadcrumb);
        ToolTip.SetTip(_pathBreadcrumb, path);
    }
    private void AddBreadcrumbAncestor(string label, string path)
    {
        var button = Ui.Button(label, () => Observe(NavigateAsync(path)), path);
        button.Name = "LocationAncestor";
        button.MaxWidth = 102;
        button.MinHeight = 20;
        button.Padding = new Thickness(2, 0);
        button.Background = Brushes.Transparent;
        button.BorderThickness = new Thickness(0);
        button.Content = new TextBlock
        {
            Text = label, TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = 90, VerticalAlignment = VerticalAlignment.Center
        };
        AutomationProperties.SetName(button, $"Open ancestor folder: {path}");
        _breadcrumbSegments.Children.Add(button);
        var separator = Ui.Text(">");
        separator.Margin = new Thickness(2, 0);
        _breadcrumbSegments.Children.Add(separator);
    }
    private static string FormatBreadcrumb(string path)
    {
        var root = Path.GetPathRoot(path) ?? "";
        var parts = path[root.Length..].Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return root;
        var leaf = parts[^1];
        if (parts.Length == 1) return string.IsNullOrEmpty(root) ? leaf : root + leaf;
        // The location shares a narrow lister header with navigation and view controls.
        // Keep the actual current folder visible instead of truncating it behind a long
        // root, profile directory or disposable-session identifier.
        if (leaf.Length > 24) return "... > " + leaf;
        var parent = parts[^2];
        if (parent.Length > 14) parent = parent[..11] + "...";
        return (parts.Length > 2 ? "... > " : "") + parent + " > " + leaf;
    }
    public void FocusFilter() { _filterHost.IsVisible = true; _filter.Focus(); _filter.SelectAll(); }

    public IReadOnlyList<FileEntry> SelectedEntries => (_tiles.IsVisible ? _tiles.SelectedItems : _files.SelectedItems)?.Cast<FileEntry>().ToArray() ?? [];
    internal bool CheckboxMode => ActiveFormatLock?.CheckboxMode ??
        (!IsSearchResults ? _folderCheckboxMode : null) ?? CurrentTab.Settings.CheckboxMode;
    internal IReadOnlyList<FileEntry> ActionEntries => CheckboxMode
        ? _visible.Where(entry => CurrentTab.CheckedPaths.Contains(entry.FullPath)).ToArray() : SelectedEntries;
    private event Action? CheckboxesChanged;

    internal void SetCheckboxMode(bool enabled)
    {
        if (CheckboxMode == enabled) return;
        if (ActiveFormatLock is { } format) CurrentTab.Settings.FormatLock = format with { CheckboxMode = enabled };
        else if (!IsSearchResults && _folderCheckboxMode.HasValue) _folderCheckboxMode = enabled;
        else CurrentTab.Settings.CheckboxMode = enabled;
        if (!enabled) CurrentTab.CheckedPaths.Clear();
        CheckboxesChanged?.Invoke();
        UpdateStatus();
        Navigated?.Invoke(this);
    }

    internal void SelectActionPaths(IEnumerable<string> paths)
    {
        if (!CheckboxMode) { SelectPaths(paths); return; }
        var wanted = paths.ToHashSet(PathComparer);
        var shown = _visible.Select(entry => entry.FullPath).ToHashSet(PathComparer);
        CurrentTab.CheckedPaths.RemoveWhere(shown.Contains);
        CurrentTab.CheckedPaths.UnionWith(_visible.Where(entry => wanted.Contains(entry.FullPath)).Select(entry => entry.FullPath));
        CheckboxesChanged?.Invoke();
        UpdateStatus();
    }

    internal void ToggleFocusedCheckbox()
    {
        if (!CheckboxMode) return;
        var focused = TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() as Visual;
        var row = focused as DataGridRow ?? focused?.FindAncestorOfType<DataGridRow>();
        var entry = (_tiles.IsVisible ? _tiles.SelectedItem : row?.DataContext ?? _files.SelectedItem) as FileEntry
            ?? SelectedEntries.FirstOrDefault();
        if (entry is null) return;
        if (!CurrentTab.CheckedPaths.Add(entry.FullPath)) CurrentTab.CheckedPaths.Remove(entry.FullPath);
        CheckboxesChanged?.Invoke();
        UpdateStatus();
    }

    private CheckBox CreateFileCheckbox(FileEntry entry)
    {
        var check = new CheckBox { Name = "FileRowCheckbox", MinWidth = 0, MinHeight = 0,
            Padding = new Thickness(0), VerticalAlignment = VerticalAlignment.Center, Focusable = false };
        AutomationProperties.SetName(check, $"Check {entry.Name}");
        void Synchronize()
        {
            check.IsVisible = CheckboxMode;
            check.IsChecked = CurrentTab.CheckedPaths.Contains(entry.FullPath);
        }
        check.AttachedToVisualTree += (_, _) => { CheckboxesChanged += Synchronize; Synchronize(); };
        check.DetachedFromVisualTree += (_, _) => CheckboxesChanged -= Synchronize;
        check.Click += (_, args) =>
        {
            if (check.IsChecked == true) CurrentTab.CheckedPaths.Add(entry.FullPath);
            else CurrentTab.CheckedPaths.Remove(entry.FullPath);
            CheckboxesChanged?.Invoke();
            UpdateStatus();
            args.Handled = true;
        };
        Synchronize();
        return check;
    }
    public ListarySearchResult? IndexedIdentity(FileEntry entry) => _indexed.GetValueOrDefault(entry.FullPath);
    internal ListarySearchResult? CurrentDirectoryIndexedIdentity => _currentDirectoryIndexedIdentity;
    public PaneSettings Capture()
    {
        SyncCurrentColumnLayout();
        return new PaneSettings { Tabs = _tabs.Select(x => CloneSettings(x.Settings)).ToList(), ActiveTab = _activeIndex };
    }

    public async Task ApplySnapshotAsync(PaneSettings snapshot)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(snapshot);
        var next = CloneSettings(snapshot);
        if (next.Tabs.Count == 0) throw new InvalidOperationException("A saved Lister pane must contain at least one folder tab.");
        var previous = Capture();
        try
        {
            ReplaceTabs(next);
            _filter.Text = "";
            _lastScheduledFilter = "";
            var expected = DirectoryBrowser.ResolvePath(CurrentDirectory);
            await NavigateWithoutFolderNotificationAsync(expected, addHistory: false);
            if (IsSearchResults || _listedDirectory is null || !PathComparer.Equals(_listedDirectory, expected))
                throw new IOException($"The saved folder could not be restored: {expected}");
        }
        catch (Exception restoreError)
        {
            try
            {
                ReplaceTabs(previous);
                _filter.Text = "";
                _lastScheduledFilter = "";
                var original = DirectoryBrowser.ResolvePath(CurrentDirectory);
                await NavigateWithoutFolderNotificationAsync(original, addHistory: false);
                if (IsSearchResults || _listedDirectory is null || !PathComparer.Equals(_listedDirectory, original))
                    throw new IOException($"The previous folder could not be restored: {original}");
            }
            catch (Exception rollbackError)
            {
                throw new AggregateException("The saved Lister pane failed to load and its previous pane state could not be fully restored.",
                    restoreError, rollbackError);
            }
            throw;
        }
    }

    private void ReplaceTabs(PaneSettings snapshot)
    {
        _tabs.Clear();
        foreach (var tab in snapshot.Tabs) _tabs.Add(new PaneTab(CloneSettings(tab)));
        _activeIndex = Math.Clamp(snapshot.ActiveTab, 0, _tabs.Count - 1);
        ApplyColumnLayout();
        RenderTabs();
    }

    private static PaneSettings CloneSettings(PaneSettings source)
    {
        var tabs = (source.Tabs ?? []).Where(tab => tab is not null).Select(CloneSettings).ToList();
        if (tabs.Count == 0) tabs.Add(new TabSettings());
        return new PaneSettings { Tabs = tabs, ActiveTab = Math.Clamp(source.ActiveTab, 0, tabs.Count - 1) };
    }

    private static TabSettings CloneSettings(TabSettings source) => new()
    {
        Path = source.Path,
        ViewMode = source.ViewMode,
        CheckboxMode = source.CheckboxMode,
        SortColumn = source.SortColumn,
        Descending = source.Descending,
        Locked = source.Locked,
        LinkedTabGroup = source.LinkedTabGroup,
        FormatLock = source.FormatLock?.Copy(),
        Columns = (source.Columns ?? []).Where(column => column is not null).Select(column => column.Copy()).ToList()
    };

    public bool BeginInlineRename()
    {
        var entry = SelectedEntries.FirstOrDefault();
        if (entry is null || _tiles.IsVisible || IsLoading) return false;
        ContentControl? cell = null;
        if (_nameCells.TryGetValue(entry.FullPath, out var weak)) weak.TryGetTarget(out cell);
        // Virtualized grids can retain a visible cell when ItemsSource changes to a new snapshot.
        // Resolve that already-realized cell without retaining old folder paths indefinitely.
        cell ??= _files.GetVisualDescendants().OfType<ContentControl>()
            .FirstOrDefault(control => control.Tag is string path && PathComparer.Equals(path, entry.FullPath));
        if (cell is null) return false;
        var original = cell.Content;
        var editor = new TextBox { Text = entry.Name, MinHeight = 22, Padding = new Thickness(2, 0), Name = "InlineRename" };
        cell.Content = editor;
        var finished = false;
        editor.KeyDown += async (_, e) =>
        {
            if (e.Key is not (Key.Enter or Key.Escape)) return;
            e.Handled = true;
            if (finished) return;
            finished = true;
            cell.Content = original;
            FocusList();
            if (e.Key == Key.Enter && RenameRequested is not null)
            {
                try { await RenameRequested(this, entry, editor.Text ?? ""); }
                catch (Exception error) { Error?.Invoke(error); }
            }
        };
        editor.LostFocus += (_, _) => { if (!finished) { finished = true; cell.Content = original; } };
        editor.Focus();
        editor.SelectionStart = 0;
        editor.SelectionEnd = entry.IsDirectory ? entry.Name.Length : Path.GetFileNameWithoutExtension(entry.Name).Length;
        return true;
    }

    public Task NavigateAsync(string target, bool addHistory = true, ListarySearchResult? indexedDirectoryIdentity = null)
    {
        LoadingTask = NavigateCoreAsync(target, addHistory, indexedDirectoryIdentity, notifyFolderChanged: true);
        return LoadingTask;
    }

    internal Task NavigateWithoutFolderNotificationAsync(string target, bool addHistory = true)
    {
        LoadingTask = NavigateCoreAsync(target, addHistory, null, notifyFolderChanged: false);
        return LoadingTask;
    }

    private async Task NavigateCoreAsync(string target, bool addHistory, ListarySearchResult? indexedDirectoryIdentity,
        bool notifyFolderChanged, bool fromHistory = false, bool refreshMeasuredSizes = false)
    {
        if (_disposed) return;
        LastNavigationSucceeded = false;
        var resolved = DirectoryBrowser.ResolvePath(target, CurrentDirectory);
        if (CurrentTab.Settings.Locked && !PathComparer.Equals(resolved, CurrentDirectory))
        {
            await AddTabAsync(resolved, indexedDirectoryIdentity);
            return;
        }
        // Invalidate a prior folder's filter immediately. Waiting until the
        // new listing completes allows its old result to replace the lister
        // while the next directory is already loading.
        _filterCancellation?.Cancel();
        ++_filterGeneration;
        _readCancellation?.Cancel();
        _readCancellation?.Dispose();
        // A prior folder's queued watcher callback or debounce must never
        // refresh a newly selected directory after navigation begins.
        ++_watchGeneration;
        _watchDebounce.Stop();
        _refreshPending = false;
        var cancellation = _readCancellation = new CancellationTokenSource();
        var generation = ++_readGeneration;
        var previousSelection = SelectedEntries.Select(x => x.FullPath).ToArray();
        var preserveFilter = !IsSearchResults && PathComparer.Equals(resolved, _listedDirectory);
        var measured = refreshMeasuredSizes && preserveFilter
            ? _all.Where(entry => entry.CalculatedFolderLength.HasValue)
                .ToDictionary(entry => entry.FullPath, entry => entry.CalculatedFolderLength!.Value, PathComparer)
            : null;
        IsLoading = true;
        _listHost.IsEnabled = false;
        _filter.IsEnabled = false;
        _viewMode.IsEnabled = false;
        _path.Text = resolved;
        UpdateBreadcrumb(resolved);
        _status.Text = "Reading folder…";
        try
        {
            var progress = new Progress<DirectoryBatch>(batch =>
            {
                if (generation == _readGeneration) _status.Text = $"Reading folder… {batch.Discovered:N0} items";
            });
            var listing = await _browser.ReadAsync(resolved, new BrowseOptions(ShowHidden: _showHidden()), progress, cancellation.Token);
            if (generation != _readGeneration || _disposed) return;
            if (listing.RootError is not null)
                throw new IOException($"Could not read folder: {listing.RootError}");
            var leftSearch = IsSearchResults;
            IsSearchResults = false;
            _duplicateGroups = null;
            _activeDuplicateResult = null;
            CurrentTab.DuplicateResults = null;
            ReloadFolderColumns(listing.Directory);
            if (leftSearch) ApplyColumnLayout();
            _all = listing.Entries;
            if (fromHistory && _cacheFolderSizesInHistory() &&
                CurrentTab.CachedFolderSizes.TryGetValue(listing.Directory, out var cachedSizes))
                _all = ApplyFolderSizes(_all, cachedSizes);
            else CurrentTab.CachedFolderSizes.Remove(listing.Directory);
            if (measured is { Count: > 0 }) _all = ApplyFolderSizes(_all, measured);
            _indexed.Clear();
            UpdateSortHeaders();
            _listingNotice = string.Join(" · ", listing.Errors.Take(2));
            if (listing.Truncated)
                _listingNotice += (_listingNotice.Length > 0 ? " · " : "") + "Listing limit reached; narrow this folder";
            _applying = true;
            if (!preserveFilter) _filter.Text = "";
            _viewMode.SelectedItem = CurrentViewMode;
            _applying = false;
            ApplyViewMode();
            await FilterAndSortAsync();
            if (generation != _readGeneration || _disposed) return;
            CurrentTab.InitialDirectory ??= listing.Directory;
            if (!PathComparer.Equals(CurrentTab.Settings.Path, listing.Directory))
                CurrentTab.CheckedPaths.Clear();
            CurrentTab.Settings.Path = listing.Directory;
            CheckboxesChanged?.Invoke();
            _path.Text = listing.Directory;
            UpdateBreadcrumb(listing.Directory);
            var folderChanged = !PathComparer.Equals(_listedDirectory, listing.Directory) ||
                !ReferenceEquals(_committedTab, CurrentTab);
            _listedDirectory = listing.Directory;
            _committedTab = CurrentTab;
            LastNavigationSucceeded = true;
            _currentDirectoryIndexedIdentity = indexedDirectoryIdentity;
            if (addHistory) CurrentTab.Visit(listing.Directory);
            SelectPaths(previousSelection);
            RenderTabs();
            SetWatcher(listing.Directory);
            Navigated?.Invoke(this);
            if (notifyFolderChanged && folderChanged) FolderCommitted?.Invoke(this);
            var sizeMode = _folderSizeMode(listing.Directory);
            if (sizeMode != FolderSizeMode.Off || measured is { Count: > 0 })
                Observe(CalculateAutomaticFolderSizesAsync(listing with { Entries = _all }, sizeMode, generation,
                    cancellation.Token, refreshMeasuredSizes: measured is { Count: > 0 }));
        }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            if (generation != _readGeneration) return;
            // The previous folder remains committed after a failed navigation.
            // Reapply its filter because starting this read invalidated any
            // pending filter job; otherwise the visible rows can disagree with
            // the still-displayed filter text. Keep the actual read error visible.
            await FilterAndSortAsync();
            if (generation != _readGeneration || _disposed) return;
            // Navigation invalidated the previous watcher generation before
            // attempting this read. The original directory is still committed,
            // so restore monitoring for it after the failed destination.
            if (!IsSearchResults && _listedDirectory is not null &&
                PathComparer.Equals(_listedDirectory, CurrentDirectory))
                SetWatcher(CurrentDirectory);
            _path.Text = CurrentDirectory;
            UpdateBreadcrumb(CurrentDirectory);
            _status.Text = $"Could not read folder: {error.Message}";
            Error?.Invoke(error);
        }
        finally
        {
            if (generation == _readGeneration)
            {
                IsLoading = false;
                _listHost.IsEnabled = true;
                _filter.IsEnabled = true;
                _viewMode.IsEnabled = true;
                if (!_disposed) OnSelectionChanged();
                if (_refreshPending && !_disposed && !IsSearchResults)
                {
                    _refreshPending = false;
                    _watchDebounce.Stop();
                    _watchDebounce.Start();
                }
            }
        }
    }

    public Task RefreshAsync(bool afterFileOperation = false)
    {
        LoadingTask = NavigateCoreAsync(CurrentDirectory, false, _currentDirectoryIndexedIdentity,
            notifyFolderChanged: true, refreshMeasuredSizes: afterFileOperation && _updateFolderSizesAfterOperations());
        return LoadingTask;
    }
    public Task ParentAsync()
    {
        var parent = Directory.GetParent(CurrentDirectory)?.FullName;
        return parent is null ? Task.CompletedTask : NavigateAsync(parent);
    }
    public Task HistoryAsync(int delta)
    {
        var target = CurrentTab.Move(delta);
        if (target is null) return Task.CompletedTask;
        LoadingTask = NavigateCoreAsync(target, false, null, notifyFolderChanged: true, fromHistory: true);
        return LoadingTask;
    }
    public async Task AddTabAsync(string path, ListarySearchResult? indexedDirectoryIdentity = null)
    {
        AddTab(path);
        await NavigateAsync(path, indexedDirectoryIdentity: indexedDirectoryIdentity);
    }

    private void AddTab(string path)
    {
        SyncCurrentColumnLayout();
        _tabs.Add(new PaneTab(new TabSettings { Path = path }));
        _activeIndex = _tabs.Count - 1;
        ApplyColumnLayout();
        RenderTabs();
    }
    public void CloseCurrentTab()
    {
        if (_tabs.Count <= 1) return;
        SyncCurrentColumnLayout();
        if (ActiveTabLink is { } group) UnlinkTabRequested?.Invoke(group);
        _tabs.RemoveAt(_activeIndex);
        SelectTab(Math.Min(_activeIndex, _tabs.Count - 1), followLink: true, captureCurrent: false);
    }

    public void NextTab(int delta) => SelectTab((_activeIndex + delta + _tabs.Count) % _tabs.Count, followLink: true);

    internal void SelectTabWithoutLink(int index)
    {
        if (index != _activeIndex) SelectTab(index, followLink: false);
    }

    internal void ActivateLinkedTab(Guid group)
    {
        var index = _tabs.FindIndex(tab => tab.Settings.LinkedTabGroup == group);
        if (index >= 0 && index != _activeIndex) SelectTab(index, followLink: false);
    }

    internal void SetActiveTabLink(Guid group)
    {
        CurrentTab.Settings.LinkedTabGroup = group;
        RenderTabs();
    }

    internal void RemoveTabLink(Guid group)
    {
        foreach (var tab in _tabs.Where(tab => tab.Settings.LinkedTabGroup == group)) tab.Settings.LinkedTabGroup = null;
        RenderTabs();
    }

    private void SelectTab(int index, bool followLink, bool captureCurrent = true)
    {
        if (captureCurrent) SyncCurrentColumnLayout();
        _activeIndex = index;
        ApplyColumnLayout();
        RenderTabs();
        if (CurrentTab.DuplicateResults is { } duplicate)
        {
            if (followLink && ActiveTabLink is { } duplicateLink) LinkedTabSelected?.Invoke(this, duplicateLink);
            LoadingTask = ShowResultsCoreAsync(null, duplicate.Description, null, duplicate);
            Observe(LoadingTask);
            Navigated?.Invoke(this);
            return;
        }
        if (followLink)
        {
            if (ActiveTabLink is { } group) LinkedTabSelected?.Invoke(this, group);
            Observe(NavigateAsync(CurrentDirectory, false));
        }
        else Observe(NavigateWithoutFolderNotificationAsync(CurrentDirectory, addHistory: false));
    }
    internal void ToggleFormatLock()
    {
        if (IsSearchResults) throw new InvalidOperationException("Open a folder before changing the format lock.");
        var settings = CurrentTab.Settings;
        settings.FormatLock = settings.FormatLock is null
            ? new LockedFolderFormat(CurrentViewMode, CurrentSortColumn, CurrentSortDescending, CurrentColumnLayout.ToList(), CheckboxMode)
            : null;
        ApplyColumnLayout();
        RefreshViewMode();
        RefreshSort();
        CheckboxesChanged?.Invoke();
        UpdateFormatStatus();
    }

    internal void ResetFolderFormat(LockedFolderFormat? userDefault = null)
    {
        if (IsSearchResults) throw new InvalidOperationException("Open a folder before resetting its format.");
        var settings = CurrentTab.Settings;
        var wasLocked = IsFormatLocked;
        settings.FormatLock = null;
        ReloadFolderColumns(CurrentDirectory, force: true);
        if (userDefault is not null)
        {
            _temporaryUserDefault = true;
            _resolvedColumns = userDefault.Columns.Select(column => column.Copy()).ToList();
            _folderSort = (userDefault.SortColumn, userDefault.Descending);
            _folderViewMode = userDefault.ViewMode;
            _folderCheckboxMode = userDefault.CheckboxMode;
            ApplyColumnLayout();
        }
        if (wasLocked)
            settings.FormatLock = new LockedFolderFormat(CurrentViewMode, CurrentSortColumn, CurrentSortDescending,
                EffectiveColumns.Select(column => column.Copy()).ToList(), CheckboxMode);
        RefreshViewMode();
        RefreshSort();
        CheckboxesChanged?.Invoke();
        UpdateFormatStatus();
    }

    public void ToggleTabLock() { CurrentTab.Settings.Locked = !CurrentTab.Settings.Locked; RenderTabs(); Navigated?.Invoke(this); }

    public void ShowResults(IReadOnlyList<FileEntry> entries, string description, IReadOnlyList<ListarySearchResult>? indexed = null)
    {
        CurrentTab.DuplicateResults = null;
        LoadingTask = ShowResultsCoreAsync(entries, description, indexed, null);
        Observe(LoadingTask);
    }

    internal void ShowDuplicateResults(IReadOnlyList<FileEntry> entries, string description,
        IReadOnlyDictionary<string, DuplicateGroupHeader> headers, DuplicateResult result, bool newTab)
    {
        if (newTab) AddTab(CurrentDirectory);
        var duplicate = CurrentTab.DuplicateResults = new DuplicateTabResults(result, description, headers);
        LoadingTask = ShowResultsCoreAsync(entries, description, null, duplicate);
        Observe(LoadingTask);
        RenderTabs();
        Navigated?.Invoke(this);
    }

    private async Task ShowResultsCoreAsync(IReadOnlyList<FileEntry>? entries, string description, IReadOnlyList<ListarySearchResult>? indexed,
        DuplicateTabResults? duplicate)
    {
        _filterCancellation?.Cancel();
        ++_filterGeneration;
        _readCancellation?.Cancel();
        _readCancellation?.Dispose();
        var cancellation = _readCancellation = new CancellationTokenSource();
        var generation = ++_readGeneration;
        IsLoading = true;
        _listHost.IsEnabled = false;
        _filter.IsEnabled = false;
        _viewMode.IsEnabled = false;
        _activeDuplicateResult = null;
        try
        {
            if (entries is null && duplicate is not null)
            {
                try
                {
                    var groups = await new DuplicateFinder().ReadReviewedGroupsAsync(duplicate.Result, verifyContents: false, cancellation.Token);
                    entries = groups.SelectMany(group => group).Where(entry => duplicate.Headers.ContainsKey(entry.FullPath)).ToArray();
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    description = $"Duplicate results need a new scan. {error.Message}";
                    entries = [];
                    duplicate = null;
                }
                if (generation != _readGeneration || _disposed) return;
            }
            IsSearchResults = true;
            _duplicateGroups = duplicate?.Headers;
            _activeDuplicateResult = duplicate?.Result;
            UpdateFormatStatus();
            ApplyColumnLayout();
            RefreshViewMode();
            _all = entries ?? [];
            _indexed = indexed?.GroupBy(x => x.Entry.FullPath, PathComparer).ToDictionary(x => x.Key, x => x.First(), PathComparer) ?? new(PathComparer);
            UpdateSortHeaders();
            _listingNotice = description;
            _applying = true;
            _filter.Text = "";
            _applying = false;
            await FilterAndSortAsync();
        }
        catch (OperationCanceledException) { }
        finally
        {
            if (generation == _readGeneration && !_disposed)
            {
                IsLoading = false;
                _listHost.IsEnabled = true;
                _filter.IsEnabled = true;
                _viewMode.IsEnabled = true;
                OnSelectionChanged();
            }
        }
    }

    public void SelectPaths(IEnumerable<string> paths)
    {
        var selected = paths.ToHashSet(PathComparer);
        var entries = _visible.Where(entry => selected.Contains(entry.FullPath)).ToArray();
        var desired = entries.ToHashSet();
        var wasApplying = _applying;
        _applying = true;
        try
        {
            // The inactive lister has no ItemsSource. Its selected-items
            // collection must not receive rows until that view becomes active;
            // ApplyViewMode restores the same paths after rebinding it.
            if (_tiles.IsVisible) SynchronizeSelection(_tiles.SelectedItems, entries, desired);
            else SynchronizeSelection(_files.SelectedItems, entries, desired);
        }
        finally { _applying = wasApplying; }
        OnSelectionChanged();
    }
    private static void SynchronizeSelection(System.Collections.IList? selection, IReadOnlyList<FileEntry> entries, HashSet<FileEntry> desired)
    {
        if (selection is null) return;
        // Do not clear and re-add an unchanged current row. DataGrid can retain its
        // CurrentItem while clearing SelectedItems, making a same-item re-add vanish.
        for (var index = selection.Count - 1; index >= 0; index--)
            if (selection[index] is not FileEntry entry || !desired.Contains(entry)) selection.RemoveAt(index);
        var present = selection.OfType<FileEntry>().ToHashSet();
        foreach (var entry in entries)
            if (present.Add(entry)) selection.Add(entry);
    }
    public void SelectAll()
    {
        if (CheckboxMode) { SelectActionPaths(_visible.Select(entry => entry.FullPath)); return; }
        if (_tiles.IsVisible) _tiles.SelectAll();
        else _files.SelectAll();
        OnSelectionChanged();
    }
    public void InvertSelection()
    {
        var selected = ActionEntries.Select(x => x.FullPath).ToHashSet(PathComparer);
        SelectActionPaths(_visible.Where(x => !selected.Contains(x.FullPath)).Select(x => x.FullPath));
    }
    private void SortBy(string key)
    {
        ApplySort(key, CurrentSortColumn == key && !CurrentSortDescending);
    }

    internal void ApplySort(string key, bool descending)
    {
        if (ActiveFormatLock is { } format)
            CurrentTab.Settings.FormatLock = format with { SortColumn = key, Descending = descending };
        else if (!IsSearchResults && _folderSort is not null) _folderSort = (key, descending);
        else
        {
            CurrentTab.Settings.SortColumn = key;
            CurrentTab.Settings.Descending = descending;
        }
        RefreshSort();
    }

    private async Task CalculateAutomaticFolderSizesAsync(DirectoryListing listing, FolderSizeMode mode, long generation,
        CancellationToken token, bool refreshMeasuredSizes)
    {
        var links = _folderSizeLinks();
        var folders = listing.Entries.Where(entry => entry.IsDirectory &&
            (entry.CalculatedFolderLength.HasValue ? refreshMeasuredSizes :
                mode != FolderSizeMode.Off && (!entry.IsSymbolicLink || links.SizeLinkedFolders))).ToArray();
        if (folders.Length == 0) return;
        var scanned = await Task.Run<(Dictionary<string, long> Sizes, List<string> Errors)?>(() =>
        {
            if (mode != FolderSizeMode.All)
            {
                if (listing.Directory.StartsWith(@"\\", StringComparison.Ordinal))
                    return null;
                var type = new DriveInfo(listing.Directory).DriveType;
                if (type is not (DriveType.Fixed or DriveType.Removable))
                    return null;
            }
            return DirectoryBrowser.CalculateFolderSizes(folders, token, followNestedLinks: links.FollowNested);
        }, token);
        if (scanned is not { } result || _disposed || token.IsCancellationRequested || generation != _readGeneration || IsSearchResults ||
            _folderSizeMode(listing.Directory) != mode || _folderSizeLinks() != links ||
            (refreshMeasuredSizes && !_updateFolderSizesAfterOperations())) return;
        if (refreshMeasuredSizes && result.Errors.Count > 0)
        {
            var stale = folders.Where(entry => !result.Sizes.ContainsKey(entry.FullPath))
                .Select(entry => entry.FullPath).ToHashSet(PathComparer);
            _all = _all.Select(entry => stale.Contains(entry.FullPath) ? entry with { CalculatedFolderLength = null } : entry).ToArray();
        }
        if (result.Sizes.Count > 0 || refreshMeasuredSizes) await ShowCalculatedFolderSizesAsync(result.Sizes);
        if (result.Errors.Count > 0 && generation == _readGeneration && !token.IsCancellationRequested)
        {
            _listingNotice += (_listingNotice.Length == 0 ? "" : " · ") +
                $"{result.Errors.Count:N0} folder sizes unavailable";
            UpdateStatus();
        }
    }

    private static FileEntry[] ApplyFolderSizes(IReadOnlyList<FileEntry> entries, IReadOnlyDictionary<string, long> sizes) =>
        entries.Select(entry => entry.IsDirectory && sizes.TryGetValue(entry.FullPath, out var length)
            ? entry with { CalculatedFolderLength = length } : entry).ToArray();

    internal async Task ShowCalculatedFolderSizesAsync(IReadOnlyDictionary<string, long> sizes,
        IReadOnlySet<string>? attempted = null)
    {
        _all = ApplyFolderSizes(_all, sizes);
        if (attempted is not null)
            _all = _all.Select(entry => attempted.Contains(entry.FullPath) && !sizes.ContainsKey(entry.FullPath)
                ? entry with { CalculatedFolderLength = null } : entry).ToArray();
        if (!IsSearchResults && _cacheFolderSizesInHistory())
        {
            var calculated = _all.Where(entry => entry.CalculatedFolderLength.HasValue).Take(1025).ToArray();
            var cache = CurrentTab.CachedFolderSizes;
            cache.Remove(CurrentDirectory);
            if (calculated.Length <= 1024)
            {
                if (cache.Count == 8) cache.Remove(cache.Keys.First());
                cache[CurrentDirectory] = calculated.ToDictionary(entry => entry.FullPath,
                    entry => entry.CalculatedFolderLength!.Value, PathComparer);
            }
        }
        await FilterAndSortAsync();
    }

    internal void ClearFolderSizeHistory()
    {
        foreach (var tab in _tabs) tab.CachedFolderSizes.Clear();
    }

    internal void RefreshSort()
    {
        UpdateSortHeaders();
        QueueFilterAndSort();
        Navigated?.Invoke(this);
    }
    private void QueueFilterAndSort(bool debounce = false) => Observe(FilterAndSortAsync(debounce));
    private Task FilterAndSortAsync(bool debounce = false)
    {
        FilteringTask = FilterAndSortCoreAsync(debounce);
        return FilteringTask;
    }
    private async Task FilterAndSortCoreAsync(bool debounce)
    {
        _filterCancellation?.Cancel();
        _filterCancellation?.Dispose();
        var token = (_filterCancellation = new CancellationTokenSource()).Token;
        var generation = ++_filterGeneration;
        var entries = _all;
        var filter = FilterText;
        _lastScheduledFilter = filter;
        var column = CurrentSortColumn;
        var descending = CurrentSortDescending;
        var preserveIndexedOrder = IsSearchResults && _indexed.Count > 0;
        var duplicateGroups = _duplicateGroups;
        var acquired = false;
        try
        {
            if (debounce) await Task.Delay(120, token);
            await _filterGate.WaitAsync(token);
            acquired = true;
            // DirectoryBrowser.ReadAsync already returns folders-first natural
            // Name order. On an unfiltered folder using that same sort, reusing
            // the listing avoids another O(n log n) sort and two allocations
            // on every navigation/refresh. Search, grouped results and custom
            // sorts still take the existing filtering path.
            var reuseListing = !IsSearchResults && string.IsNullOrWhiteSpace(filter) &&
                string.Equals(column, "Name", StringComparison.OrdinalIgnoreCase) && !descending && duplicateGroups is null;
            IReadOnlyList<FileEntry> visible;
            if (reuseListing)
                visible = entries;
            else
                visible = await Task.Run(() =>
                {
                    token.ThrowIfCancellationRequested();
                    var filtered = DirectoryBrowser.FilterEntries(entries, filter);
                    token.ThrowIfCancellationRequested();
                    var sorted = preserveIndexedOrder
                        ? filtered
                        : DirectoryBrowser.SortEntries(filtered, column, descending);
                    token.ThrowIfCancellationRequested();
                    return duplicateGroups is null ? sorted : sorted.OrderBy(entry => duplicateGroups[entry.FullPath].Number).ToArray();
                }, token);
            token.ThrowIfCancellationRequested();
            if (generation != _filterGeneration || _disposed) return;
            var selection = SelectedEntries.Select(x => x.FullPath).ToArray();
            _visible = visible;
            // Cells are recycled by the controls; do not retain every path ever visited.
            _nameCells.Clear();
            _applying = true;
            BindActiveViewItems(duplicateGroups);
            _applying = false;
            SelectPaths(selection);
            UpdateStatus();
        }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            if (generation == _filterGeneration && !_disposed) _status.Text = $"Filter: {error.Message}";
        }
        finally
        {
            _applying = false;
            if (acquired) _filterGate.Release();
        }
    }

    private void BindActiveViewItems(IReadOnlyDictionary<string, DuplicateGroupHeader>? groups)
    {
        // Only the displayed list needs the live snapshot. Keeping both lists
        // bound makes a hidden view retain thousands of stale file rows and
        // repeat item-source work on every filter/navigation.
        if (_tiles.IsVisible)
        {
            _files.ItemsSource = null;
            _tiles.ItemsSource = _visible;
            return;
        }
        _tiles.ItemsSource = null;
        if (groups is null) _files.ItemsSource = _visible;
        else
        {
            var grouped = new DataGridCollectionView(_visible);
            grouped.GroupDescriptions.Add(new DuplicateGroupDescription(groups));
            _files.ItemsSource = grouped;
        }
    }

    internal sealed record DuplicateGroupHeader(int Number, string Label)
    {
        public override string ToString() => Label;
    }

    private sealed class DuplicateGroupDescription(IReadOnlyDictionary<string, DuplicateGroupHeader> groups) : DataGridGroupDescription
    {
        public override string PropertyName => "Duplicate group";

        public override object GroupKeyFromItem(object item, int level, System.Globalization.CultureInfo culture) =>
            groups[((FileEntry)item).FullPath];
    }
    internal void SetViewMode(string mode)
    {
        if (ActiveFormatLock is { } format) CurrentTab.Settings.FormatLock = format with { ViewMode = mode };
        else if (!IsSearchResults && _folderViewMode is not null) _folderViewMode = mode;
        else CurrentTab.Settings.ViewMode = mode;
        RefreshViewMode();
        Navigated?.Invoke(this);
    }

    internal void RefreshViewMode()
    {
        _applying = true;
        _viewMode.SelectedItem = CurrentViewMode;
        _applying = false;
        ApplyViewMode();
    }

    private void ApplyViewMode()
    {
        var selected = SelectedEntries.Select(entry => entry.FullPath).ToArray();
        var filmstrip = CurrentViewMode == "Filmstrip";
        var tiles = filmstrip || CurrentViewMode == "Tiles";
        var wasTiles = _tiles.IsVisible;
        if (filmstrip != _filmstrip)
        {
            _filmstrip = filmstrip;
            _tiles.ItemsPanel = new FuncTemplate<Panel?>(() => new VirtualizingStackPanel
                { Orientation = filmstrip ? Orientation.Horizontal : Orientation.Vertical });
            _tiles.ItemTemplate = new FuncDataTemplate<FileEntry>((entry, _) => CreateFileTile(entry, filmstrip));
            _tiles.Height = filmstrip ? 124 : double.NaN;
            ScrollViewer.SetHorizontalScrollBarVisibility(_tiles, filmstrip ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled);
            ScrollViewer.SetVerticalScrollBarVisibility(_tiles, filmstrip ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Auto);
        }
        _files.IsVisible = !tiles;
        _tiles.IsVisible = tiles;
        if (wasTiles != tiles)
        {
            var wasApplying = _applying;
            _applying = true;
            try { BindActiveViewItems(_duplicateGroups); }
            finally { _applying = wasApplying; }
        }
        SelectPaths(selected);
    }

    private Control CreateFileTile(FileEntry? entry, bool filmstrip)
    {
        if (entry is null) return new TextBlock();
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        row.Children.Add(CreateFileCheckbox(entry));
        var tile = new FileTile(entry, allowImage: !IsSearchResults, filmstrip: filmstrip);
        Grid.SetColumn(tile, 1);
        row.Children.Add(tile);
        return row;
    }
    internal void SetDetailsThumbnails(bool visible)
    {
        var selected = SelectedEntries.Select(entry => entry.FullPath).ToArray();
        var columns = CurrentColumnLayout.ToList();
        var thumbnail = columns.FirstOrDefault(column => column.Id == "Thumbnail")
            ?? FileColumnSettings.CreateDefaultLayout().Single(column => column.Id == "Thumbnail");
        if (visible && !thumbnail.Visible)
        {
            columns.Remove(thumbnail);
            columns.Insert(0, thumbnail);
        }
        thumbnail.Visible = visible;
        ApplyCurrentColumnLayout(columns);
        SetViewMode("Details");
        SelectPaths(selected);
    }

    internal void ApplyCurrentColumnLayout(IEnumerable<FileColumnSettings> columns)
    {
        ArgumentNullException.ThrowIfNull(columns);
        var next = columns.Where(column => column is not null).Take(FileColumnSettings.CreateDefaultLayout().Count).Select(column => column.Copy()).ToList();
        var name = next.FirstOrDefault(column => string.Equals(column.Id, "Name", StringComparison.OrdinalIgnoreCase));
        if (name is null)
        {
            name = FileColumnSettings.CreateDefaultLayout().First();
            next.Insert(0, name);
        }
        name.Id = "Name";
        name.Visible = true;
        EffectiveColumns = next;
        ApplyColumnLayout();
        Navigated?.Invoke(this);
    }
    private void ApplyColumnLayout()
    {
        _applyingColumns = true;
        try
        {
            _nameCells.Clear();
            _headers.Clear();
            _columnIds.Clear();
            // Keep the DataGrid's internal group spacer when replacing user columns.
            for (var index = _files.Columns.Count - 1; index >= 0; index--)
                _files.Columns.RemoveAt(index);
            foreach (var settings in EffectiveColumns.Where(column => column.Visible))
                _files.Columns.Add(CreateDetailsColumn(settings));
            if (_columnIds.Values.Contains("Thumbnail")) _files.RowHeight = 96;
            else _files.ClearValue(DataGrid.RowHeightProperty);
            if (_files.Columns.Count == 0)
            {
                var name = FileColumnSettings.CreateDefaultLayout().First();
                EffectiveColumns.Insert(0, name);
                _files.Columns.Add(CreateDetailsColumn(name));
            }
            UpdateSortHeaders();
        }
        finally { _applyingColumns = false; }
        ApplyCompactDetailsColumns();
    }
    private void ApplyCompactDetailsColumns()
    {
        if (_applyingColumns || _disposed) return;
        var width = _listHost.Bounds.Width;
        if (!double.IsFinite(width) || width <= 0) return;
        // A narrow side-by-side display cannot fit the full Details columns.
        // Retain Name and the user's first other visible column if it fits;
        // Size might be deliberately hidden or ordered after another column.
        // This never changes the saved columns or folder-format rules.
        var compact = width < 420;
        var preferred = _files.Columns.FirstOrDefault(column =>
            _columnIds.TryGetValue(column, out var id) && id != "Name");
        var showPreferred = preferred is not null &&
            width >= MinimumColumnWidth("Name") + preferred.MinWidth + 10;
        _applyingColumns = true;
        try
        {
            foreach (var column in _files.Columns)
            {
                if (!_columnIds.TryGetValue(column, out var id)) continue;
                var show = !compact || id == "Name" || (showPreferred && ReferenceEquals(column, preferred));
                if (column.IsVisible != show) column.IsVisible = show;
            }
        }
        finally { _applyingColumns = false; }
        AutomationProperties.SetHelpText(_files, compact
            ? "Narrow file display: widen this pane to see the other saved Details columns."
            : "File Details columns follow the saved folder format.");
    }
    private DataGridColumn CreateDetailsColumn(FileColumnSettings settings)
    {
        DataGridColumn column = settings.Id switch
        {
            "Name" => new DataGridTemplateColumn
            {
                CellTemplate = new FuncDataTemplate<FileEntry>((entry, _) =>
                {
                    if (entry is null) return new TextBlock();
                    var title = Ui.Text(entry.Name);
                    title.TextTrimming = TextTrimming.CharacterEllipsis;
                    var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,19,*"), Margin = new Thickness(3, 0) };
                    row.Children.Add(CreateFileCheckbox(entry));
                    var icon = Ui.Icon(entry.IsSymbolicLink ? "link" : entry.IsDirectory ? "folder" : "file",
                        entry.IsDirectory ? new SolidColorBrush(Color.Parse("#D8AD55")) : Brushes.SlateGray, 13);
                    Grid.SetColumn(icon, 1); row.Children.Add(icon);
                    Grid.SetColumn(title, 2); row.Children.Add(title);
                    var cell = new ContentControl { Content = row, Tag = entry.FullPath, HorizontalContentAlignment = HorizontalAlignment.Stretch };
                    AttachFileNameToolTip(cell, entry);
                    _nameCells[entry.FullPath] = new WeakReference<ContentControl>(cell);
                    return cell;
                })
            },
            "Size" => Ui.ReadableColumn<FileEntry>("Size", entry => entry.SizeText),
            "Modified" => Ui.ReadableColumn<FileEntry>("Modified", entry => FormatFileDate(entry.ModifiedUtc), horizontalInset: 3, alignRight: true),
            "Created" => Ui.ReadableColumn<FileEntry>("Created", entry => FormatFileDate(entry.CreatedUtc), horizontalInset: 3, alignRight: true),
            "Type" => Ui.ReadableColumn<FileEntry>("Type", entry => entry.Kind),
            "Attr" => Ui.ReadableColumn<FileEntry>("Attr", entry => FormatAttributes(entry.Attributes)),
            "Path" => Ui.ReadableColumn<FileEntry>("Path", entry => entry.FullPath),
            "Thumbnail" => new DataGridTemplateColumn
            {
                CanUserSort = false,
                CellTemplate = new FuncDataTemplate<FileEntry>((entry, _) => entry is null ? new TextBlock() :
                    new FileThumbnail(entry, allowImage: !IsSearchResults) { Height = 90, Margin = new Thickness(3) })
            },
            _ => throw new InvalidOperationException($"Unsupported file column: {settings.Id}")
        };
        column.MinWidth = MinimumColumnWidth(settings.Id);
        column.Width = settings.WidthMode == "Star"
            ? new DataGridLength(settings.Width, DataGridLengthUnitType.Star)
            : new DataGridLength(settings.Width);
        var sortKey = settings.Id;
        _columnIds[column] = sortKey;
        if (sortKey == "Thumbnail")
        {
            column.Header = "Thumbnail";
            return column;
        }
        var button = Ui.Button(ColumnLabel(sortKey), () => SortBy(sortKey));
        button.Background = Brushes.Transparent;
        button.BorderThickness = new Thickness(0);
        button.HorizontalAlignment = HorizontalAlignment.Stretch;
        button.HorizontalContentAlignment = HorizontalAlignment.Left;
        button.Padding = new Thickness(4, 0);
        _headers[sortKey] = button;
        column.Header = button;
        return column;
    }
    private void AttachFileNameToolTip(ContentControl cell, FileEntry entry)
    {
        if (!entry.IsDirectory || entry.IsSymbolicLink || IsSearchResults)
        {
            ToolTip.SetTip(cell, entry.FullPath);
            return;
        }
        var size = Ui.Text("", true);
        var content = new StackPanel { Spacing = 3, Children = { Ui.Text(entry.FullPath), size } };
        ToolTip.SetTip(cell, content);
        CancellationTokenSource? pending = null;
        void Stop()
        {
            pending?.Cancel();
            pending?.Dispose();
            pending = null;
        }
        cell.DetachedFromVisualTree += (_, _) => Stop();
        cell.PropertyChanged += async (_, change) =>
        {
            if (change.Property != ToolTip.IsOpenProperty) return;
            Stop();
            if (!ToolTip.GetIsOpen(cell)) return;
            if (entry.CalculatedFolderLength is { } known)
            {
                size.Text = $"Size: {FileEntry.FormatSize(known)}";
                return;
            }
            var scan = pending = CancellationTokenSource.CreateLinkedTokenSource(
                _readCancellation?.Token ?? CancellationToken.None);
            var token = scan.Token;
            size.Text = "Calculating folder size…";
            try
            {
                var total = await Task.Run(() => DirectoryBrowser.CalculateFolderSize(entry.FullPath, token,
                    followNestedLinks: _folderSizeLinks().FollowNested), token);
                if (!token.IsCancellationRequested && ReferenceEquals(scan, pending) && ToolTip.GetIsOpen(cell))
                    size.Text = $"Size: {FileEntry.FormatSize(total)}";
            }
            catch (OperationCanceledException) { }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or OverflowException)
            {
                if (!token.IsCancellationRequested && ReferenceEquals(scan, pending) && ToolTip.GetIsOpen(cell))
                    size.Text = $"Size unavailable: {error.Message}";
            }
        };
    }

    private void UpdateSortHeaders()
    {
        // Indexed results arrive in the canonical provider's occurrence order.
        // Do not imply that a local folder column controls that ranking.
        var folderSort = !IsSearchResults || _indexed.Count == 0;
        var sortColumn = CurrentSortColumn;
        var descending = CurrentSortDescending;
        foreach (var (key, button) in _headers)
        {
            var selected = folderSort && string.Equals(key, sortColumn, StringComparison.OrdinalIgnoreCase);
            button.Classes.Set("sorted-column", selected);
            button.Background = selected ? new SolidColorBrush(Color.Parse("#613515")) : Brushes.Transparent;
            if (selected) button.Foreground = new SolidColorBrush(Color.Parse("#FFD097"));
            else button.ClearValue(Button.ForegroundProperty);
            var label = ColumnLabel(key);
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 3 };
            row.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis });
            if (selected)
            {
                var marker = new Avalonia.Controls.Shapes.Path
                {
                    Data = Geometry.Parse(descending ? "M 0,0 L 8,0 L 4,5 Z" : "M 0,5 L 8,5 L 4,0 Z"),
                    Fill = new SolidColorBrush(Color.Parse("#FFD097")),
                    Width = 8, Height = 6, Stretch = Stretch.Uniform,
                    VerticalAlignment = VerticalAlignment.Center
                };
                Grid.SetColumn(marker, 1);
                row.Children.Add(marker);
            }
            button.Content = row;
            AutomationProperties.SetName(button, selected
                ? $"{label}, sorted {(descending ? "descending" : "ascending")}" : $"Sort by {label}");
            ToolTip.SetTip(button, selected
                ? $"Sorted by {label} {(descending ? "descending" : "ascending")}; click to reverse"
                : $"Sort by {label}");
        }
    }
    internal static string FormatFileDate(DateTimeOffset utc)
    {
        var local = utc.ToLocalTime();
        var today = DateTime.Today;
        var label = local.Date == today ? "Today" : local.Date == today.AddDays(-1) ? "Yesterday" : null;
        return label is null ? local.ToString("yyyy-MM-dd h:mm tt") : $"{label} {local:h:mm tt}";
    }

    internal static string FormatAttributes(FileAttributes attributes)
    {
        Span<char> flags = stackalloc char[8];
        var count = 0;
        if ((attributes & FileAttributes.ReadOnly) != 0) flags[count++] = 'R';
        if ((attributes & FileAttributes.Archive) != 0) flags[count++] = 'A';
        if ((attributes & FileAttributes.System) != 0) flags[count++] = 'S';
        if ((attributes & FileAttributes.Hidden) != 0) flags[count++] = 'H';
        if ((attributes & FileAttributes.Compressed) != 0) flags[count++] = 'C';
        if ((attributes & FileAttributes.Encrypted) != 0) flags[count++] = 'E';
        if ((attributes & FileAttributes.ReparsePoint) != 0) flags[count++] = 'L';
        if ((attributes & FileAttributes.Directory) != 0) flags[count++] = 'D';
        return count == 0 ? "" : new string(flags[..count]);
    }
    private void OnLiveColumnLayoutChanged()
    {
        if (_applyingColumns || _disposed) return;
        if (SyncCurrentColumnLayout()) Navigated?.Invoke(this);
    }
    private bool SyncCurrentColumnLayout()
    {
        if (_applyingColumns || _files.Columns.Count == 0) return false;
        var previous = EffectiveColumns.Select(column => column.Copy()).ToList();
        var liveColumns = _files.Columns.OrderBy(column => column.DisplayIndex)
            .Where(column => _columnIds.ContainsKey(column)).ToArray();
        var liveIds = liveColumns.Select(column => _columnIds[column]).ToArray();
        var liveById = liveColumns.ToDictionary(column => _columnIds[column], StringComparer.OrdinalIgnoreCase);
        var previousById = previous.ToDictionary(column => column.Id, StringComparer.OrdinalIgnoreCase);
        var updated = new List<FileColumnSettings>(previous.Count);
        var liveIndex = 0;
        foreach (var source in previous)
        {
            if (!source.Visible || liveIndex >= liveIds.Length)
            {
                updated.Add(source.Copy());
                continue;
            }
            var id = liveIds[liveIndex++];
            if (!previousById.TryGetValue(id, out var stored) || !liveById.TryGetValue(id, out var live))
            {
                updated.Add(source.Copy());
                continue;
            }
            var widthMode = live.Width.IsStar ? "Star" : "Pixel";
            var width = live.Width.Value;
            var captured = stored.Copy();
            captured.WidthMode = widthMode;
            captured.Width = double.IsFinite(width) ? Math.Clamp(width, widthMode == "Star" ? 0.25 : MinimumColumnWidth(id), widthMode == "Star" ? 8 : 1200) : stored.Width;
            updated.Add(captured);
        }
        if (ColumnLayoutsEqual(previous, updated)) return false;
        EffectiveColumns = updated;
        return true;
    }
    private static bool ColumnLayoutsEqual(IReadOnlyList<FileColumnSettings> left, IReadOnlyList<FileColumnSettings> right) =>
        left.Count == right.Count && left.Zip(right).All(pair =>
            pair.First.Id == pair.Second.Id && pair.First.Visible == pair.Second.Visible &&
            pair.First.WidthMode == pair.Second.WidthMode && Math.Abs(pair.First.Width - pair.Second.Width) < 0.5);
    private static string ColumnLabel(string id) => id switch
    {
        "Modified" => "Modified",
        "Created" => "Created",
        _ => id
    };
    private static double MinimumColumnWidth(string id) => id switch
    {
        "Name" => 180,
        "Modified" or "Created" => 110,
        "Path" => 140,
        _ => 60
    };
    private void OnSelectionChanged() { if (_applying || IsLoading || _disposed) return; UpdateStatus(); SelectionChanged?.Invoke(this); }
    private void UpdateStatus()
    {
        var selection = ActionEntries;
        var hidden = _all.Count - _visible.Count;
        var fileCount = _visible.Count(entry => !entry.IsDirectory);
        var folderCount = _visible.Count - fileCount;
        var listedBytes = _visible.Where(entry => !entry.IsDirectory).Sum(entry => entry.Length);
        var selectedBytes = selection.Where(entry => !entry.IsDirectory).Sum(entry => entry.Length);
        var types = FileTypeSummary.Summarize(selection);
        if (!_selectedFileTypes.SequenceEqual(types))
        {
            _selectedFileTypes = types;
            _fileTypeStatus.IsVisible = types.Length > 0;
            _fileTypeStatus.Content = types.Length == 0 ? null : FileTypeSummary.StatusBadges(types, _statusBadgeLimit);
            AutomationProperties.SetName(_fileTypeStatus, "File Type Summary " + FileTypeSummary.Compact(types));
            ToolTip.SetTip(_fileTypeStatus, types.Length == 0 ? null : FileTypeSummary.Chart(types, bySize: false, compact: true));
        }
        var capacity = TryGetCapacity(CurrentDirectory);
        _freeSpaceStatus.IsVisible = _diskCapacity.IsVisible = capacity is not null;
        if (capacity is { } disk)
        {
            _freeSpaceStatus.Text = $"{FileEntry.FormatSize(disk.Available)} free";
            _diskCapacity.Value = 100 * (1 - (double)disk.Available / disk.Total);
            var label = $"{_freeSpaceStatus.Text} of {FileEntry.FormatSize(disk.Total)}";
            AutomationProperties.SetName(_diskCapacity, label);
            ToolTip.SetTip(_diskCapacity, label);
        }
        _status.Text = $"{fileCount:N0} files, {folderCount:N0} folders · {FileEntry.FormatSize(listedBytes)}"
            + (hidden > 0 ? $" · {hidden:N0} filtered" : "")
            + $" · {selection.Count:N0} {(CheckboxMode ? "checked" : "selected")}"
            + (selection.Count > 0 ? $" · {FileEntry.FormatSize(selectedBytes)}" : "")
            + (_listingNotice.Length > 0 ? " · " + _listingNotice : "");
        ToolTip.SetTip(_status, _status.Text);
    }
    private static (long Available, long Total)? TryGetCapacity(string path)
    {
        try
        {
            if (!Path.IsPathRooted(path) || path.StartsWith("\\\\", StringComparison.Ordinal)) return null;
            // On Unix, querying the folder path identifies its mounted filesystem.
            // Reducing it to "/" would report the startup disk for every volume.
            var drive = new DriveInfo(path);
            if (!drive.IsReady) return null;
            var total = drive.TotalSize;
            if (total <= 0) return null;
            return (Math.Clamp(drive.AvailableFreeSpace, 0, total), total);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }
    private void RenderTabs()
    {
        UpdateFormatStatus();
        _tabButtons.Children.Clear();
        for (var index = 0; index < _tabs.Count; index++)
        {
            var tabIndex = index;
            var settings = _tabs[index].Settings;
            var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(settings.Path));
            if (string.IsNullOrEmpty(name)) name = settings.Path;
            if (_tabs[index].DuplicateResults is not null) name = "Duplicate Files";
            var automationName = (settings.Locked ? "Locked · " : "") + name;
            var tab = Ui.Button(automationName, () =>
            {
                if (tabIndex != _activeIndex) SelectTab(tabIndex, followLink: true);
            }, settings.Path);
            tab.Name = "FolderTab";
            tab.Tag = settings.Path;
            tab.AddHandler(PointerPressedEvent, (_, e) =>
            {
                if (!e.GetCurrentPoint(tab).Properties.IsLeftButtonPressed) return;
                if (e.KeyModifiers == KeyModifiers.Control && ToggleTabLinkRequested?.Invoke(this, tabIndex) == true)
                    e.Handled = true;
                else if (settings.LinkedTabGroup.HasValue && e.KeyModifiers == (KeyModifiers.Control | KeyModifiers.Shift) &&
                    ToggleLinkedNavigationRequested?.Invoke(this, tabIndex) == true)
                    e.Handled = true;
                else if (e.KeyModifiers == KeyModifiers.Shift && settings.LinkedTabGroup.HasValue)
                {
                    SelectTabWithoutLink(tabIndex);
                    e.Handled = true;
                }
            }, RoutingStrategies.Tunnel);
            var tabText = Ui.Text(name);
            tabText.TextTrimming = TextTrimming.CharacterEllipsis;
            tab.Content = Ui.Row(Ui.Icon(settings.LinkedTabGroup.HasValue ? "link" : "folder",
                new SolidColorBrush(Color.Parse("#D8AD55")), 12), tabText);
            var linkAction = new MenuItem
            {
                Header = settings.LinkedTabGroup.HasValue ? "Unlink Tab" : "Link to Active Tab in Other Pane"
            };
            linkAction.Click += (_, _) =>
            {
                if (settings.LinkedTabGroup is { } group) UnlinkTabRequested?.Invoke(group);
                else ToggleTabLinkRequested?.Invoke(this, tabIndex);
            };
            var context = new ContextMenu { Items = { linkAction } };
            if (settings.LinkedTabGroup is { } linked)
            {
                var navLock = new MenuItem { Header = "Navigation Lock", ToggleType = MenuItemToggleType.CheckBox };
                navLock.Click += (_, _) => ToggleLinkedNavigationRequested?.Invoke(this, tabIndex);
                context.Opened += (_, _) =>
                {
                    navLock.IsEnabled = CanLinkTabs;
                    navLock.IsChecked = IsLinkedNavigationLocked?.Invoke(linked) == true;
                };
                context.Items.Add(navLock);
            }
            context.Opened += (_, _) => linkAction.IsEnabled = settings.LinkedTabGroup.HasValue || CanLinkTabs;
            tab.ContextMenu = context;
            tab.MaxWidth = 180;
            tab.MinHeight = 21;
            tab.FontWeight = index == _activeIndex ? FontWeight.SemiBold : FontWeight.Normal;
            if (index == _activeIndex) tab.BorderBrush = new SolidColorBrush(Color.Parse("#B56A21"));
            _tabButtons.Children.Add(tab);
        }
        _closeTab.IsEnabled = _tabs.Count > 1;
        _back.IsEnabled = CurrentTab.CanBack;
        _forward.IsEnabled = CurrentTab.CanForward;
    }
    private void UpdateFormatStatus()
    {
        var locked = !IsSearchResults && IsFormatLocked;
        var label = IsSearchResults ? "Folder format unavailable in search results" :
            locked ? "Folder format locked" : "Folder format follows navigation";
        _formatStatus.Content = Ui.Icon(locked ? "format-lock" : "format-info",
            IsSearchResults ? null : new SolidColorBrush(Color.Parse(locked ? "#F2B76B" : "#6EACD9")), size: 12);
        _formatStatus.IsEnabled = !IsSearchResults;
        _formatLockAction.Header = locked ? "Unlock Folder Format" : "Lock Folder Format";
        AutomationProperties.SetName(_formatStatus, label);
        ToolTip.SetTip(_formatStatus, label);
    }
    private void SetWatcher(string path)
    {
        var watchGeneration = ++_watchGeneration;
        _watcher?.Dispose();
        _watcher = null;
        try
        {
            _watcher = new FileSystemWatcher(path) { NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.Size | NotifyFilters.LastWrite };
            // One pending UI callback per watcher generation is sufficient:
            // the existing debounce refreshes the full directory. Keep this
            // gate local to the watcher so retiring a folder cannot suppress
            // notifications from the next folder's watcher.
            var notificationQueued = 0;
            FileSystemEventHandler changed = (_, _) =>
            {
                if (Interlocked.Exchange(ref notificationQueued, 1) != 0) return;
                Interlocked.Increment(ref _watchUiDispatches);
                Dispatcher.UIThread.Post(() =>
                {
                    Interlocked.Exchange(ref notificationQueued, 0);
                    if (_disposed || IsSearchResults || watchGeneration != _watchGeneration) return;
                    if (IsLoading) _refreshPending = true;
                    else { _watchDebounce.Stop(); _watchDebounce.Start(); }
                });
            };
            _watcher.Created += changed; _watcher.Deleted += changed; _watcher.Changed += changed;
            _watcher.Renamed += (_, _) => changed(this, new FileSystemEventArgs(WatcherChangeTypes.Renamed, path, ""));
            _watcher.Error += (_, _) => Dispatcher.UIThread.Post(() =>
            {
                if (_disposed || watchGeneration != _watchGeneration) return;
                _listingNotice = "Folder monitoring stopped. Press F5 to refresh.";
                UpdateStatus();
            });
            _watcher.EnableRaisingEvents = true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
        { _listingNotice += (_listingNotice.Length > 0 ? " · " : "") + "Automatic refresh unavailable"; UpdateStatus(); }
    }
    private async Task OpenAsync(FileEntry entry)
    {
        Activate();
        if (OpenRequested is null) return;
        try { await OpenRequested(this, entry); }
        catch (Exception error) { Error?.Invoke(error); }
    }
    private void Observe(Task task)
    {
        _ = ObserveCoreAsync(task);
    }
    private async Task ObserveCoreAsync(Task task)
    {
        try { await task; }
        catch (OperationCanceledException) { }
        catch (Exception error) { Error?.Invoke(error); }
    }
    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        Activate();
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        { _dragPress = e; _dragStart = e.GetPosition(this); }
    }
    private async void OnPointerMoved(object? sender, PointerEventArgs e)
    {
        var distance = e.GetPosition(this) - _dragStart;
        if (_dragging || _dragPress is null || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed ||
            distance.X * distance.X + distance.Y * distance.Y < 49 || SelectedEntries.Count == 0) return;
        var press = _dragPress;
        _dragPress = null;
        _dragging = true;
        try
        {
            var top = TopLevel.GetTopLevel(this);
            if (top is null) return;
            using var transfer = new DataTransfer();
            var entries = ValidateSelectionRequested is not null ? await ValidateSelectionRequested(this) : SelectedEntries;
            if (entries.Count == 0) return;
            foreach (var entry in entries)
            {
                IStorageItem? item = entry.IsDirectory
                    ? await top.StorageProvider.TryGetFolderFromPathAsync(entry.FullPath)
                    : await top.StorageProvider.TryGetFileFromPathAsync(entry.FullPath);
                if (item is null)
                    throw new IOException($"The selected item could not be added to the drag: {entry.FullPath}. Select the current items again.");
                transfer.Add(DataTransferItem.CreateFile(item));
            }
            await DragDrop.DoDragDropAsync(press, transfer, DragDropEffects.Copy | DragDropEffects.Move);
        }
        catch (Exception error) { Error?.Invoke(error); }
        finally { _dragging = false; }
    }
    private void OnDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = !IsLoading && e.DataTransfer.TryGetFiles()?.Any() == true
            ? ((e.KeyModifiers & KeyModifiers.Shift) != 0 ? DragDropEffects.Move : DragDropEffects.Copy)
            : DragDropEffects.None;
        e.Handled = true;
    }
    private async void OnDrop(object? sender, DragEventArgs e)
    {
        if (IsLoading) { e.DragEffects = DragDropEffects.None; e.Handled = true; return; }
        e.Handled = true;
        if (FilesDropped is null) return;
        var files = e.DataTransfer.TryGetFiles()?.ToArray() ?? [];
        if (files.Length == 0) return;
        try
        {
            var paths = files.Select(item =>
            {
                var path = item.TryGetLocalPath();
                return !string.IsNullOrWhiteSpace(path) ? path
                    : throw new IOException("The dropped selection contains an item without a local file path. No items were transferred.");
            }).ToArray();
            await FilesDropped(this, paths, (e.KeyModifiers & KeyModifiers.Shift) != 0,
                sender is Button { Name: "HomeFolderButton" });
        }
        catch (Exception error) { e.DragEffects = DragDropEffects.None; Error?.Invoke(error); }
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _readCancellation?.Cancel(); _readCancellation?.Dispose();
        ++_filterGeneration;
        _filterCancellation?.Cancel(); _filterCancellation?.Dispose();
        _nameCells.Clear();
        _watcher?.Dispose(); _watchDebounce.Stop();
    }

    private Button BuildHomeButton()
    {
        void DispatchHome(string command)
        {
            Activated?.Invoke(this);
            CommandRequested?.Invoke(this, command);
        }
        var home = Ui.IconButton("home", "Home folder", () => DispatchHome("home"));
        home.Name = "HomeFolderButton";
        home.AddHandler(PointerPressedEvent, (_, e) =>
        {
            if (!e.GetCurrentPoint(home).Properties.IsMiddleButtonPressed) return;
            e.Handled = true;
            DispatchHome("home-new-tab");
        }, RoutingStrategies.Tunnel);
        DragDrop.SetAllowDrop(home, true);
        home.AddHandler(DragDrop.DragOverEvent, OnDragOver);
        home.AddHandler(DragDrop.DropEvent, OnDrop);
        var menu = new ContextMenu();
        foreach (var (label, command) in new[]
        {
            ("Set Current Folder as Home", "home-set-current"), ("Reset Home Folder", "home-reset"),
            ("Home Folder Preferences…", "home-settings")
        })
        {
            var item = new MenuItem { Header = label };
            item.Click += (_, _) => DispatchHome(command);
            menu.Items.Add(item);
        }
        home.ContextMenu = menu;
        return home;
    }

    private sealed record DuplicateTabResults(DuplicateResult Result, string Description, IReadOnlyDictionary<string, DuplicateGroupHeader> Headers);

    private sealed class PaneTab
    {
        public string? InitialDirectory { get; set; }
        public TabSettings Settings { get; }
        public DuplicateTabResults? DuplicateResults { get; set; }
        public HashSet<string> CheckedPaths { get; } = new(PathComparer);
        public Dictionary<string, Dictionary<string, long>> CachedFolderSizes { get; } = new(PathComparer);
        private readonly List<string> _history = [];
        private int _index = -1;
        public bool CanBack => _index > 0;
        public bool CanForward => _index + 1 < _history.Count;
        public PaneTab(TabSettings settings) { Settings = settings; Visit(settings.Path); }
        public void Visit(string path)
        {
            if (_index >= 0 && PathComparer.Equals(_history[_index], path)) return;
            if (_index + 1 < _history.Count) _history.RemoveRange(_index + 1, _history.Count - _index - 1);
            _history.Add(path); _index = _history.Count - 1;
            if (_history.Count > 200) { _history.RemoveAt(0); _index--; }
        }
        public string? Move(int delta)
        {
            var next = _index + delta;
            if (next < 0 || next >= _history.Count) return null;
            _index = next; return _history[_index];
        }
    }
}
