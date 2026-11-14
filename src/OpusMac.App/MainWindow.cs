using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using OpusMac.Core;
using OpusMac.Core.Browsing;
using OpusMac.Core.Operations;
using OpusMac.Core.Persistence;
using OpusMac.Core.Platform;
using OpusMac.Core.Search;

namespace OpusMac.App;

public sealed partial class MainWindow : Window
{
    private readonly LaunchOptions _options;
    private readonly SettingsStore _store;
    private readonly AppSettings _settings;
    private readonly SharedAppearance _appearance;
    private readonly DirectoryBrowser _browser = new();
    private readonly IFilePlatform _platform = new DesktopFilePlatform();
    private readonly FileOperationService _operations;
    private readonly ListaryClient _listary = new();
    private readonly PaneView[] _panes;
    private readonly PreviewPane _preview;
    private readonly TreeView _tree = new() { Name = "FolderTree", MinWidth = 0 };
    private readonly Grid _workspace = new();
    private readonly Grid _paneGrid = new();
    private readonly TreeView _secondTree = new() { Name = "DestinationFolderTree", MinWidth = 0 };
    private readonly Border _treeHost;
    private readonly Border _secondTreeHost;
    private readonly Grid[] _treePaneHosts;
    private readonly GridSplitter[] _localTreeSplitters;
    private readonly Border _viewerHost;
    private readonly MenuItem _navigationLockMenu = new()
    {
        Header = "Navigation Lock", ToggleType = MenuItemToggleType.CheckBox
    };
    private readonly (object Tab, string Base)[] _navigationBases = new (object, string)[2];
    private readonly Button _navigationLockButton;
    private readonly Border _navigationLockWarning;
    private sealed record LinkedNavigationBases(PaneView First, string FirstPath, string SecondPath);
    private readonly Dictionary<Guid, LinkedNavigationBases> _linkedNavigationLocks = [];
    private bool _navigationLocked;
    private long _navigationLockRevision;
    private long _navigationLockControlsRevision;
    private readonly TextBlock _message = Ui.Text("Ready", true);
    private readonly TextBlock _jobText = Ui.Text("No file operations running", true);
    private readonly ProgressBar _jobProgress = new() { Minimum = 0, Maximum = 100, Height = 5 };
    private readonly StackPanel _jobPanel;
    private readonly MenuItem _favoritesBarMenu = new() { Header = "Favorites Bar", ToggleType = MenuItemToggleType.CheckBox };
    private readonly Control _bookmarkBar;
    private readonly StackPanel _bookmarkButtons = new() { Orientation = Orientation.Horizontal, Spacing = 3 };
    private readonly MenuItem _favoritesMenu = new() { Header = "Favorites" };
    private readonly TextBox _search = new() { Name = "FileSearch", Watermark = "Search files", MinWidth = 180 };
    private readonly ComboBox _searchScope = new() { Name = "SearchScope", ItemsSource = new[] { "This folder", "Subfolders", "Listary index" }, SelectedIndex = 0, Width = 130 };
    private readonly Button _undo;
    private MenuItem? _undoMenuItem;
    private readonly Button _cancel;
    private readonly DispatcherTimer _saveDebounce = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly Dictionary<string, ActiveJob> _jobs = [];
    private CancellationTokenSource? _toolCancellation;
    private Task? _initialization;
    private int _activePane;
    private bool _allowClose;
    private bool _closingState;
    private bool _suppressScheduledSave;
    private CancellationTokenSource _shutdownCancellation = new();
    private readonly HashSet<Task> _pendingCommands = [];
    private bool _disposing;
    private bool _selectingTreeFromFind;
    private readonly SemaphoreSlim _saveGate = new(1, 1);
    private List<string> _clipboardPaths = [];
    private bool _clipboardCut;
    private IAsyncDataTransfer? _clipboardTransfer;
    private long _previewGeneration;
    private Task _toolTask = Task.CompletedTask;
    private Task _lastCommand = Task.CompletedTask;
    public Task Initialization => _initialization ?? Task.CompletedTask;
    public Task LastCommand => _lastCommand;
    internal PaneView ActivePane => _panes[_activePane];
    internal PaneView OtherPane => _panes[1 - _activePane];
    internal bool DualPaneVisible => _settings.DualPane;
    internal bool ViewerVisible => _settings.ShowViewer;
    internal bool TreeVisible => _settings.ShowTree;
    internal string SplitOrientation => _settings.SplitOrientation;
    internal int ActivePaneIndex => _activePane;
    internal PreviewPane Viewer => _preview;
    internal string StatusMessage => _message.Text ?? "";
    internal FileOperationResult? LastOperation => _lastOperation;
    internal IReadOnlyList<OperationHistoryItem> OperationHistory => _operations.History;
    internal bool CanUndoOperation => _operations.CanUndo;
    internal bool UsesSharedAppearance => _appearance.UsesSharedProfile;
    internal string AppearanceProfile => _appearance.ProfileId;
    internal string AppearanceStatus => _appearance.Status;
    internal string AppearanceSettingsPath => _appearance.SettingsPath;

    public MainWindow(LaunchOptions options)
    {
        _options = options;
        _store = new SettingsStore(options.StateDirectory);
        _settings = _store.Load();
        _appearance = SharedAppearance.Load(_settings.Theme);
        _operations = new FileOperationService(_platform, _store.StateDirectory);
        Title = "Directory Opus Mac";
        Width = _settings.WindowWidth; Height = _settings.WindowHeight;
        MinWidth = 1000; MinHeight = 650;
        _appearance.Apply(Application.Current!);
        while (_settings.Panes.Count < 2) _settings.Panes.Add(new PaneSettings());
        _panes = _settings.Panes.Select(pane => new PaneView(_browser, pane,
            () => _settings.ShowHidden, (directory, suppliesSetting) => _settings.ResolveFolderFormat(directory, suppliesSetting),
            ExpandLocationAliasAsync, path => _settings.ResolveFolderSizeMode(path), () => _settings.CacheFolderSizesInHistory,
            () => _settings.UpdateFolderSizesAfterOperations,
            () => (!_settings.IgnoreLinksBelowSizedFolders, !_settings.IgnoreLinkedFoldersForAutomaticSizing))).ToArray();
        _preview = new PreviewPane(() => ExecuteCommandGuardedAsync("quick-look"), ShowError);
        foreach (var pane in _panes)
        {
            pane.Activated += SetActive;
            pane.SelectionChanged += changed => { if (changed == ActivePane) UpdateSelection(); };
            pane.Navigated += changed =>
            {
                if (changed == ActivePane)
                {
                    Title = $"Directory Opus Mac — {changed.CurrentDirectory}";
                    UpdateWorkspaceRows();
                    UpdateSelection();
                }
                ScheduleSave();
            };
            pane.FolderCommitted += changed =>
            {
                UpdateNavigationLockControls();
                Observe(FollowPairedNavigationAsync(changed));
            };
            pane.LinkedTabSelected += (changed, group) =>
            {
                if (_settings.DualPane) _panes[1 - Array.IndexOf(_panes, changed)].ActivateLinkedTab(group);
            };
            pane.UnlinkTabRequested += UnlinkTabs;
            pane.ToggleTabLinkRequested += ToggleTabLink;
            pane.ToggleLinkedNavigationRequested += ToggleLinkedNavigationLock;
            pane.IsLinkedNavigationLocked = _linkedNavigationLocks.ContainsKey;
            pane.Error += ShowError;
            pane.OpenRequested += OpenEntryAsync;
            pane.ValidateSelectionRequested += view => ResolveSelectionAsync(view, useChecks: false);
            pane.RenameRequested += RenameEntryAsync;
            pane.FilesDropped += TransferDroppedPathsAsync;
            pane.CommandRequested += (_, command) => Dispatch(command);
        }
        _treeHost = CreateFolderTreeHost(_tree, () => UseIndependentTrees ? _panes[0] : ActivePane);
        _secondTreeHost = CreateFolderTreeHost(_secondTree, () => _panes[1], "destination-tree");
        _treePaneHosts =
        [
            new() { ColumnDefinitions = new ColumnDefinitions("180,3,*") },
            new() { ColumnDefinitions = new ColumnDefinitions("180,3,*") }
        ];
        _localTreeSplitters =
        [
            CreateLocalTreeSplitter("SourceTreeSplitter", 0),
            CreateLocalTreeSplitter("DestinationTreeSplitter", 1)
        ];
        _viewerHost = Ui.Surface(_preview, "inset");
        _workspace.ColumnDefinitions = new ColumnDefinitions("180,3,*,3,520");
        _workspace.RowDefinitions = new RowDefinitions("*,Auto");
        _workspace.Children.Add(_treeHost);
        var treeSplitter = new GridSplitter
        {
            Name = "TreeSplitter", Width = 4, HorizontalAlignment = HorizontalAlignment.Stretch,
            ResizeDirection = GridResizeDirection.Columns, ResizeBehavior = GridResizeBehavior.PreviousAndNext
        };
        treeSplitter.DragCompleted += (_, _) =>
        {
            if (!_settings.ShowTree) return;
            _settings.TreeWidth = Math.Clamp(_workspace.ColumnDefinitions[0].Width.Value, 120, 420);
            UpdateSidebarWidths();
            ScheduleSave();
        };
        Grid.SetColumn(treeSplitter, 1); _workspace.Children.Add(treeSplitter);
        Grid.SetColumn(_paneGrid, 2); _workspace.Children.Add(_paneGrid);
        var previewSplitter = new GridSplitter
        {
            Name = "ViewerSplitter", Width = 4, HorizontalAlignment = HorizontalAlignment.Stretch,
            ResizeDirection = GridResizeDirection.Columns, ResizeBehavior = GridResizeBehavior.PreviousAndNext
        };
        previewSplitter.DragCompleted += (_, _) =>
        {
            if (!_settings.ShowViewer) return;
            _settings.ViewerWidth = Math.Clamp(_workspace.ColumnDefinitions[4].Width.Value, 300, 960);
            UpdateSidebarWidths();
            ScheduleSave();
        };
        Grid.SetColumn(previewSplitter, 3); _workspace.Children.Add(previewSplitter);
        Grid.SetColumn(_viewerHost, 4); _workspace.Children.Add(_viewerHost);

        var menus = BuildMenu();
        _bookmarkBar = BuildBookmarkStrip();
        UpdateFavoritesBar();
        _favoritesBarMenu.Click += (_, _) => Dispatch("favorites-bar");
        var operations = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(5, 3) };
        foreach (var (label, command) in new[] { ("Copy Files", "copy-files"), ("Move Files", "move-files"), ("Rename", "advanced-rename"),
                     ("New Folder", "new-folder"), ("Delete", "trash"), ("Archive", "archive"), ("Properties", "properties") })
        {
            var button = Ui.Button(label, () => Dispatch(command));
            button.Margin = new Thickness(0, 0, 4, 0);
            operations.Children.Add(button);
        }
        // The user's installed Opus 13.22.11 Lister keeps these commands in its
        // menus/context bindings rather than a persistent large text-button belt.
        // Preserve every command and automation target without consuming first-
        // viewport space with replacement-specific chrome.
        operations.IsVisible = false;
        _undo = Ui.Button("Undo", () => Dispatch("undo"), "Undo the last undoable file operation");
        operations.Children.Add(_undo);
        foreach (var (label, command) in new[] { ("Dual", "dual"), ("Viewer", "viewer"), ("Tree", "tree") })
        {
            var button = Ui.Button(label, () => Dispatch(command));
            button.Margin = new Thickness(5, 0, 0, 0);
            operations.Children.Add(button);
        }
        _navigationLockButton = Ui.IconButton("link", "Navigation Lock", () => Dispatch("navigation-lock"));
        _navigationLockWarning = BuildNavigationLockWarning();
        Grid.SetColumn(_navigationLockWarning, 2);
        _workspace.Children.Add(_navigationLockWarning);
        var commandHeader = BuildReferenceCommandHeader();
        _search.KeyDown += (_, e) => { if (e.Key == Key.Enter) { Dispatch("search"); e.Handled = true; } };

        _cancel = Ui.Button("Cancel work", CancelWork);
        _cancel.IsEnabled = false;
        var history = Ui.Button("History", () => Dispatch("history"));
        var jobRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"), ColumnSpacing = 7 };
        jobRow.Children.Add(_jobText); Grid.SetColumn(history, 1); jobRow.Children.Add(history);
        Grid.SetColumn(_cancel, 2); jobRow.Children.Add(_cancel);
        _jobPanel = new StackPanel { Margin = new Thickness(5, 2), Spacing = 2, IsVisible = false, Children = { jobRow, _jobProgress } };
        _message.TextTrimming = TextTrimming.CharacterEllipsis;
        var root = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,Auto,*,Auto,Auto,Auto") };
        root.Children.Add(_bookmarkBar);
        Grid.SetRow(menus, 1); root.Children.Add(menus);
        Grid.SetRow(commandHeader, 2); root.Children.Add(commandHeader);
        Grid.SetRow(_workspace, 3); root.Children.Add(_workspace);
        Grid.SetRow(_duplicatePanel, 4); root.Children.Add(_duplicatePanel);
        Grid.SetRow(_jobPanel, 5); root.Children.Add(_jobPanel);
        var status = Ui.Surface(_message, padding: new Thickness(5, 2));
        Grid.SetRow(status, 6); root.Children.Add(status);
        Content = root;
        Opened += (_, _) => _appearance.ApplyWindowChrome(this);
        _operations.HistoryChanged += OnHistoryChanged;
        _saveDebounce.Tick += (_, _) => { _saveDebounce.Stop(); Observe(SaveSettingsAsync()); };
        Opened += (_, _) => Observe(InitializeAsync());
        Closing += OnClosing;
        Closed += (_, _) => DisposeServices();
        SizeChanged += (_, _) => UpdateSidebarWidths();
        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
        _activePane = Math.Clamp(_settings.ActivePane, 0, 1);
        ApplyLayout();
        OnHistoryChanged();
    }

    private void UpdateJobPanelVisibility() => _jobPanel.IsVisible = _jobs.Count > 0 || _toolCancellation is not null;

    private Menu BuildMenu()
    {
        var menu = new Menu();
        void Add(string heading, params (string Label, string Command)[] commands)
        {
            var item = new MenuItem { Header = heading };
            foreach (var command in commands)
            {
                var child = new MenuItem { Header = command.Label };
                child.Click += (_, _) => Dispatch(command.Command);
                item.Items.Add(child);
            }
            menu.Items.Add(item);
        }
        Add("_File", ("Open folder…", "open-folder"), ("Open selected", "open"), ("Open selected in new tab", "new-tab-selected"),
            ("New tab", "new-tab"), ("Close tab", "close-tab"), ("Quit", "quit"));
        Add("_Edit", ("Undo", "undo"), ("Cut", "cut"), ("Copy", "copy-clipboard"), ("Paste", "paste"),
            ("Copy Full Pathnames", "copy-paths"), ("Copy File Names", "copy-names"),
            ("Calculate Folder Sizes", "folder-sizes"),
            ("Copy Files", "copy-files"), ("Move Files", "move-files"),
            ("Update All", "update-all"), ("Update Existing", "update-existing"),
            ("Duplicate", "duplicate"), ("Rename", "rename"), ("Advanced Rename…", "advanced-rename"),
            ("Select All", "select-all"), ("Select by Pattern…", "select-pattern"), ("Invert Selection", "invert"), ("New Folder", "new-folder"), ("New Text File", "new-file"), ("Move to Trash", "trash"));
        var edit = menu.Items.OfType<MenuItem>().Single(item => Equals(item.Header, "_Edit"));
        _undoMenuItem = edit.Items.OfType<MenuItem>().Single(item => Equals(item.Header, "Undo"));
        var advancedLinks = new MenuItem { Header = "Create Advanced Links" };
        var hardLinks = new MenuItem { Header = "Create Hard Links" };
        hardLinks.Click += (_, _) => Dispatch("hard-links");
        advancedLinks.Items.Add(hardLinks);
        var moveFiles = edit.Items.OfType<MenuItem>().Single(item => Equals(item.Header, "Move Files"));
        edit.Items.Insert(edit.Items.IndexOf(moveFiles) + 1, advancedLinks);
        var selectOther = new MenuItem { Header = "Select Other" };
        foreach (var (label, command) in new[]
        {
            ("Reselect Files", "reselect-files"), ("Select Same Extensions", "select-same-extensions"), ("Select Random Item", "select-random"),
            ("Select Empty Folders", "select-empty-folders"), ("Select Zero-Byte Folders", "select-zero-folders"),
            ("Select Source Selection to Destination", "select-source-selection"),
            ("Select Source All to Destination", "select-source-all"),
            ("Select Source All to Destination (ignore extensions)", "select-source-stems"),
            ("Select Source-not-in-Destination", "select-source-missing"),
            ("Selection to Checkboxes", "selection-to-checkboxes"), ("Checkboxes to Selection", "checkboxes-to-selection"),
            ("Select Duplicates…", "select-duplicates")
        })
        {
            var action = new MenuItem { Header = label };
            action.Click += (_, _) => Dispatch(command);
            selectOther.Items.Add(action);
        }
        var invertSelection = edit.Items.OfType<MenuItem>().Single(item => Equals(item.Header, "Invert Selection"));
        edit.Items.Insert(edit.Items.IndexOf(invertSelection) + 1, selectOther);
        Add("_Tools", ("Find Files", "focus-search"), ("Find Duplicate Files…", "duplicates"), ("Synchronize / Compare Folders…", "compare"),
            ("Print / Export Folder Listing…", "print-folder"),
            ("Join Files…", "join-files"),
            ("Split File…", "split-file"),
            ("Create ZIP…", "archive"), ("Browse / Extract ZIP…", "extract"), ("Quick Look", "quick-look"),
            (OperatingSystem.IsMacOS() ? "Reveal in Finder" : OperatingSystem.IsWindows() ? "Reveal in Explorer" : "Reveal in file manager", "reveal"),
            ("Open Terminal Here", "terminal"), ("Operation History", "history"));
        Add("_Settings", ("Preferences…", "settings"), ("Home Folder…", "home-settings"), ("Folder Sizes…", "folder-size-settings"), ("Folder Aliases…", "folder-aliases"), ("Paired Folders…", "folder-pairs"), ("Manage Folder Formats…", "manage-folder-formats"), ("Save Lister Layout As…", "save-lister-layout"), ("Lister Layouts…", "lister-layouts"),
            ("Add Current Folder to Favorites", "favorite"),
            ("Lock / Unlock Current Tab", "lock-tab"));
        return menu;
    }

    private Control BuildReferenceCommandHeader()
    {
        Menu MakeMenu(params (string Label, (string Label, string Command)[] Commands)[] groups)
        {
            var menu = new Menu { VerticalAlignment = VerticalAlignment.Center };
            foreach (var group in groups)
            {
                var root = new MenuItem { Header = group.Label };
                foreach (var command in group.Commands)
                {
                    var child = command.Command switch
                    {
                        "favorites-bar" => _favoritesBarMenu,
                        "navigation-lock" => _navigationLockMenu,
                        _ => new MenuItem { Header = command.Label }
                    };
                    if (child != _favoritesBarMenu) child.Click += (_, _) => Dispatch(command.Command);
                    if (command.Command == "checkbox-mode")
                    {
                        child.ToggleType = MenuItemToggleType.CheckBox;
                        root.SubmenuOpened += (_, _) => child.IsChecked = ActivePane.CheckboxMode;
                    }
                    root.Items.Add(child);
                }
                menu.Items.Add(root);
            }
            return menu;
        }

        var places = new Menu { VerticalAlignment = VerticalAlignment.Center };
        RefreshFavoritesMenu();
        places.Items.Add(_favoritesMenu);

        var placesRoot = new MenuItem { Header = "Places" };
        foreach (var (label, path) in KnownPlaces())
        {
            var item = new MenuItem { Header = label, IsEnabled = Directory.Exists(path) };
            var target = path;
            item.Click += (_, _) => Observe(ActivePane.NavigateAsync(target));
            placesRoot.Items.Add(item);
        }
        places.Items.Add(placesRoot);

        var commands = MakeMenu(
            ("View",
            [
                ("Viewer Pane (F7)", "viewer"), ("Folder Tree", "tree"), ("Show Hidden Files", "hidden"),
                ("Details", "details-view"), ("Details + Thumbnails", "details-thumbnails"),
                ("Navigation Lock", "navigation-lock"), ("Reset Navigation Lock Position", "navigation-lock-reset"),
                ("Folder Format / Columns…", "folder-format"), ("Lock / Unlock Folder Format", "lock-format"), ("Refresh (F5)", "refresh")
            ]),
            ("Folder",
            [
                ("Open Folder…", "open-folder"), ("New Folder", "new-folder"), ("New Text File", "new-file"),
                ("Rename", "rename"), ("Properties", "properties"), ("Checkbox Mode", "checkbox-mode")
            ]),
            ("Lister",
            [
                ("Commander", "style-commander"), ("Explorer", "style-explorer"),
                ("Single", "style-single"), ("Images", "style-images"), ("Filmstrip", "style-filmstrip"),
                ("Import Opus Style…", "import-lister-style"), ("Favorites Bar", "favorites-bar"),
                ("Dual Folder Tree (Shift+F8)", "destination-tree"),
                ("Dual Vertical", "style-dual-vertical"), ("Dual Horizontal", "style-dual-horizontal"), ("Toggle Dual Display (F6)", "dual"),
                ("Save Lister Layout As…", "save-lister-layout"), ("Lister Layouts…", "lister-layouts")
            ]));

        // Keep the full "Listary index" source visible at compact widths.
        // Balance against the query field rather than growing the toolbar.
        _searchScope.Width = 122;
        _search.MinWidth = 210;
        _search.Width = 242;
        _search.Watermark = "Windows Search";
        var searchButton = Ui.IconButton("search", "Find", () => Dispatch("search"));
        var clearSearch = Ui.IconButton("close", "Clear search", () => Observe(ClearSearchAsync()));
        _moreSearch.Content = Ui.Icon("forward", size: 14);
        _moreSearch.Width = 22;
        _moreSearch.Padding = new Thickness(3);
        _moreSearch.IsEnabled = false;
        _moreSearch.Click += (_, _) => Observe(NextSearchPageAsync());

        var search = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 3,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { _searchScope, _search, searchButton, clearSearch, _moreSearch }
        };
        // The reference lister exposes its view and pane-layout controls beside
        // the search field. These use the same commands as the menus and F6/F7.
        var viewActions = Ui.Row(
            Ui.IconButton("tree", "Toggle folder tree", () => Dispatch("tree")),
            Ui.IconButton("viewer", "Toggle viewer pane", () => Dispatch("viewer")),
            Ui.IconButton("dual", "Toggle dual display", () => Dispatch("dual")),
            Ui.IconButton("horizontal", "Stack panes vertically", () => Dispatch("horizontal")),
            Ui.IconButton("vertical", "Place panes side by side", () => Dispatch("vertical")),
            _navigationLockButton);
        viewActions.Name = "ListerViewToolbar";
        viewActions.HorizontalAlignment = HorizontalAlignment.Right;
        viewActions.VerticalAlignment = VerticalAlignment.Center;
        viewActions.Margin = new Thickness(0, 0, 5, 0);
        var header = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*,Auto"),
            ColumnSpacing = 6,
            Margin = new Thickness(4, 0, 5, 1),
            MinHeight = 25
        };
        header.Children.Add(places);
        Grid.SetColumn(commands, 1); header.Children.Add(commands);
        Grid.SetColumn(viewActions, 2); header.Children.Add(viewActions);
        Grid.SetColumn(search, 3); header.Children.Add(search);
        return Ui.Surface(header, padding: new Thickness(0));
    }

    private static IEnumerable<(string Label, string Path)> KnownPlaces()
    {
        static string Folder(Environment.SpecialFolder folder) => Environment.GetFolderPath(folder);
        var home = Folder(Environment.SpecialFolder.UserProfile);
        yield return ("Home", home);
        yield return ("Desktop", Folder(Environment.SpecialFolder.DesktopDirectory));
        yield return ("Documents", Folder(Environment.SpecialFolder.MyDocuments));
        if (!string.IsNullOrWhiteSpace(home)) yield return ("Downloads", Path.Combine(home, "Downloads"));
    }

    private Control BuildBookmarkStrip()
    {
        RefreshBookmarkStrip();
        var scroll = new ScrollViewer
        {
            Content = _bookmarkButtons,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled
        };
        var bar = Ui.Surface(scroll, padding: new Thickness(5, 1));
        bar.Name = "FavoritesBar";
        return bar;
    }

    private void UpdateFavoritesBar()
    {
        _bookmarkBar.IsVisible = _settings.ShowFavoritesBar;
        _favoritesBarMenu.IsChecked = _settings.ShowFavoritesBar;
    }

    private void RefreshBookmarkStrip()
    {
        _bookmarkButtons.Children.Clear();
        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var shortcuts = _settings.Favorites.Select(favorite => (favorite.Label, favorite.Path))
            .Concat(KnownPlaces().Where(place => place.Label is "Home" or "Downloads" or "Desktop" or "Documents"))
            // Do not synchronously probe saved UNC/offline favorites while laying out the window.
            .Where(place => !string.IsNullOrWhiteSpace(place.Path))
            .DistinctBy(place => Path.TrimEndingDirectorySeparator(place.Path), comparer);
        foreach (var (label, path) in shortcuts)
        {
            var destination = path;
            var button = Ui.Button(label, () => Observe(ActivePane.NavigateAsync(destination)), destination);
            button.Name = "BookmarkShortcut";
            button.Tag = destination;
            button.MinHeight = 22;
            button.Padding = new Thickness(4, 1);
            button.Background = Brushes.Transparent;
            button.BorderThickness = new Thickness(0);
            var text = Ui.Text(label);
            text.TextTrimming = TextTrimming.CharacterEllipsis;
            button.Content = Ui.Row(Ui.Icon("folder", new SolidColorBrush(Color.Parse("#D8AD55")), 13), text);
            _bookmarkButtons.Children.Add(button);
        }
    }

    private void RefreshFavoritesMenu()
    {
        _favoritesMenu.Items.Clear();
        foreach (var favorite in _settings.Favorites)
        {
            var destination = favorite.Path;
            var item = new MenuItem { Header = favorite.Label };
            item.Click += (_, _) => Observe(ActivePane.NavigateAsync(destination));
            _favoritesMenu.Items.Add(item);
        }
        if (_settings.Favorites.Count == 0)
            _favoritesMenu.Items.Add(new MenuItem { Header = "No favorites configured", IsEnabled = false });
        var add = new MenuItem { Header = "Add current folder" };
        add.Click += (_, _) => Dispatch("favorite");
        _favoritesMenu.Items.Add(add);
    }

    public Task InitializeAsync() => _initialization ??= InitializeCoreAsync();
    private async Task InitializeCoreAsync()
    {
        BuildTree();
        for (var index = 0; index < Math.Min(2, _options.Paths.Count); index++)
        {
            var path = _options.Paths[index];
            if (File.Exists(path)) path = Path.GetDirectoryName(path)!;
            _settings.Panes[index].Tabs[_settings.Panes[index].ActiveTab].Path = path;
        }
        await Task.WhenAll(_panes.Select(pane => pane.NavigateAsync(pane.CurrentDirectory, false)));
        _message.Text = _store.LastLoadWarning ?? "Ready · Tab switches source and destination · F6 dual display · F7 viewer";
        ActivePane.FocusList();
    }

    public async Task OpenPathsAsync(IEnumerable<string> paths, bool activate = true)
    {
        EnsureAcceptingWork();
        await InitializeAsync();
        foreach (var input in paths)
        {
            var path = DirectoryBrowser.ResolvePath(input);
            if (Directory.Exists(path)) await ActivePane.AddTabAsync(path);
            else if (File.Exists(path))
            {
                await ActivePane.AddTabAsync(Path.GetDirectoryName(path)!);
                ActivePane.SelectPaths([path]);
            }
            else ShowError(new FileNotFoundException("The requested path no longer exists.", path));
        }
        if (activate) { if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal; Activate(); ActivePane.FocusList(); }
    }
    internal bool QueueOpenPaths(IEnumerable<string> paths, bool activate = true)
    {
        if (_closingState || _disposing) return false;
        Observe(OpenPathsAsync(paths, activate));
        return true;
    }

    private void SetActive(PaneView pane)
    {
        var requested = Array.IndexOf(_panes, pane);
        if (requested < 0 || (!_settings.DualPane && requested != _activePane)) return;
        _activePane = requested;
        for (var index = 0; index < 2; index++) _panes[index].SetActive(index == _activePane);
        _settings.ActivePane = _activePane;
        UpdateNavigationLockControls();
        UpdateSelection(); ScheduleSave();
    }
    private void UpdateSelection() => Observe(UpdatePreviewAsync());
    private Task<(FolderPair Pair, string Target, bool FromPrimary)?> ResolveFolderPairAsync(
        string path, Func<FolderPair, bool> applies) =>
        ResolveFolderPairAsync(path, _settings.FolderPairs.Where(applies).ToArray());

    private Task<(FolderPair Pair, string Target, bool FromPrimary)?> ResolveFolderPairAsync(
        string path, FolderPair[] rules)
    {
        if (rules.Length == 0) return Task.FromResult<(FolderPair, string, bool)?>(null);
        var cancellation = _shutdownCancellation.Token;
        return Task.Run<(FolderPair, string, bool)?>(() =>
        {
            foreach (var rule in rules)
            {
                cancellation.ThrowIfCancellationRequested();
                if (ExpandFolderPairAliases(rule, resolveVolumeLabels: true) is not { } pair) continue;
                if (pair.Resolve(path, out var fromPrimary) is not { } target) continue;
                if (pair.MissingTarget == MissingFolderPairTarget.UsePath) return (pair, target, fromPrimary);
                for (string? candidate = target; candidate is not null; candidate = Path.GetDirectoryName(candidate))
                {
                    if (Directory.Exists(candidate)) return (pair, candidate, fromPrimary);
                    if (pair.MissingTarget == MissingFolderPairTarget.IgnorePair) break;
                }
            }
            return null;
        }, cancellation).WaitAsync(cancellation);
    }

    private Task<string> ExpandLocationAliasAsync(string input, string? initialHome = null)
    {
        if (!input.TrimStart().StartsWith("/$", StringComparison.Ordinal))
            return Task.FromResult(_settings.ExpandFolderAlias(input, _store.StateDirectory, initialHome: initialHome));
        var cancellation = _shutdownCancellation.Token;
        return Task.Run(() =>
        {
            var resolved = _settings.ExpandFolderAlias(input, _store.StateDirectory, resolveVolumeLabel: true);
            if (resolved == input) throw new IOException("The volume label is unavailable or ambiguous.");
            return resolved;
        }, cancellation).WaitAsync(cancellation);
    }

    private static bool IsVolumeAlias(string path) => path.TrimStart().StartsWith("/$", StringComparison.Ordinal);

    private FolderPair? ExpandFolderPairAliases(FolderPair pair, bool resolveVolumeLabels = false)
    {
        if (pair.RegularExpression)
        {
            if (!pair.ExpandRegexAliases) return pair;
            var match = _settings.ExpandRegexFolderAlias(pair.Source, _store.StateDirectory, replacement: false);
            var replace = _settings.ExpandRegexFolderAlias(pair.Destination, _store.StateDirectory, replacement: true);
            return match is not null && replace is not null ? pair with { Source = match, Destination = replace } : null;
        }
        var source = _settings.ExpandFolderAlias(pair.Source, _store.StateDirectory, resolveVolumeLabels);
        var destination = _settings.ExpandFolderAlias(pair.Destination, _store.StateDirectory, resolveVolumeLabels);
        if (IsVolumeAlias(source) || IsVolumeAlias(destination)) return null;
        return FolderPair.TryNormalize(source, out var left) && FolderPair.TryNormalize(destination, out var right) &&
            !FolderPair.Comparer.Equals(left, right) ? pair with { Source = left, Destination = right } : null;
    }

    private void UpdateNavigationLockControls()
    {
        var revision = ++_navigationLockControlsRevision;
        var path = ActivePane.CurrentDirectory;
        var enabled = _settings.DualPane || _settings.FolderPairs.Any(pair => pair.DefaultNavigationLock &&
            !pair.RegularExpression && ExpandFolderPairAliases(pair)?.Resolve(path) is not null);
        _navigationLockMenu.IsEnabled = enabled;
        _navigationLockButton.IsEnabled = enabled;
        if (!enabled && _settings.FolderPairs.Any(pair => pair.DefaultNavigationLock))
            Observe(UpdateAsyncNavigationLockControlsAsync(path, revision));
    }

    private async Task UpdateAsyncNavigationLockControlsAsync(string path, long revision)
    {
        var pair = await ResolveFolderPairAsync(path, static pair => pair.DefaultNavigationLock);
        if (revision != _navigationLockControlsRevision) return;
        _navigationLockMenu.IsEnabled = pair is not null;
        _navigationLockButton.IsEnabled = pair is not null;
    }

    private async Task<bool> OpenFolderPairAsync(PaneView source, string sourcePath, string target,
        int? sourceSide = null, bool linkTabs = false)
    {
        if (source != ActivePane || !FolderPair.Comparer.Equals(source.CurrentDirectory, sourcePath)) return false;
        var sourceRevision = source.ViewRevision.Navigation;
        var dualAtStart = _settings.DualPane;
        var peer = OtherPane;
        if (peer.IsLoading || peer.CurrentTabLocked)
        {
            _message.Text = "Finish loading or unlock the destination tab before opening its paired folder.";
            return false;
        }
        var navigation = peer.NavigateWithoutFolderNotificationAsync(target);
        await navigation;
        if (source != ActivePane || sourceRevision != source.ViewRevision.Navigation || dualAtStart != _settings.DualPane ||
            !ReferenceEquals(peer.LoadingTask, navigation) || !peer.LastNavigationSucceeded ||
            !FolderPair.Comparer.Equals(peer.CurrentDirectory, target) || peer.IsSearchResults) return false;
        if (sourceSide is { } side && _activePane != side)
        {
            Array.Reverse(_panes);
            _activePane = side;
        }
        if (linkTabs && (source.ActiveTabLink is null || source.ActiveTabLink != peer.ActiveTabLink))
            LinkTabs(source, peer);
        _settings.DualPane = true;
        ApplyLayout();
        _message.Text = "Opened the paired folder";
        return true;
    }

    private bool ToggleTabLink(PaneView source, int tabIndex)
    {
        if (!_settings.DualPane) return false;
        var peer = _panes[1 - Array.IndexOf(_panes, source)];
        source.SelectTabWithoutLink(tabIndex);
        if (source.ActiveTabLink is { } group && peer.ActiveTabLink == group) UnlinkTabs(group);
        else LinkTabs(source, peer);
        return true;
    }

    private void LinkTabs(PaneView source, PaneView peer)
    {
        if (source.ActiveTabLink is { } sourceLink) UnlinkTabs(sourceLink);
        if (peer.ActiveTabLink is { } peerLink) UnlinkTabs(peerLink);
        var group = Guid.NewGuid();
        source.SetActiveTabLink(group);
        peer.SetActiveTabLink(group);
        ScheduleSave();
    }

    private bool ToggleLinkedNavigationLock(PaneView source, int tabIndex)
    {
        if (!_settings.DualPane) return false;
        source.SelectTabWithoutLink(tabIndex);
        if (source.ActiveTabLink is not { } group) return false;
        if (_linkedNavigationLocks.Remove(group))
        {
            _message.Text = "Linked tab Navigation Lock off";
            return true;
        }
        var peer = _panes[1 - Array.IndexOf(_panes, source)];
        peer.ActivateLinkedTab(group);
        if (peer.ActiveTabLink != group) return false;
        _linkedNavigationLocks[group] = new LinkedNavigationBases(source, source.CurrentDirectory, peer.CurrentDirectory);
        _message.Text = "Linked tab Navigation Lock · current folders paired";
        return true;
    }

    private async Task FollowLinkedNavigationAsync(PaneView source, PaneView peer, Guid group, LinkedNavigationBases bases)
    {
        var first = source == bases.First;
        var sourceBase = first ? bases.FirstPath : bases.SecondPath;
        var peerBase = first ? bases.SecondPath : bases.FirstPath;
        var relative = RelativeFolderBelow(sourceBase, source.CurrentDirectory);
        if (relative is not null)
        {
            var target = Path.GetFullPath(Path.Combine(peerBase, relative));
            if (!peer.IsSearchResults && FolderPair.Comparer.Equals(peer.CurrentDirectory, target)) return;
            if (!peer.IsLoading && !peer.CurrentTabLocked)
            {
                var revision = source.ViewRevision.Navigation;
                var navigation = peer.NavigateWithoutFolderNotificationAsync(target);
                await navigation;
                if (revision != source.ViewRevision.Navigation || peer.ActiveTabLink != group ||
                    !_linkedNavigationLocks.TryGetValue(group, out var current) || !ReferenceEquals(bases, current)) return;
                if (ReferenceEquals(peer.LoadingTask, navigation) && peer.LastNavigationSucceeded &&
                    FolderPair.Comparer.Equals(peer.CurrentDirectory, target)) return;
            }
        }
        if (_settings.WarnWhenNavigationLockOutOfSync) _message.Text = "Linked tab Navigation Lock · out of sync";
    }

    private static string? RelativeFolderBelow(string root, string folder)
    {
        var relative = Path.GetRelativePath(root, folder);
        return Path.IsPathRooted(relative) || relative == ".." ||
            relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) ? null : relative;
    }

    private void UnlinkTabs(Guid group)
    {
        _linkedNavigationLocks.Remove(group);
        foreach (var pane in _panes) pane.RemoveTabLink(group);
        ScheduleSave();
    }

    private async Task FollowPairedNavigationAsync(PaneView source)
    {
        var peer = _panes[1 - Array.IndexOf(_panes, source)];
        if (_settings.DualPane && source.ActiveTabLink is { } group && peer.ActiveTabLink == group &&
            _linkedNavigationLocks.TryGetValue(group, out var bases))
        {
            await FollowLinkedNavigationAsync(source, peer, group, bases);
            return;
        }
        if (_navigationLocked)
        {
            await FollowLockedNavigationAsync(source);
            return;
        }
        if (source != ActivePane || _initialization?.IsCompletedSuccessfully != true) return;

        var dual = _settings.DualPane;
        var directory = source.CurrentDirectory;
        var revision = source.ViewRevision.Navigation;
        var pair = await ResolveFolderPairAsync(directory,
            rule => rule.DefaultDual && (dual ? rule.ReadIntoExistingDual : rule.SwitchToDualAutomatically));
        if (pair is not { } match || source != ActivePane || _navigationLocked || dual != _settings.DualPane ||
            revision != source.ViewRevision.Navigation) return;

        var side = match.Pair.PrimaryOnLeft ? (match.FromPrimary ? 0 : 1) : (int?)null;
        if (await OpenFolderPairAsync(source, directory, match.Target, side, match.Pair.LinkPairedTabs) && !dual && match.Pair.LockOnDual)
            SetNavigationLock(true);
    }

    private void SetNavigationLock(bool enabled)
    {
        _navigationLocked = enabled && _settings.DualPane;
        _navigationLockMenu.IsChecked = _navigationLocked;
        _navigationLockButton.Classes.Set("navigation-locked", _navigationLocked);
        _navigationLockWarning.IsVisible = false;
        _navigationLockRevision++;
        if (!_navigationLocked) return;
        for (var index = 0; index < _panes.Length; index++)
            _navigationBases[index] = (_panes[index].ActiveTabIdentity, _panes[index].CurrentDirectory);
        _message.Text = "Navigation Lock · current folders paired";
    }

    private async Task FollowLockedNavigationAsync(PaneView changed)
    {
        if (!_navigationLocked) return;
        if (_panes.Where((pane, index) => !ReferenceEquals(pane.ActiveTabIdentity, _navigationBases[index].Tab)).Any())
        {
            SetNavigationLock(true);
            return;
        }

        var source = Array.IndexOf(_panes, changed);
        var peer = _panes[1 - source];
        var comparer = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var relative = RelativeFolderBelow(_navigationBases[source].Base, changed.CurrentDirectory);
        var outside = relative is null;
        var revision = ++_navigationLockRevision;
        var sourceRead = changed.ViewRevision.Navigation;
        string? target;
        if (outside)
        {
            var paired = await ResolveFolderPairAsync(changed.CurrentDirectory, static _ => true);
            if (revision != _navigationLockRevision || sourceRead != changed.ViewRevision.Navigation) return;
            target = paired?.Target;
            if (target is null && _settings.CancelNavigationLockWhenNavigatingAway)
            {
                SetNavigationLock(false);
                return;
            }
        }
        else target = Path.GetFullPath(Path.Combine(_navigationBases[1 - source].Base, relative!));

        if (target is not null)
        {
            var matched = !peer.IsLoading && !peer.IsSearchResults && string.Equals(peer.CurrentDirectory, target, comparer);
            if (!matched && !peer.IsLoading && !peer.CurrentTabLocked)
            {
                var navigation = peer.NavigateWithoutFolderNotificationAsync(target);
                await navigation;
                if (revision != _navigationLockRevision || sourceRead != changed.ViewRevision.Navigation) return;
                matched = ReferenceEquals(peer.LoadingTask, navigation) && peer.LastNavigationSucceeded &&
                    string.Equals(peer.CurrentDirectory, target, comparer);
            }
            if (matched)
            {
                if (outside) SetNavigationLock(true);
                _navigationLockWarning.IsVisible = false;
                _message.Text = "Navigation Lock · folders in sync";
                return;
            }
        }
        if (_settings.WarnWhenNavigationLockOutOfSync)
        {
            _navigationLockWarning.IsVisible = true;
            _message.Text = "Navigation Lock · out of sync";
        }
    }

    private Border BuildNavigationLockWarning()
    {
        var detail = Ui.Text("The two displays are out of sync. Return to matching folders or reset the pairing.");
        detail.TextWrapping = TextWrapping.Wrap;
        var actions = new WrapPanel { Orientation = Orientation.Horizontal };
        actions.Children.Add(Ui.Button("Reset sync position", () => Dispatch("navigation-lock-reset")));
        actions.Children.Add(Ui.Button("Turn off Navigation Lock", () => Dispatch("navigation-lock")));
        actions.Children.Add(Ui.IconButton("close", "Dismiss Navigation Lock warning",
            () => _navigationLockWarning.IsVisible = false));
        var warning = Ui.Surface(new StackPanel
        {
            Spacing = 5, Children = { Ui.Text("Navigation Lock"), detail, actions }
        }, padding: new Thickness(10, 6));
        warning.Name = "NavigationLockWarning";
        warning.Margin = new Thickness(6, 6, 6, 28);
        warning.HorizontalAlignment = HorizontalAlignment.Center;
        warning.VerticalAlignment = VerticalAlignment.Bottom;
        warning.MaxWidth = 460;
        warning.IsVisible = false;
        return warning;
    }

    private bool UseIndependentTrees => _settings.DualPane && _settings.IndependentFolderTrees;

    private Border CreateFolderTreeHost(TreeView tree, Func<PaneView> targetPane, string hideCommand = "tree")
    {
        tree.SelectionChanged += (_, _) =>
        {
            // Inline Find dispatches exactly one navigation through the pane
            // itself: a TreeViewItem can be loaded yet not have a realized
            // presenter, so IsSelected alone does not reliably raise this.
            if (_selectingTreeFromFind) return;
            if (tree.SelectedItem is TreeViewItem { Tag: string path }) NavigateFromTree(targetPane(), path);
        };
        // The installed Lister reserves a small, actionable header for the
        // folder tree. Keep its commands local to the existing tree/layout.
        var treePanel = new Grid { RowDefinitions = new RowDefinitions("Auto,*") };
        var treeHeader = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto,Auto"), Margin = new Thickness(5, 2, 3, 2) };
        var treeTitle = Ui.Text("Folder Tree", true);
        treeTitle.FontWeight = FontWeight.SemiBold;
        treeHeader.Children.Add(treeTitle);
        var treeFind = new TextBox
        {
            Name = "FolderTreeFind", Watermark = "Find folder", MinWidth = 0,
            IsVisible = false, Padding = new Thickness(3, 1), Height = 24
        };
        treeHeader.Children.Add(treeFind);
        void CloseTreeFind()
        {
            treeFind.Text = "";
            treeFind.IsVisible = false;
            treeTitle.IsVisible = true;
            targetPane().FocusList();
        }
        var findTree = Ui.IconButton("search", "Find in folder tree", () =>
        {
            if (treeFind.IsVisible) { CloseTreeFind(); return; }
            treeTitle.IsVisible = false;
            treeFind.IsVisible = true;
            treeFind.Focus();
        });
        findTree.Name = "FindFolderTree";
        Grid.SetColumn(findTree, 1); treeHeader.Children.Add(findTree);
        treeFind.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                CloseTreeFind();
                e.Handled = true;
            }
            else if (e.Key == Key.Enter)
            {
                var query = (treeFind.Text ?? "").Trim();
                if (query.Length > 0 && !SelectLoadedFolderFromTree(tree, targetPane(), query))
                    _message.Text = "No loaded Folder Tree entry matches. Expand a branch or use Find Files.";
                e.Handled = true;
            }
        };
        var collapseTree = Ui.IconButton("up", "Collapse folder tree branches", () => CollapseFolderTree(tree));
        collapseTree.Name = "CollapseTreeBranches";
        Grid.SetColumn(collapseTree, 2); treeHeader.Children.Add(collapseTree);
        var hideTree = Ui.IconButton("close", "Hide folder tree", () => Dispatch(hideCommand));
        hideTree.Name = "HideTreeFromHeader";
        Grid.SetColumn(hideTree, 3); treeHeader.Children.Add(hideTree);
        treePanel.Children.Add(treeHeader);
        Grid.SetRow(tree, 1); treePanel.Children.Add(tree);
        return Ui.Surface(treePanel, "inset");
    }

    private void NavigateFromTree(PaneView pane, string path)
    {
        SetActive(pane);
        Observe(pane.NavigateAsync(path));
    }

    private GridSplitter CreateLocalTreeSplitter(string name, int paneIndex)
    {
        var splitter = new GridSplitter
        {
            Name = name, Width = 3, HorizontalAlignment = HorizontalAlignment.Stretch,
            ResizeDirection = GridResizeDirection.Columns, ResizeBehavior = GridResizeBehavior.PreviousAndNext
        };
        Grid.SetColumn(splitter, 1);
        splitter.DragCompleted += (_, _) =>
        {
            if (!UseIndependentTrees) return;
            var width = Math.Clamp(_treePaneHosts[paneIndex].ColumnDefinitions[0].Width.Value, 120, 420);
            if (paneIndex == 0) _settings.TreeWidth = width;
            else _settings.DestinationTreeWidth = width;
            UpdateSidebarWidths();
            ScheduleSave();
        };
        return splitter;
    }

    private void ApplyLayout()
    {
        if (!_settings.DualPane) SetNavigationLock(false);
        foreach (var pane in _panes) pane.CanLinkTabs = _settings.DualPane;
        UpdateNavigationLockControls();
        _paneGrid.Children.Clear();
        foreach (var host in _treePaneHosts) host.Children.Clear();
        var independent = UseIndependentTrees;
        if (independent)
        {
            _workspace.Children.Remove(_treeHost);
            if (_secondTree.ItemsSource is null) PopulateTree(_secondTree);
        }
        else if (!_workspace.Children.Contains(_treeHost))
        {
            _workspace.Children.Add(_treeHost);
        }

        Control Display(PaneView pane, int index)
        {
            if (!independent || index == 0 && !_settings.ShowTree) return pane;
            var host = _treePaneHosts[index];
            var treeHost = index == 0 ? _treeHost : _secondTreeHost;
            Grid.SetRow(treeHost, 0);
            Grid.SetColumn(treeHost, 0);
            Grid.SetRow(pane, 0);
            Grid.SetColumn(pane, 2);
            host.Children.Add(treeHost);
            host.Children.Add(_localTreeSplitters[index]);
            host.Children.Add(pane);
            return host;
        }

        var horizontal = _settings.SplitOrientation == "Horizontal";
        _paneGrid.RowDefinitions = new RowDefinitions(horizontal && _settings.DualPane ? "*,4,*" : "*");
        _paneGrid.ColumnDefinitions = new ColumnDefinitions(!horizontal && _settings.DualPane ? "*,4,*" : "*");
        var firstPane = Display(_settings.DualPane ? _panes[0] : ActivePane, 0);
        Grid.SetRow(firstPane, 0); Grid.SetColumn(firstPane, 0); _paneGrid.Children.Add(firstPane);
        if (_settings.DualPane)
        {
            var splitter = new GridSplitter
            {
                Name = "FilePaneSplitter", ResizeBehavior = GridResizeBehavior.PreviousAndNext,
                ResizeDirection = horizontal ? GridResizeDirection.Rows : GridResizeDirection.Columns
            };
            void ApplyPaneShare(double share)
            {
                var first = new GridLength(share, GridUnitType.Star);
                var second = new GridLength(1 - share, GridUnitType.Star);
                if (horizontal)
                {
                    _paneGrid.RowDefinitions[0].Height = first;
                    _paneGrid.RowDefinitions[2].Height = second;
                }
                else
                {
                    _paneGrid.ColumnDefinitions[0].Width = first;
                    _paneGrid.ColumnDefinitions[2].Width = second;
                }
            }
            ApplyPaneShare(horizontal ? _settings.StackedPaneShare : _settings.SideBySidePaneShare);
            splitter.DragCompleted += (_, _) =>
            {
                var first = horizontal ? _paneGrid.RowDefinitions[0].Height.Value : _paneGrid.ColumnDefinitions[0].Width.Value;
                var second = horizontal ? _paneGrid.RowDefinitions[2].Height.Value : _paneGrid.ColumnDefinitions[2].Width.Value;
                var share = SettingsStore.NormalizePaneShare(first / (first + second));
                if (horizontal) _settings.StackedPaneShare = share;
                else _settings.SideBySidePaneShare = share;
                ApplyPaneShare(share);
                UpdateSidebarWidths();
                ScheduleSave();
            };
            var secondPane = Display(_panes[1], 1);
            if (horizontal) { splitter.Height = 4; Grid.SetRow(splitter, 1); Grid.SetRow(secondPane, 2); Grid.SetColumn(secondPane, 0); }
            else { splitter.Width = 4; Grid.SetColumn(splitter, 1); Grid.SetColumn(secondPane, 2); Grid.SetRow(secondPane, 0); }
            _paneGrid.Children.Add(splitter); _paneGrid.Children.Add(secondPane);
        }
        _treeHost.IsVisible = _settings.ShowTree;
        _viewerHost.IsVisible = _settings.ShowViewer;
        _workspace.ColumnDefinitions[1].Width = new GridLength(_settings.ShowTree && !independent ? 3 : 0);
        _workspace.ColumnDefinitions[3].Width = new GridLength(_settings.ShowViewer ? 3 : 0);
        UpdateSidebarWidths();
        UpdateWorkspaceRows();
        SetActive(ActivePane);
    }
    private void UpdateWorkspaceRows()
    {
        var filmstrip = !_settings.DualPane && !_settings.ShowTree && _settings.ShowViewer &&
            ActivePane.CurrentViewMode == "Filmstrip";
        Grid.SetRow(_paneGrid, filmstrip ? 1 : 0);
        Grid.SetColumn(_paneGrid, filmstrip ? 0 : 2);
        Grid.SetColumnSpan(_paneGrid, filmstrip ? 5 : 1);
        Grid.SetColumn(_viewerHost, filmstrip ? 0 : 4);
        Grid.SetColumnSpan(_viewerHost, filmstrip ? 5 : 1);
    }
    private void UpdateSidebarWidths()
    {
        if (_workspace.ColumnDefinitions.Count < 5) return;
        // Measure the *actual* physical HWND rather than a downscaled desktop
        // helper preview: installed Opus 13.22.11 at 1664x910 has its viewer
        // boundary near x1073 and the tree consumes ~180px; the earlier native
        // 2176x1190 capture begins its viewer near x1410. Interpolate the
        // viewer only; never shrink the correctly matched tree from a scaled
        // screenshot. Preserve the original splitter/visibility owner.
        var scale = Math.Clamp((Width - 1664) / 512.0, 0, 1);
        var viewerFraction = .355 - .003 * scale;
        var desiredViewer = _settings.ViewerWidth ?? Math.Clamp(Width * viewerFraction, 300, 960);
        var maximumTree = Math.Max(120, Width - (_settings.ShowViewer ? 300 : 0) - 400 - 8);
        var treeWidth = _settings.ShowTree ? Math.Min(_settings.TreeWidth ?? 180, Math.Max(180, maximumTree)) : 0;
        var independent = UseIndependentTrees;
        _workspace.ColumnDefinitions[0].Width = new GridLength(independent ? 0 : treeWidth);
        var reservedTree = _settings.ShowTree ? treeWidth + 3 : 0;
        if (independent)
        {
            var sideBySide = _settings.SplitOrientation != "Horizontal";
            var displayWidth = Width - (_settings.ShowViewer ? 303 : 0) - (sideBySide ? 4 : 0);
            var sourceShare = _settings.SideBySidePaneShare;
            var sourceLimit = Math.Max(80, displayWidth * (sideBySide ? sourceShare : 1) - 160);
            var destinationLimit = Math.Max(80, displayWidth * (sideBySide ? 1 - sourceShare : 1) - 160);
            var sourceWidth = _settings.ShowTree ? Math.Min(_settings.TreeWidth ?? 180, sourceLimit) : 0;
            var destinationWidth = Math.Min(_settings.DestinationTreeWidth ?? 180, destinationLimit);
            _treePaneHosts[0].ColumnDefinitions[0].Width = new GridLength(sourceWidth);
            _treePaneHosts[1].ColumnDefinitions[0].Width = new GridLength(destinationWidth);
            var sourceReservation = _settings.ShowTree ? sourceWidth + 3 : 0;
            var destinationReservation = destinationWidth + 3;
            reservedTree = sideBySide ? sourceReservation + destinationReservation : Math.Max(sourceReservation, destinationReservation);
        }
        // At the supported 1000px minimum, a fixed 300px viewer plus the
        // folder tree leaves both side-by-side file lists barely 255px wide.
        // Temporarily compress only the viewer, retaining both file panes,
        // the visible preview and the user's saved splitter preference.
        var filePaneReservation = _settings.DualPane && _settings.SplitOrientation != "Horizontal" ? 590 : 400;
        var availableViewer = Math.Max(180, Width - reservedTree - filePaneReservation - 8);
        _workspace.ColumnDefinitions[4].Width = new GridLength(
            _settings.ShowViewer ? Math.Min(desiredViewer, availableViewer) : 0);
    }
    private void BuildTree()
    {
        PopulateTree(_tree);
        if (_secondTree.ItemsSource is not null) PopulateTree(_secondTree);
    }

    private void PopulateTree(TreeView tree)
    {
        var roots = new List<TreeViewItem>();
        var quickAccess = CreateTreeGroup("Quick access", KnownPlaces().Where(place => place.Label is "Home" or "Downloads")
            .Select(place => CreateKnownTreeNode(place.Label, place.Path)), true);
        roots.Add(quickAccess);

        var favorites = CreateTreeGroup("Favorites", [], true);
        foreach (var favorite in _settings.Favorites) favorites.Items.Add(CreateTreeNode(favorite.Label, favorite.Path));
        if (_settings.Favorites.Count == 0) favorites.Items.Add(new TreeViewItem { Header = "Use Settings to add a folder", IsEnabled = false });
        roots.Add(favorites);

        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        roots.Add(CreateKnownTreeNode("Desktop", desktop));

        var thisPc = CreateTreeGroup("This PC", [], true);
        foreach (var drive in DriveInfo.GetDrives())
            thisPc.Items.Add(CreateTreeNode(drive.Name, drive.RootDirectory.FullName));
        roots.Add(thisPc);

        roots.Add(CreateTreeGroup("File Collections", [new TreeViewItem { Header = "No collections configured", IsEnabled = false }]));
        roots.Add(CreateTreeGroup("Libraries",
        [
            CreateKnownTreeNode("Documents", Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)),
            CreateKnownTreeNode("Pictures", Environment.GetFolderPath(Environment.SpecialFolder.MyPictures)),
            CreateKnownTreeNode("Music", Environment.GetFolderPath(Environment.SpecialFolder.MyMusic)),
            CreateKnownTreeNode("Videos", Environment.GetFolderPath(Environment.SpecialFolder.MyVideos))
        ]));
        roots.Add(CreateTreeGroup("FTP", [new TreeViewItem { Header = "No FTP connection configured", IsEnabled = false }]));
        tree.ItemsSource = roots;
    }
    private static void CollapseFolderTree(TreeView tree)
    {
        if (tree.ItemsSource is not IEnumerable<TreeViewItem> roots) return;
        foreach (var root in roots) CollapseTreeBranch(root);
    }
    // This is a view-local lookup of *already materialized* tree nodes, not a
    // second file index, recursive disk scan, or permission to mutate a result.
    private bool SelectLoadedFolderFromTree(TreeView tree, PaneView pane, string query)
    {
        if (tree.ItemsSource is not IEnumerable<TreeViewItem> roots) return false;
        var visited = new HashSet<TreeViewItem>(ReferenceEqualityComparer.Instance);
        bool Seek(TreeViewItem node, List<TreeViewItem> ancestors)
        {
            if (!visited.Add(node)) return false;
            var text = node.Header switch
            {
                TextBlock block => block.Text ?? "",
                StackPanel panel => panel.Children.OfType<TextBlock>().FirstOrDefault()?.Text ?? "",
                _ => node.Header?.ToString() ?? ""
            };
            // This is a lookup over already loaded UI entries, not a filesystem
            // health probe. A removable/network root may have disappeared;
            // allow its entry to be found and let the existing asynchronous
            // pane navigation report the unavailable-folder error. Checking
            // every node with Directory.Exists stalls the UI for offline roots.
            if (node.IsEnabled && node.Tag is string target &&
                (text.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                 Path.GetFileName(Path.TrimEndingDirectorySeparator(target))
                     .Contains(query, StringComparison.OrdinalIgnoreCase)))
            {
                foreach (var parent in ancestors) parent.IsExpanded = true;
                _message.Text = "Folder Tree: " + target;
                _selectingTreeFromFind = true;
                try { node.IsSelected = true; }
                finally { _selectingTreeFromFind = false; }
                NavigateFromTree(pane, target);
                return true;
            }
            ancestors.Add(node);
            foreach (var child in node.Items.OfType<TreeViewItem>())
                if (Seek(child, ancestors)) return true;
            ancestors.RemoveAt(ancestors.Count - 1);
            return false;
        }
        return roots.Any(root => Seek(root, []));
    }
    private static void CollapseTreeBranch(TreeViewItem node)
    {
        foreach (var child in node.Items.OfType<TreeViewItem>()) CollapseTreeBranch(child);
        node.IsExpanded = false;
    }
    private TreeViewItem CreateKnownTreeNode(string label, string path) =>
        !string.IsNullOrWhiteSpace(path) && Directory.Exists(path)
            ? CreateTreeNode(label, path)
            : new TreeViewItem { Header = label, IsEnabled = false };
    private static TreeViewItem CreateTreeGroup(string label, IEnumerable<TreeViewItem> children, bool expanded = false)
    {
        var title = Ui.Text(label);
        title.FontWeight = FontWeight.SemiBold;
        var group = new TreeViewItem { Header = title, IsExpanded = expanded };
        foreach (var child in children) group.Items.Add(child);
        return group;
    }
    private TreeViewItem CreateTreeNode(string label, string path)
    {
        var node = new TreeViewItem { Header = Ui.Row(Ui.Icon("folder", size: 15), Ui.Text(label)), Tag = path };
        node.Items.Add(new TreeViewItem { Header = "Loading…" });
        var loaded = false;
        node.PropertyChanged += async (_, change) =>
        {
            if (change.Property != TreeViewItem.IsExpandedProperty || !node.IsExpanded || loaded) return;
            loaded = true;
            try
            {
                var listing = await _browser.ReadAsync(path, new BrowseOptions(ShowHidden: _settings.ShowHidden, MaxEntries: 20000));
                node.Items.Clear();
                foreach (var folder in listing.Entries.Where(x => x.IsBrowsable).Take(2000))
                    node.Items.Add(CreateTreeNode(folder.Name, folder.FullPath));
                if (listing.Errors.Count > 0) node.Items.Add(new TreeViewItem { Header = "Some folders could not be read", IsEnabled = false });
            }
            catch (Exception error) { node.Items.Clear(); node.Items.Add(new TreeViewItem { Header = error.Message, IsEnabled = false }); }
        };
        return node;
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        var editing = e.Source is TextBox || e.Source is Control control && control.FindAncestorOfType<TextBox>() is not null;
        var command = Ui.Command(e.KeyModifiers);
        if (e.Key == Key.F4 || command && e.Key == Key.L || e.Key == Key.D && (e.KeyModifiers & KeyModifiers.Alt) != 0)
        { ActivePane.FocusPath(); e.Handled = true; return; }
        if (e.Key == Key.F5) { Dispatch("refresh"); e.Handled = true; return; }
        if (e.Key == Key.F6) { Dispatch("dual"); e.Handled = true; return; }
        if (e.Key == Key.F7) { Dispatch("viewer"); e.Handled = true; return; }
        if (e.Key == Key.F8 && e.KeyModifiers == KeyModifiers.None) { Dispatch("tree"); e.Handled = true; return; }
        if (e.Key == Key.F8 && e.KeyModifiers == KeyModifiers.Shift && _settings.DualPane)
        { Dispatch("destination-tree"); e.Handled = true; return; }
        if (e.Key == Key.F3 && e.KeyModifiers == KeyModifiers.None) { Dispatch("focus-search"); e.Handled = true; return; }
        if (command && e.Key == Key.F) { Dispatch("focus-search"); e.Handled = true; return; }
        if (editing)
        {
            if (command && e.Key is Key.C or Key.X) ++_clipboardRequestGeneration;
            return;
        }
        string? action = e.Key switch
        {
            Key.F2 => "rename", Key.Enter when e.KeyModifiers == KeyModifiers.Alt => "properties",
            Key.Enter => "open", Key.Space when ActivePane.CheckboxMode && e.KeyModifiers == KeyModifiers.None => "toggle-checkbox",
            Key.Space => "quick-look",
            Key.Home when e.KeyModifiers == KeyModifiers.Alt => "home",
            Key.Delete => "trash",
            Key.Back when OperatingSystem.IsWindows() && e.KeyModifiers == KeyModifiers.Control => "drive-root",
            Key.Back when OperatingSystem.IsMacOS() && e.KeyModifiers == KeyModifiers.Meta => "trash",
            Key.Back when e.KeyModifiers == KeyModifiers.None => "parent",
            Key.Tab when command && (e.KeyModifiers & KeyModifiers.Shift) != 0 => "previous-tab",
            Key.Tab when command => "next-tab", Key.Tab when e.KeyModifiers == KeyModifiers.None => "switch-pane",
            Key.B when e.KeyModifiers == (KeyModifiers.Control | KeyModifiers.Shift) => "favorites-bar",
            Key.C when e.KeyModifiers == (KeyModifiers.Control | KeyModifiers.Shift) => "copy-paths",
            Key.C when command => "copy-clipboard", Key.X when command => "cut", Key.V when command => "paste",
            Key.Z when command && (e.KeyModifiers & KeyModifiers.Shift) == 0 => "undo", Key.A when command => "select-all",
            Key.I when e.KeyModifiers == KeyModifiers.Control => "invert",
            Key.S when e.KeyModifiers == KeyModifiers.Control => "select-pattern",
            Key.K when e.KeyModifiers == KeyModifiers.Control => "folder-sizes",
            Key.Y when e.KeyModifiers == KeyModifiers.Control => "compare",
            Key.U when e.KeyModifiers == KeyModifiers.Control => "duplicates",
            Key.T when command => "new-tab", Key.W when command => "close-tab",
            Key.N when e.KeyModifiers == (KeyModifiers.Control | KeyModifiers.Shift) => "copy-names",
            Key.N when OperatingSystem.IsWindows() && e.KeyModifiers == KeyModifiers.Control => "new-folder",
            Key.N when OperatingSystem.IsMacOS() && e.KeyModifiers == (KeyModifiers.Meta | KeyModifiers.Shift) => "new-folder",
            Key.D when e.KeyModifiers == (KeyModifiers.Control | KeyModifiers.Shift) => "details-view",
            Key.H when e.KeyModifiers == (KeyModifiers.Control | KeyModifiers.Shift) => "details-thumbnails",
            Key.M when e.KeyModifiers == (KeyModifiers.Control | KeyModifiers.Shift) => "tiles-view",
            Key.Left when command => "previous-tab", Key.Right when command => "next-tab",
            Key.Left when (e.KeyModifiers & KeyModifiers.Alt) != 0 => "back",
            Key.Right when (e.KeyModifiers & KeyModifiers.Alt) != 0 => "forward",
            Key.Multiply => "focus-filter", Key.D8 when (e.KeyModifiers & KeyModifiers.Shift) != 0 => "focus-filter", Key.Escape => "cancel-work",
            _ => null
        };
        if (action is not null) { e.Handled = true; Dispatch(action); }
    }
    public void Dispatch(string command)
    {
        if (_closingState || _disposing) return;
        _lastCommand = ExecuteCommandGuardedAsync(command);
        if (command != "quit" && !_lastCommand.IsCompleted)
        {
            _pendingCommands.Add(_lastCommand);
            _ = UntrackCommandAsync(_lastCommand);
        }
    }
    private async Task UntrackCommandAsync(Task task)
    {
        try { await task; } finally { _pendingCommands.Remove(task); }
    }
    private void EnsureAcceptingWork()
    {
        if (_closingState || _disposing) throw new InvalidOperationException("The window is closing. New work cannot start.");
    }
    private static void EnsurePaneReady(PaneView pane)
    {
        if (pane.IsLoading) throw new InvalidOperationException("Wait for this folder to finish loading.");
    }
    private async Task ExecuteCommandGuardedAsync(string command)
    {
        try { await ExecuteCommandAsync(command); }
        catch (OperationCanceledException) { _message.Text = "Canceled"; }
        catch (Exception error) { ShowError(error); }
    }
    private void ShowError(Exception error) => _message.Text = $"{error.GetType().Name}: {error.Message}";
    private void Observe(Task task) => _ = ObserveCoreAsync(task);
    private async Task ObserveCoreAsync(Task task)
    {
        try { await task; }
        catch (OperationCanceledException) { }
        catch (Exception error) { ShowError(error); }
    }
    private void ScheduleSave()
    {
        if (_disposing || _suppressScheduledSave) return;
        _saveDebounce.Stop();
        _saveDebounce.Start();
    }
    internal async Task SaveSettingsAsync()
    {
        if (!_store.CanSave) return;
        await _saveGate.WaitAsync();
        try
        {
            _settings.Panes = _panes.Select(x => x.Capture()).ToList();
            _settings.WindowWidth = Width; _settings.WindowHeight = Height;
            await _store.SaveAsync(_settings);
        }
        finally { _saveGate.Release(); }
    }
    private void OnHistoryChanged() => Dispatcher.UIThread.Post(() =>
    {
        if (_disposing) return;
        var canUndo = _operations.CanUndo;
        _undo.IsEnabled = canUndo;
        if (_undoMenuItem is not null) _undoMenuItem.IsEnabled = canUndo;
    });
    private void CancelWork()
    {
        foreach (var job in _jobs.Values) job.Cancellation.Cancel();
        _toolCancellation?.Cancel();
        _message.Text = "Cancellation requested";
    }
    private async void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_allowClose) return;
        e.Cancel = true;
        if (_closingState) return;
        var owned = OwnedWindows.FirstOrDefault(window => window.IsVisible);
        if (owned is not null) { owned.Activate(); _message.Text = "Finish or close the active dialog before closing this window."; return; }
        _closingState = true;
        if (Content is Control content) content.IsEnabled = false;
        try
        {
            if (_jobs.Count > 0 || _toolCancellation is not null || _pendingCommands.Count > 0)
            {
                if (!await Dialogs.ConfirmAsync(this, "Work is still running", "Cancel the active work and close the window?", "Cancel work and close")) { ResetClosing(); return; }
                _shutdownCancellation.Cancel();
                CancelWork();
                await Task.WhenAll(_jobs.Values.Select(x => x.Completion.Task).ToArray());
                try { await _toolTask; } catch (Exception) { }
                await Task.WhenAll(_pendingCommands.ToArray());
            }
            try { await SaveSettingsAsync(); }
            catch (Exception error)
            {
                ShowError(error);
                if (!await Dialogs.ConfirmAsync(this, "Could not save preferences", error.Message + "\n\nFile operations have stopped. Close without saving the latest window settings?", "Close without saving"))
                { ResetClosing(); return; }
            }
            await StopPlatformAsync();
            _allowClose = true; Close();
        }
        catch (Exception error) { ShowError(error); ResetClosing(); }
    }
    private void ResetClosing()
    {
        _closingState = false;
        if (_shutdownCancellation.IsCancellationRequested) { _shutdownCancellation.Dispose(); _shutdownCancellation = new CancellationTokenSource(); }
        if (Content is Control content) content.IsEnabled = true;
    }
    internal async Task CloseForDiagnosticsAsync()
    {
        await SaveSettingsAsync(); await StopPlatformAsync(); _allowClose = true; Close();
    }
    private async Task StopPlatformAsync()
    {
        _shutdownCancellation.Cancel();
        if (_platform is IAsyncDisposable owner) await owner.DisposeAsync();
    }
    private void DisposeServices()
    {
        if (_disposing) return; _disposing = true;
        _saveDebounce.Stop(); CancelWork();
        foreach (var pane in _panes) pane.Dispose();
        _preview.Dispose(); _operations.HistoryChanged -= OnHistoryChanged; _operations.Dispose(); _listary.Dispose();
        _shutdownCancellation.Cancel(); _shutdownCancellation.Dispose();
    }
    private sealed class ActiveJob
    {
        public CancellationTokenSource Cancellation { get; } = new();
        public TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
