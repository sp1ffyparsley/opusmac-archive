using OpusMac.Core.Persistence;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using OpusMac.Core;
using OpusMac.Core.Browsing;
using OpusMac.Core.Search;
using OpusMac.Core.Tools;
using System.Text;
using System.Globalization;

namespace OpusMac.App;

public sealed partial class MainWindow
{
    internal sealed record JoinPartRow(int Number, string Path);

    private async Task JoinFilesAsync(PaneView pane)
    {
        var entries = await ResolveSelectionAsync(pane);
        var sources = new List<(string Path, string Identity)>();
        var sourceDirectory = pane.CurrentDirectory;
        var destinationDirectory = _settings.DualPane ? OtherPane.CurrentDirectory : sourceDirectory;
        var rows = new ListBox { Name = "JoinFileParts", Height = 150 };
        rows.ItemTemplate = new FuncDataTemplate<JoinPartRow>((part, _) =>
        {
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("28,*"), ColumnSpacing = 4 };
            if (part is null) return row;
            row.Children.Add(Ui.Text(part.Number.ToString(CultureInfo.CurrentCulture)));
            var file = Ui.Text(part.Path);
            file.TextTrimming = TextTrimming.LeadingCharacterEllipsis;
            ToolTip.SetTip(file, part.Path);
            Grid.SetColumn(file, 1);
            row.Children.Add(file);
            return row;
        });
        var target = new TextBox { Name = "JoinOutputPath", Watermark = "Output file" };
        var uudecode = new CheckBox { Name = "JoinUuDecode", Content = "UUDecode" };
        var status = Ui.Text("", true);
        status.TextWrapping = TextWrapping.Wrap;
        var panel = new StackPanel { Margin = new Thickness(12), Spacing = 6 };
        var dialog = Dialogs.Window("Join Files", new ScrollViewer { Content = panel }, 535, 340);
        var join = Ui.Button("OK", () => dialog.Close(true));
        var manuallyOrdered = false;
        string? suggestedTarget = null;
        void RefreshRows(int selection = -1)
        {
            rows.ItemsSource = sources.Select((source, index) => new JoinPartRow(index + 1, source.Path)).ToArray();
            rows.SelectedIndex = selection;
            join.IsEnabled = sources.Count >= 2 && !string.IsNullOrWhiteSpace(target.Text);
        }
        void UpdateSuggestedTarget()
        {
            if (!string.IsNullOrWhiteSpace(target.Text) && !string.Equals(target.Text, suggestedTarget, StringComparison.Ordinal)) return;
            suggestedTarget = null;
            if (sources.Count > 0)
            {
                var first = Path.GetFileName(sources[0].Path);
                var extension = Path.GetExtension(first);
                var filename = extension.Length is >= 4 and <= 6 && extension[1..].All(char.IsAsciiDigit)
                    ? first[..^extension.Length] : first + ".joined";
                suggestedTarget = Path.Combine(destinationDirectory, filename);
            }
            target.Text = suggestedTarget;
            target.CaretIndex = suggestedTarget?.Length ?? 0;
        }
        void AddPaths(IEnumerable<string> paths)
        {
            var additions = new List<(string Path, string Identity)>();
            var known = sources.Select(source => source.Path).ToHashSet(
                OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
            foreach (var path in paths)
            {
                if (!known.Add(path)) continue;
                var entry = DirectoryBrowser.ReadEntry(path);
                var identity = ListaryClient.ReadNativeItemIdentity(path);
                if (entry.IsDirectory || entry.IsSymbolicLink || identity is null)
                    throw new IOException($"Choose a regular local file whose identity can be verified: {path}");
                additions.Add((path, identity));
            }
            sources.AddRange(additions.OrderBy(source => Path.GetFileName(source.Path), NaturalNameComparer.Instance)
                .ThenBy(source => source.Path, StringComparer.Ordinal));
            if (!manuallyOrdered)
            {
                // A later batch can contain earlier numbered pieces. Keep the whole automatic order
                // until the user moves a row explicitly.
                sources.Sort((left, right) =>
                {
                    var compared = NaturalNameComparer.Instance.Compare(Path.GetFileName(left.Path), Path.GetFileName(right.Path));
                    return compared != 0 ? compared : StringComparer.Ordinal.Compare(left.Path, right.Path);
                });
                UpdateSuggestedTarget();
            }
            RefreshRows();
        }
        AddPaths(entries.Where(entry => !entry.IsDirectory).Select(entry => entry.FullPath));
        target.TextChanged += (_, _) => join.IsEnabled = sources.Count >= 2 && !string.IsNullOrWhiteSpace(target.Text);
        var add = Ui.AsyncButton("Add…", async () =>
        {
            var files = await dialog.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { Title = "Add files to join", AllowMultiple = true });
            AddPaths(files.Select(file => file.TryGetLocalPath() ?? throw new IOException("Choose local files to join.")));
        }, error => status.Text = error.Message);
        add.IsEnabled = dialog.StorageProvider.CanOpen;
        DragDrop.SetAllowDrop(rows, true);
        rows.AddHandler(DragDrop.DragOverEvent, (_, args) =>
        {
            args.DragEffects = args.DataTransfer.TryGetFiles()?.Any() == true ? DragDropEffects.Copy : DragDropEffects.None;
            args.Handled = true;
        });
        rows.AddHandler(DragDrop.DropEvent, (_, args) =>
        {
            args.Handled = true;
            try
            {
                AddPaths((args.DataTransfer.TryGetFiles() ?? []).Select(file =>
                    file.TryGetLocalPath() ?? throw new IOException("Drop local files to join.")));
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
            { status.Text = error.Message; }
        });
        IPointer? reorderPointer = null;
        Point reorderOrigin = default;
        var reorderSource = -1;
        var reordering = false;
        ListBoxItem? dropTarget = null;
        static ListBoxItem? JoinRow(object? source) =>
            source is ListBoxItem item ? item : (source as Visual)?.FindAncestorOfType<ListBoxItem>();
        void MarkDropTarget(ListBoxItem? item)
        {
            if (ReferenceEquals(dropTarget, item)) return;
            dropTarget?.Classes.Remove("join-drop-target");
            dropTarget = item;
            dropTarget?.Classes.Add("join-drop-target");
        }
        void ResetReorder()
        {
            reorderPointer = null;
            reorderSource = -1;
            reordering = false;
            MarkDropTarget(null);
        }
        rows.AddHandler(InputElement.PointerPressedEvent, (_, args) =>
        {
            if (args.Pointer.Type != PointerType.Mouse || !args.GetCurrentPoint(rows).Properties.IsLeftButtonPressed ||
                JoinRow(args.Source) is not { } row) return;
            var index = rows.IndexFromContainer(row);
            if (index < 0) return;
            reorderPointer = args.Pointer;
            reorderSource = index;
            reorderOrigin = args.GetPosition(rows);
        }, RoutingStrategies.Tunnel);
        rows.AddHandler(InputElement.PointerMovedEvent, (_, args) =>
        {
            if (args.Pointer != reorderPointer) return;
            var point = args.GetCurrentPoint(rows);
            if (!point.Properties.IsLeftButtonPressed) { ResetReorder(); return; }
            if (!reordering)
            {
                var distance = point.Position - reorderOrigin;
                if (distance.X * distance.X + distance.Y * distance.Y < 36) return;
                reordering = true;
                args.Pointer.Capture(rows);
            }
            MarkDropTarget(JoinRow(rows.InputHitTest(point.Position)));
            args.Handled = true;
        }, RoutingStrategies.Tunnel);
        rows.AddHandler(InputElement.PointerReleasedEvent, (_, args) =>
        {
            if (args.Pointer != reorderPointer) return;
            var from = reorderSource;
            var moving = reordering;
            var row = moving ? JoinRow(rows.InputHitTest(args.GetPosition(rows))) : null;
            var insertion = row is null ? -1 : rows.IndexFromContainer(row) +
                (args.GetPosition(row).Y >= row.Bounds.Height / 2 ? 1 : 0);
            ResetReorder();
            if (!moving) return;
            args.Pointer.Capture(null);
            args.Handled = true;
            if (from < 0 || from >= sources.Count || insertion < 0 || insertion > sources.Count) return;
            var destination = insertion > from ? insertion - 1 : insertion;
            if (destination == from) return;
            var source = sources[from];
            sources.RemoveAt(from);
            sources.Insert(destination, source);
            manuallyOrdered = true;
            RefreshRows(destination);
        }, RoutingStrategies.Tunnel);
        rows.PointerCaptureLost += (_, args) =>
        {
            if (args.Pointer == reorderPointer && args.Pointer.Captured != rows) ResetReorder();
        };
        var remove = Ui.Button("Remove", () =>
        {
            if (rows.SelectedIndex < 0) return;
            var index = rows.SelectedIndex;
            sources.RemoveAt(index);
            if (!manuallyOrdered) UpdateSuggestedTarget();
            RefreshRows(Math.Min(index, sources.Count - 1));
        });
        void MovePart(int offset)
        {
            var from = rows.SelectedIndex;
            var to = from + offset;
            if (from < 0 || to < 0 || to >= sources.Count) return;
            (sources[from], sources[to]) = (sources[to], sources[from]);
            manuallyOrdered = true;
            RefreshRows(to);
        }
        var browse = Ui.AsyncButton("Browse…", async () =>
        {
            var picked = await dialog.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Choose joined output file", SuggestedFileName = Path.GetFileName(target.Text)
            });
            if (picked?.TryGetLocalPath() is { } path) target.Text = path;
        }, error => status.Text = error.Message);
        browse.IsEnabled = dialog.StorageProvider.CanSave;
        var moveUp = Ui.Button("Move Up", () => MovePart(-1));
        var moveDown = Ui.Button("Move Down", () => MovePart(1));
        var clear = Ui.Button("Clear", () =>
        {
            sources.Clear();
            manuallyOrdered = false;
            UpdateSuggestedTarget();
            RefreshRows();
        });
        void UpdateListButtons()
        {
            remove.IsEnabled = rows.SelectedIndex >= 0;
            moveUp.IsEnabled = rows.SelectedIndex > 0;
            moveDown.IsEnabled = rows.SelectedIndex >= 0 && rows.SelectedIndex + 1 < sources.Count;
            clear.IsEnabled = sources.Count > 0;
        }
        rows.SelectionChanged += (_, _) => UpdateListButtons();
        rows.PropertyChanged += (_, args) => { if (args.Property == ItemsControl.ItemsSourceProperty) UpdateListButtons(); };
        UpdateListButtons();
        panel.Children.Add(Ui.Text("Join these files:"));
        panel.Children.Add(rows);
        panel.Children.Add(Ui.Row(add, remove, moveUp, moveDown, clear));
        var output = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 6 };
        output.Children.Add(target);
        Grid.SetColumn(browse, 1);
        output.Children.Add(browse);
        panel.Children.Add(Dialogs.Form(("Output File", output)));
        panel.Children.Add(uudecode);
        panel.Children.Add(status);
        var buttons = Ui.Row(join, Ui.Button("Cancel", () => dialog.Close(false)));
        buttons.HorizontalAlignment = HorizontalAlignment.Right;
        panel.Children.Add(buttons);
        if (!await dialog.ShowDialog<bool>(this)) return;
        var path = DirectoryBrowser.ResolvePath(target.Text!, destinationDirectory);
        var parent = Path.GetDirectoryName(path) ?? throw new IOException("Choose an existing output folder.");
        var parentIdentity = ListaryClient.ReadNativeItemIdentity(parent) ?? throw new IOException("The output folder identity cannot be verified.");
        var reviewed = sources.ToArray();
        var decode = uudecode.IsChecked == true;
        var bytes = await RunToolAsync("Joining files", (token, progress) => FileParts.JoinAsync(reviewed, path, parentIdentity, decode, progress, token));
        _message.Text = $"Joined {reviewed.Length:N0} files · {FileEntry.FormatSize(bytes)} · {path}";
        await RefreshPanesAsync();
    }

    private static readonly string[] SplitChunkPresets =
        ["Automatic", "648 MB (CDR)", "698 MB (CDR)", "2 GB (DVD ISO)", "4.7 GB (DVD5 UDF)", "8.5 GB (DVD9 UDF)"];

    private async Task SplitFileAsync(PaneView pane)
    {
        EnsurePaneReady(pane);
        var captured = pane.ActionEntries.Where(entry => !entry.IsDirectory)
            .Select(entry => (Entry: entry, Identity: pane.IndexedIdentity(entry))).ToArray();
        if (captured.Length != 1) throw new InvalidOperationException("Select one file to split.");
        var source = (await ResolveCapturedSelectionAsync(pane, captured)).Single();
        var identity = ListaryClient.ReadNativeItemIdentity(source.FullPath);
        if (source.IsDirectory || source.IsSymbolicLink || identity is null)
            throw new IOException("Choose a regular file whose filesystem identity can be verified.");
        var initial = _settings.DualPane ? OtherPane.CurrentDirectory : Path.GetDirectoryName(source.FullPath)!;
        var folder = new TextBox { Name = "SplitFolder", Text = initial };
        var chunk = new TextBox { Name = "SplitChunkSize", Text = "Automatic" };
        var uuencode = new CheckBox { Name = "SplitUuEncode", Content = "UUEncode" };
        var presetMenu = new MenuFlyout();
        foreach (var size in SplitChunkPresets)
        {
            var preset = new MenuItem { Header = size };
            preset.Click += (_, _) => { chunk.Text = size; chunk.CaretIndex = size.Length; };
            presetMenu.Items.Add(preset);
        }
        var presets = new Button { Name = "SplitChunkPresets", Content = "▾", Flyout = presetMenu };
        Avalonia.Automation.AutomationProperties.SetName(presets, "Chunk size presets");
        var status = Ui.Text("", true);
        status.TextWrapping = TextWrapping.Wrap;
        chunk.TextChanged += (_, _) => status.Text = "";
        folder.TextChanged += (_, _) => status.Text = "";
        var panel = new StackPanel { Margin = new Thickness(12), Spacing = 6 };
        var dialog = Dialogs.Window("Split File", new ScrollViewer { Content = panel }, 490, 225);
        var browse = Ui.AsyncButton("Browse…", async () =>
        {
            var selected = await dialog.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Choose output folder" });
            if (selected.FirstOrDefault()?.TryGetLocalPath() is { } path) folder.Text = path;
        }, error => status.Text = error.Message);
        browse.IsEnabled = dialog.StorageProvider.CanPickFolder;
        static Grid FormRow(Control main, Control trailing)
        {
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 6 };
            row.Children.Add(main);
            Grid.SetColumn(trailing, 1);
            row.Children.Add(trailing);
            return row;
        }
        var filename = new TextBox { Text = source.Name, IsReadOnly = true };
        ToolTip.SetTip(filename, $"{source.FullPath} · {FileEntry.FormatSize(source.Length)}");
        var editableChunk = FormRow(chunk, presets);
        editableChunk.Width = 177;
        panel.Children.Add(Dialogs.Form(("Split file", filename), ("To folder", FormRow(folder, browse)),
            ("Chunk size", Ui.Row(editableChunk, uuencode))));
        panel.Children.Add(status);
        string? destination = null;
        long chunkBytes = 0;
        var accept = Ui.Button("OK", () =>
        {
            try
            {
                destination = DirectoryBrowser.ResolvePath(folder.Text ?? "", initial);
                if (!Directory.Exists(destination)) throw new DirectoryNotFoundException("Choose an existing output folder.");
                chunkBytes = ParseSplitChunkSize(chunk.Text ?? "", destination);
                if (chunkBytes >= source.Length) throw new ArgumentException("Choose a chunk size smaller than the source file.");
                dialog.Close(true);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
            { status.Text = error.Message; }
        });
        accept.IsDefault = true;
        var cancel = Ui.Button("Cancel", () => dialog.Close(false));
        cancel.IsCancel = true;
        var buttons = Ui.Row(accept, cancel);
        buttons.HorizontalAlignment = HorizontalAlignment.Right;
        panel.Children.Add(buttons);
        if (!await dialog.ShowDialog<bool>(this)) return;
        var current = (await ResolveCapturedSelectionAsync(pane, captured)).Single();
        if (!string.Equals(current.FullPath, source.FullPath, StringComparison.Ordinal))
            throw new IOException("The source moved while reviewing the split. Select it again.");
        var parentIdentity = ListaryClient.ReadNativeItemIdentity(destination!) ??
            throw new IOException("Choose an existing output folder whose filesystem identity can be verified.");
        var encode = uuencode.IsChecked == true;
        var count = await RunToolAsync("Splitting file", (token, progress) =>
            FileParts.SplitAsync(source.FullPath, identity, destination!, parentIdentity, chunkBytes, encode, progress, token));
        _message.Text = $"Split {source.Name} into {count:N0} pieces · {destination}";
        await RefreshPanesAsync();
    }

    private static long ParseSplitChunkSize(string text, string destination)
    {
        text = text.Trim();
        var suffix = text.IndexOf(" (", StringComparison.Ordinal);
        if (suffix >= 0 && SplitChunkPresets.Contains(text, StringComparer.OrdinalIgnoreCase))
            text = text[..suffix];
        if (text.Equals("Automatic", StringComparison.OrdinalIgnoreCase))
        {
            var drive = new DriveInfo(destination);
            return drive.DriveType == DriveType.Removable && drive.IsReady ? Math.Max(1, drive.AvailableFreeSpace) : 100L * 1024 * 1024;
        }
        var number = text.TrimEnd('B', 'b', 'K', 'k', 'M', 'm', 'G', 'g', ' ');
        var multiplier = text[number.Length..].Trim().ToUpperInvariant() switch
        {
            "" or "B" => 1L, "KB" => 1024L, "MB" => 1024L * 1024, "GB" => 1024L * 1024 * 1024, _ => 0L
        };
        if (multiplier == 0 || (!decimal.TryParse(number, NumberStyles.AllowDecimalPoint, CultureInfo.CurrentCulture, out var value) &&
            !decimal.TryParse(number, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out value)) ||
            value <= 0 || value > (decimal)long.MaxValue / multiplier || value * multiplier < 1)
            throw new ArgumentException("Enter a positive chunk size in bytes, KB, MB, or GB.");
        return (long)(value * multiplier);
    }

    private async Task ShowFileTypeSummaryAsync(PaneView pane)
    {
        EnsurePaneReady(pane);
        var selected = pane.ActionEntries.ToArray();
        var all = pane.VisibleEntries.ToArray();
        var hasSelectedFiles = selected.Any(entry => !entry.IsDirectory);
        var selectedFiles = new RadioButton { Content = "Selected files", GroupName = "SummaryScope", IsChecked = hasSelectedFiles };
        var allFiles = new RadioButton { Name = "FileTypeSummaryAll", Content = "All files", GroupName = "SummaryScope", IsChecked = !hasSelectedFiles };
        var fileCount = new RadioButton { Content = "File count", GroupName = "SummaryMetric", IsChecked = true };
        var fileSize = new RadioButton { Name = "FileTypeSummarySize", Content = "File size", GroupName = "SummaryMetric" };
        var extensions = new RadioButton { Content = "Extensions", GroupName = "SummaryCategory", IsChecked = true };
        var groups = new RadioButton { Name = "FileTypeSummaryGroups", Content = "Groups", GroupName = "SummaryCategory" };
        var chart = new ContentControl();
        void Render()
        {
            var bySize = fileSize.IsChecked == true;
            chart.Content = FileTypeSummary.Chart(FileTypeSummary.Summarize(allFiles.IsChecked == true ? all : selected,
                bySize, groups.IsChecked == true), bySize, compact: false);
        }
        foreach (var option in new[] { selectedFiles, allFiles, fileCount, fileSize, extensions, groups })
            option.IsCheckedChanged += (_, _) => { if (option.IsChecked == true) Render(); };
        Render();
        var choices = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 22 };
        choices.Children.Add(new StackPanel { Children = { selectedFiles, allFiles } });
        choices.Children.Add(new StackPanel { Children = { fileCount, fileSize } });
        choices.Children.Add(new StackPanel { Children = { extensions, groups } });
        var layout = new StackPanel { Margin = new Thickness(12), Spacing = 10, Children = { choices, chart } };
        var dialog = Dialogs.Window("File Type Summary", new ScrollViewer { Content = layout }, 455, 325);
        var close = Ui.Button("Close", () => dialog.Close());
        close.HorizontalAlignment = HorizontalAlignment.Right;
        layout.Children.Add(close);
        await dialog.ShowDialog(this);
    }

    private async Task PrintFolderAsync()
    {
        var pane = ActivePane;
        EnsurePaneReady(pane);
        var sourceDirectory = pane.CurrentDirectory;
        var source = new TextBox { Name = "ListingFolder", Text = sourceDirectory };
        var filter = new TextBox { Name = "ListingFilter", Watermark = "Filename pattern", MaxLength = 1024 };
        var useFilter = new CheckBox { Name = "ListingUseFilter", Content = "Use filter" };
        var savedFilters = new ComboBox
        {
            Name = "ListingSavedFilters", PlaceholderText = "Saved filters",
            ItemsSource = _settings.SavedFilenameFilters.OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase).ToArray()
        };
        var sizes = new CheckBox { Content = "Calculate sub-folder sizes" };
        var useFlatView = new CheckBox { Name = "ListingUseFlatView", Content = "Flat View" };
        var flat = new ComboBox { Name = "ListingFlatView", ItemsSource = new[] { "Mixed", "Mixed (No Folders)", "Grouped" }, SelectedIndex = 0, IsEnabled = false };
        var fileDestination = new RadioButton { Name = "ListingFileDestination", Content = "File", GroupName = "ListingDestination", IsChecked = true };
        var clipboardDestination = new RadioButton { Name = "ListingClipboardDestination", Content = "Clipboard", GroupName = "ListingDestination" };
        var printerDestination = new RadioButton { Name = "ListingPrinterDestination", Content = "Printer", GroupName = "ListingDestination" };
        var format = new ComboBox { Name = "ListingFormat", ItemsSource = new[] { "Normal", "Comma separated", "Tab separated" }, SelectedIndex = 0 };
        var path = new TextBox { Name = "ListingOutput", Watermark = "Output filename" };
        var headings = new CheckBox { Content = "Include column headings", IsChecked = true };
        var headerFooter = new ComboBox
        {
            Name = "ListingHeaderFooter",
            ItemsSource = new[] { "None", "Header", "Footer", "Header and Footer" },
            SelectedIndex = 0
        };
        const int pixelsPerCharacter = 7;
        static int CharacterWidth(double pixels) => (int)Math.Ceiling(pixels / (8 * pixelsPerCharacter)) * 8;
        var catalog = FileColumnSettings.CreateDefaultLayout().Where(column => column.Id != "Thumbnail").ToArray();
        var defaults = new Dictionary<string, double>
        {
            ["Name"] = 112, ["Size"] = 56, ["Type"] = 84, ["Modified"] = 98,
            ["Attr"] = 48, ["Created"] = 98, ["Path"] = 224
        };
        var current = pane.FileGrid.Columns.OrderBy(column => column.DisplayIndex)
            .Where(column => column.Header?.ToString() is { } id && defaults.ContainsKey(id))
            .ToDictionary(column => column.Header!.ToString()!, column => column.ActualWidth);
        var currentIds = pane.VisibleColumnIds.Where(defaults.ContainsKey).ToArray();
        var currentSort = pane.CurrentSortColumn;
        var currentDescending = pane.CurrentSortDescending;
        var savedFormat = _settings.PrintFolderFormat;
        var printFont = _settings.PrintFolderFont ?? FolderListingPrintFont.Default;
        var savedColumns = savedFormat?.Columns?.Where(column => column is not null && column.Visible && defaults.ContainsKey(column.Id))
            .Select(column => column.Id).Distinct().ToArray();
        var columns = savedColumns is { Length: > 0 } ? savedColumns
            : catalog.Where(column => column.Visible).Select(column => column.Id).ToArray();
        var initial = columns;
        var widths = new Dictionary<string, double>(defaults);
        foreach (var column in savedFormat?.Columns ?? [])
            if (column is not null && widths.ContainsKey(column.Id) && double.IsFinite(column.Width))
                widths[column.Id] = Math.Clamp(column.Width, 42, 1200);
        var initialWidths = new Dictionary<string, double>(widths);
        var initialSort = savedFormat?.SortColumn ?? "Name";
        var initialDescending = savedFormat?.Descending ?? false;
        var sort = initialSort;
        var descending = initialDescending;
        var preview = new DataGrid
        {
            Name = "ListingColumnPreview", Height = 80, IsReadOnly = true, AutoGenerateColumns = false,
            CanUserResizeColumns = true, CanUserReorderColumns = false, CanUserSortColumns = false,
            HeadersVisibility = DataGridHeadersVisibility.Column, ItemsSource = new[] { "Width" }
        };
        var totalWidth = Ui.Text("");
        int PreviewWidth(double pixels) => printerDestination.IsChecked == true
            ? (int)Math.Round(pixels / pixelsPerCharacter, MidpointRounding.AwayFromZero) : CharacterWidth(pixels);
        void ShowColumns()
        {
            preview.Columns.Clear();
            foreach (var id in columns)
            {
                var column = Ui.ReadableColumn<string>(id, _ => PreviewWidth(widths[id]).ToString(), widths[id]);
                column.MinWidth = 42;
                column.MaxWidth = 1200;
                column.PropertyChanged += (_, change) =>
                {
                    if (change.Property.Name != nameof(DataGridColumn.Width)) return;
                    var width = column.Width.Value;
                    if (!double.IsFinite(width) || widths[id] == width) return;
                    widths[id] = width;
                    preview.ItemsSource = new[] { "Width" };
                    totalWidth.Text = $"{columns.Sum(key => PreviewWidth(widths[key]))} characters total";
                };
                preview.Columns.Add(column);
            }
            totalWidth.Text = $"{columns.Sum(key => PreviewWidth(widths[key]))} characters total";
        }
        ShowColumns();
        printerDestination.IsCheckedChanged += (_, _) => ShowColumns();
        var status = Ui.Text("", true);
        status.TextWrapping = TextWrapping.Wrap;
        var panel = new StackPanel { Margin = new Thickness(12), Spacing = 6 };
        var dialog = Dialogs.Window("Print Folder", new ScrollViewer { Content = panel }, 625, 520);
        var browseSource = Ui.AsyncButton("Browse…", async () =>
        {
            var picked = await dialog.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Select folder to print" });
            if (picked.FirstOrDefault()?.TryGetLocalPath() is { } folder) source.Text = folder;
        }, error => status.Text = error.Message);
        var browseDestination = Ui.AsyncButton("Browse…", async () =>
        {
            var types = new FilePickerFileType[]
            {
                new("Text file") { Patterns = ["*.txt"] },
                new("Comma-separated list") { Patterns = ["*.csv"] },
                new("Tab-separated list") { Patterns = ["*.tsv"] }
            };
            var picked = await dialog.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Export folder listing", SuggestedFileName = "Folder Listing", DefaultExtension = "txt",
                FileTypeChoices = types,
                ShowOverwritePrompt = false
            });
            if (picked?.TryGetLocalPath() is { } file) path.Text = file;
        }, error => status.Text = error.Message);
        browseSource.IsEnabled = dialog.StorageProvider.CanPickFolder;
        browseDestination.IsEnabled = dialog.StorageProvider.CanSave;
        static Grid PathRow(TextBox input, Button browse)
        {
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 6 };
            row.Children.Add(input);
            Grid.SetColumn(browse, 1);
            row.Children.Add(browse);
            return row;
        }
        var outputRow = PathRow(path, browseDestination);
        fileDestination.IsCheckedChanged += (_, _) => outputRow.IsEnabled = fileDestination.IsChecked == true;
        clipboardDestination.IsCheckedChanged += (_, _) => format.IsEnabled = clipboardDestination.IsChecked == true;
        format.IsEnabled = false;
        useFlatView.IsCheckedChanged += (_, _) => flat.IsEnabled = useFlatView.IsChecked == true;
        savedFilters.SelectionChanged += (_, _) =>
        {
            if (savedFilters.SelectedItem is SavedFilenameFilter preset) filter.Text = preset.Pattern;
        };
        var defineFilter = Ui.AsyncButton("Define…", async () =>
        {
            var name = new TextBox { Name = "ListingFilterName", MaxLength = 80, Text = (savedFilters.SelectedItem as SavedFilenameFilter)?.Name };
            var pattern = new TextBox { Name = "ListingFilterPattern", MaxLength = 1024, Text = filter.Text };
            var errorText = Ui.Text("", true);
            var editor = new StackPanel { Margin = new Thickness(12), Spacing = 8,
                Children = { Dialogs.Form(("Name", name), ("Filename pattern", pattern)), errorText } };
            var define = Dialogs.Window("Define Filename Filter", editor, 510, 240);
            var selected = savedFilters.SelectedItem as SavedFilenameFilter;
            var remove = Ui.Button("Delete", () => define.Close("delete"));
            remove.IsEnabled = selected is not null;
            var save = Ui.Button("Save", () =>
            {
                if (string.IsNullOrWhiteSpace(name.Text) || string.IsNullOrWhiteSpace(pattern.Text))
                {
                    errorText.Text = "Enter a name and a filename pattern.";
                    return;
                }
                if (_settings.SavedFilenameFilters.Any(item => !Equals(item, selected) &&
                    item.Name.Equals(name.Text.Trim(), StringComparison.OrdinalIgnoreCase)))
                {
                    errorText.Text = "A filter already uses that name.";
                    return;
                }
                if (selected is null && _settings.SavedFilenameFilters.Count >= 200)
                {
                    errorText.Text = "Delete a saved filter before adding another.";
                    return;
                }
                define.Close("save");
            });
            editor.Children.Add(Ui.Row(remove, Ui.Button("Cancel", () => define.Close(null)), save));
            var action = await define.ShowDialog<string?>(dialog);
            if (action is null) return;
            var replacement = action == "save" ? new SavedFilenameFilter(name.Text!.Trim(), pattern.Text!.Trim()) : null;
            if (selected is not null) _settings.SavedFilenameFilters.Remove(selected);
            if (replacement is not null) _settings.SavedFilenameFilters.Add(replacement);
            await SaveSettingsAsync();
            savedFilters.ItemsSource = _settings.SavedFilenameFilters.OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase).ToArray();
            savedFilters.SelectedItem = replacement;
            if (replacement is null) filter.Clear();
        }, error => status.Text = error.Message);
        var filterControls = PathRow(filter, defineFilter);
        var filterOptions = new StackPanel { Spacing = 4, Children = { filterControls, savedFilters } };
        filterOptions.IsEnabled = false;
        useFilter.IsCheckedChanged += (_, _) => filterOptions.IsEnabled = useFilter.IsChecked == true;
        static Grid Section(string title)
        {
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), ColumnSpacing = 6 };
            row.Children.Add(Ui.Text(title));
            var line = new Separator { VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(line, 1);
            row.Children.Add(line);
            return row;
        }
        panel.Children.Add(Section("Source"));
        panel.Children.Add(Dialogs.Form(("Folder", PathRow(source, browseSource)), ("", sizes)));
        panel.Children.Add(Ui.Row(useFlatView, flat));
        var filterRow = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), ColumnSpacing = 6 };
        filterRow.Children.Add(useFilter);
        Grid.SetColumn(filterOptions, 1);
        filterRow.Children.Add(filterOptions);
        panel.Children.Add(filterRow);
        panel.Children.Add(Section("Destination"));
        var macPrinting = OperatingSystem.IsMacOS();
        var fileRow = macPrinting ? 1 : 0;
        var destinations = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*"),
            RowDefinitions = new RowDefinitions(macPrinting ? "Auto,Auto,Auto" : "Auto,Auto"),
            ColumnSpacing = 6, RowSpacing = 4
        };
        if (macPrinting)
        {
            destinations.Children.Add(printerDestination);
            var printLabel = Ui.Text("macOS print panel", true);
            printLabel.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(printLabel, 1);
            destinations.Children.Add(printLabel);
        }
        Grid.SetRow(fileDestination, fileRow);
        destinations.Children.Add(fileDestination);
        Grid.SetRow(outputRow, fileRow);
        Grid.SetColumn(outputRow, 1);
        destinations.Children.Add(outputRow);
        Grid.SetRow(clipboardDestination, fileRow + 1);
        destinations.Children.Add(clipboardDestination);
        Grid.SetRow(format, fileRow + 1);
        Grid.SetColumn(format, 1);
        destinations.Children.Add(format);
        panel.Children.Add(destinations);
        panel.Children.Add(Section("Format"));
        var editFormat = Ui.AsyncButton("Edit…", async () =>
        {
            var order = columns.Concat(catalog.Select(column => column.Id).Except(columns)).ToArray();
            var draft = order.Select(id => new FileColumnSettings { Id = id, Visible = columns.Contains(id), Width = widths[id] }).ToList();
            var rows = new StackPanel { Spacing = 5 };
            var invalidWidths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var sortBy = new ComboBox { Name = "PrintFolderSort", ItemsSource = order, SelectedItem = sort };
            var reverse = new CheckBox { Name = "PrintFolderDescending", Content = "Descending", IsChecked = descending };
            var options = new StackPanel { Margin = new Thickness(16), Spacing = 6 };
            options.Children.Add(Ui.Text("Columns and fixed widths (px)"));
            options.Children.Add(rows);
            options.Children.Add(Dialogs.Form(("Sort by", sortBy)));
            options.Children.Add(reverse);
            var feedback = Ui.Text("", true);
            options.Children.Add(feedback);
            var editor = Dialogs.Window("Print Folder Format", new ScrollViewer { Content = options }, 480, 475);
            var apply = Ui.Button("OK", () => editor.Close(true));
            void UpdateApplyState()
            {
                var hasVisibleColumn = draft.Any(item => item.Visible);
                apply.IsEnabled = hasVisibleColumn && invalidWidths.Count == 0;
                feedback.Text = !hasVisibleColumn ? "Select at least one column."
                    : invalidWidths.Count > 0 ? "Enter widths between 42 and 1200 pixels." : "";
            }
            RenderFolderFormatRows(rows, draft, invalidWidths, UpdateApplyState, print: true);
            options.Children.Add(Ui.Row(Ui.Button("Cancel", () => editor.Close(false)), apply));
            if (!await editor.ShowDialog<bool>(dialog)) return;
            columns = draft.Where(column => column.Visible).Select(column => column.Id).ToArray();
            foreach (var column in draft) widths[column.Id] = column.Width;
            sort = sortBy.SelectedItem as string ?? "Name";
            descending = reverse.IsChecked == true;
            ShowColumns();
        }, error => status.Text = error.Message);
        var formatButtons = new StackPanel { Spacing = 4, Children = { editFormat, Ui.Button("Current", () =>
        {
            columns = currentIds.Length == 0 ? initial : currentIds;
            foreach (var (id, width) in current)
                if (width > 0 && double.IsFinite(width)) widths[id] = Math.Clamp(width, 42, 1200);
            sort = currentSort;
            descending = currentDescending;
            ShowColumns();
        }), Ui.Button("Reset", () =>
        {
            columns = initial;
            widths = new Dictionary<string, double>(initialWidths);
            sort = initialSort;
            descending = initialDescending;
            ShowColumns();
        }) } };
        if (macPrinting)
        {
            var fontButton = Ui.AsyncButton("Font…", async () =>
            {
                const string defaultLabel = "System monospaced";
                var names = MacFolderPrinting.AvailableFontNames();
                var face = new ComboBox { ItemsSource = new[] { defaultLabel }.Concat(names).ToArray() };
                face.SelectedItem = names.Contains(printFont.PostScriptName, StringComparer.Ordinal)
                    ? printFont.PostScriptName : defaultLabel;
                var size = new NumericUpDown
                {
                    Minimum = 6, Maximum = 72, Increment = 1, FormatString = "0.##",
                    Value = (decimal)printFont.PointSize
                };
                var layout = new StackPanel { Margin = new Thickness(16), Spacing = 10 };
                layout.Children.Add(Dialogs.Form(("Font", face), ("Size (pt)", size)));
                var picker = Dialogs.Window("Print Folder Font", layout, 440, 235);
                var accept = Ui.Button("OK", () =>
                {
                    if (size.Value is not { } points) return;
                    var fontName = face.SelectedIndex > 0 ? face.SelectedItem as string : null;
                    picker.Close(new FolderListingPrintFont(fontName, (double)points).Normalize());
                });
                layout.Children.Add(Ui.Row(Ui.Button("Cancel", () => picker.Close(null)), accept));
                if (await picker.ShowDialog<FolderListingPrintFont?>(dialog) is { } selected)
                    printFont = selected;
            }, error => status.Text = error.Message);
            fontButton.IsEnabled = false;
            printerDestination.IsCheckedChanged += (_, _) => fontButton.IsEnabled = printerDestination.IsChecked == true;
            formatButtons.Children.Add(fontButton);
        }
        var previewRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 6 };
        previewRow.Children.Add(new StackPanel { Spacing = 3, Children = { preview, totalWidth } });
        Grid.SetColumn(formatButtons, 1);
        previewRow.Children.Add(formatButtons);
        panel.Children.Add(previewRow);
        panel.Children.Add(Dialogs.Form(("Header/Footer", headerFooter)));
        panel.Children.Add(headings);
        panel.Children.Add(status);
        var submit = Ui.Button("Export", () =>
        {
            if (string.IsNullOrWhiteSpace(source.Text) ||
                (fileDestination.IsChecked == true && string.IsNullOrWhiteSpace(path.Text)))
            {
                status.Text = "Enter the source folder and a filename for file output.";
                return;
            }
            dialog.Close(true);
        });
        printerDestination.IsCheckedChanged += (_, _) => submit.Content = printerDestination.IsChecked == true ? "Print…" : "Export";
        var buttons = Ui.Row(Ui.Button("Cancel", () => dialog.Close(false)), submit);
        buttons.HorizontalAlignment = HorizontalAlignment.Right;
        panel.Children.Add(buttons);
        if (!await dialog.ShowDialog<bool>(this)) return;

        var folder = DirectoryBrowser.ResolvePath(source.Text!, sourceDirectory);
        var printing = printerDestination.IsChecked == true;
        var target = fileDestination.IsChecked == true ? DirectoryBrowser.ResolvePath(path.Text!, folder) : null;
        var mode = useFlatView.IsChecked == true ? flat.SelectedIndex + 1 : 0;
        var pattern = useFilter.IsChecked == true ? filter.Text ?? "" : "";
        var includeSizes = sizes.IsChecked == true;
        var includeHeadings = headings.IsChecked == true;
        var includeHeader = headerFooter.SelectedIndex is 1 or 3;
        var includeFooter = headerFooter.SelectedIndex is 2 or 3;
        var showHidden = _settings.ShowHidden;
        var separator = (printing ? 0 : target is null ? format.SelectedIndex : Path.GetExtension(target).ToLowerInvariant() switch
        {
            ".csv" => 1, ".tsv" => 2, _ => 0
        }) switch { 1 => ",", 2 => "\t", _ => "  " };
        var clipboardGeneration = clipboardDestination.IsChecked == true ? ++_clipboardRequestGeneration : 0;
        var followLinks = !_settings.IgnoreLinksBelowSizedFolders;
        var characterWidths = columns.Select(id => CharacterWidth(widths[id])).ToArray();
        var printWidths = printing ? columns.Select(id => widths[id] / pixelsPerCharacter).ToArray() : [];
        var documentLabel = $"{folder}  {DateTime.Now:g}";
        var result = await RunToolAsync(printing ? "Preparing folder listing for printing" : "Exporting folder listing", async (token, progress) =>
        {
            var listing = await _browser.ReadAsync(folder, new BrowseOptions(showHidden, mode != 0, pattern), cancellationToken: token)
                .ConfigureAwait(false);
            if (listing.Truncated || listing.Errors.Count != 0)
                throw new IOException(listing.Errors.FirstOrDefault() ?? "The folder listing exceeded the scan limit.");
            var entries = listing.Entries.Where(entry => mode != 2 || !entry.IsDirectory).ToArray();
            if (includeSizes)
            {
                var folders = entries.Where(entry => entry.IsDirectory).ToArray();
                var measured = DirectoryBrowser.CalculateFolderSizes(folders, token, count =>
                    progress.Report(new ToolProgress("Calculating folder sizes", count, folders.Length)), followLinks);
                if (measured.Errors.Count != 0) throw new IOException(measured.Errors[0]);
                entries = entries.Select(entry => measured.Sizes.TryGetValue(entry.FullPath, out var bytes)
                    ? entry with { CalculatedFolderLength = bytes } : entry).ToArray();
            }
            var ordered = DirectoryBrowser.SortEntries(entries, sort, descending);
            if (mode == 3)
            {
                var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
                var byParent = ordered.ToLookup(entry => Path.GetDirectoryName(entry.FullPath), comparer);
                var orderedDirectories = ordered.Where(entry => entry.IsDirectory)
                    .Select(entry => entry.FullPath).ToHashSet(comparer);
                var pending = new Stack<FileEntry>(ordered.Where(entry =>
                    !orderedDirectories.Contains(Path.GetDirectoryName(entry.FullPath)!)).Reverse());
                var grouped = new List<FileEntry>(ordered.Count);
                while (pending.TryPop(out var entry))
                {
                    token.ThrowIfCancellationRequested();
                    grouped.Add(entry);
                    if (entry.IsDirectory)
                        foreach (var child in byParent[entry.FullPath].Reverse()) pending.Push(child);
                }
                ordered = grouped;
            }
            var output = new StringBuilder();
            void AppendRow(IEnumerable<string> fields)
            {
                if (printing)
                {
                    output.AppendJoin('\t', fields.Select(field => EscapeListingField(field, "  "))).Append("\r\n");
                    return;
                }
                if (separator != "  ")
                {
                    output.AppendJoin(separator, fields.Select(field => EscapeListingField(field, separator))).Append("\r\n");
                    return;
                }
                var cells = fields.ToArray();
                for (var index = 0; index < cells.Length; index++)
                {
                    var field = EscapeListingField(cells[index], separator);
                    output.Append(field);
                    if (index < cells.Length - 1)
                    {
                        var width = new System.Globalization.StringInfo(field).LengthInTextElements;
                        output.Append(' ', Math.Max(1, characterWidths[index] - width));
                    }
                }
                output.Append("\r\n");
            }
            if (includeHeader && !printing) AppendRow([documentLabel]);
            if (includeHeadings) AppendRow(columns);
            foreach (var entry in ordered)
            {
                token.ThrowIfCancellationRequested();
                AppendRow(columns.Select(column => column switch
                {
                    "Name" => mode == 3 ? new string(' ', 2 * (Path.GetRelativePath(folder, entry.FullPath)
                        .Count(character => character == Path.DirectorySeparatorChar))) + entry.Name : entry.Name,
                    "Size" => entry.SizeText,
                    "Type" => entry.Kind,
                    "Modified" => PaneView.FormatFileDate(entry.ModifiedUtc),
                    "Created" => PaneView.FormatFileDate(entry.CreatedUtc),
                    "Attr" => PaneView.FormatAttributes(entry.Attributes),
                    "Path" => entry.FullPath,
                    _ => ""
                }));
            }
            if (includeFooter && !printing) AppendRow([documentLabel]);
            var content = output.ToString();
            if (target is not null)
            {
                var temporary = Path.Combine(Path.GetDirectoryName(target)!, Path.GetRandomFileName());
                var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                var committed = false;
                try
                {
                    await using (stream)
                    await using (var writer = new StreamWriter(stream))
                        await writer.WriteAsync(content.AsMemory(), token).ConfigureAwait(false);
                    token.ThrowIfCancellationRequested();
                    File.Move(temporary, target);
                    committed = true;
                }
                finally { if (!committed) File.Delete(temporary); }
            }
            return (Content: content, Count: ordered.Count);
        });
        if (printing)
        {
            EnsureAcceptingWork();
            if (!MacFolderPrinting.Print(folder, result.Content, printFont, printWidths,
                includeHeader ? documentLabel : "", includeFooter ? documentLabel : ""))
            {
                _message.Text = "The macOS print operation did not complete.";
                return;
            }
            _settings.PrintFolderFont = printFont;
        }
        else if (target is null)
        {
            await _clipboardPublishGate.WaitAsync(_shutdownCancellation.Token);
            try
            {
                EnsureAcceptingWork();
                if (clipboardGeneration != _clipboardRequestGeneration) return;
                await RequireClipboard().SetTextAsync(result.Content);
                _clipboardPaths.Clear(); _clipboardIndexed = []; _clipboardCut = false; _clipboardTransfer = null;
            }
            finally { _clipboardPublishGate.Release(); }
        }
        _settings.PrintFolderFormat = new LockedFolderFormat("Details", sort, descending,
            columns.Select(id => new FileColumnSettings { Id = id, Width = widths[id] }).ToList());
        await SaveSettingsAsync();
        _message.Text = printing ? $"macOS print operation completed · {result.Count:N0} items"
            : target is null ? $"Copied folder listing · {result.Count:N0} items"
            : $"Exported {result.Count:N0} items · {target}";
    }

    private static string EscapeListingField(string field, string separator)
    {
        if (separator == "  ") return field.Replace("\r", "\\r").Replace("\n", "\\n").Replace("\t", "\\t");
        return field.Contains(separator, StringComparison.Ordinal) || field.IndexOfAny(['"', '\r', '\n']) >= 0
            ? "\"" + field.Replace("\"", "\"\"") + "\""
            : field;
    }

    private async Task AdvancedRenameAsync()
    {
        var pane = ActivePane;
        var selected = pane.ActionEntries
            .Select(entry => (Entry: entry, Identity: pane.IndexedIdentity(entry)))
            .ToArray();
        if (selected.Length == 0) throw new InvalidOperationException("Select a file or folder first.");
        async Task<FileEntry[]> ResolveSourceEntriesAsync() =>
            (await ResolveCapturedSelectionAsync(pane, selected)).ToArray();
        var sources = (await ResolveSourceEntriesAsync()).Select(entry => entry.FullPath).ToArray();
        var pathComparer = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;
        var find = new TextBox { Name = "RenameFind" }; var replace = new TextBox { Name = "RenameReplace" }; var prefix = new TextBox { Name = "RenamePrefix" }; var suffix = new TextBox { Name = "RenameSuffix" };
        var regex = new CheckBox { Content = "Regular expression" };
        var ignoreCase = new CheckBox { Content = "Ignore case" };
        var extension = new CheckBox { Content = "Include extension" };
        var casing = new ComboBox { ItemsSource = Enum.GetValues<RenameCase>(), SelectedIndex = 0 };
        var numbering = new TextBox { Watermark = "Off (or first number)" };
        var digits = new TextBox { Text = "2" };
        var rows = Ui.Grid(("Current name", nameof(RenameRow.OldName), -1), ("New name", nameof(RenameRow.NewName), -1), ("Source folder", nameof(RenameRow.Folder), -1), ("Issue", nameof(RenameRow.Issue), -1));
        rows.Columns[0] = Ui.ReadableColumn<RenameRow>("Current name", row => row.OldName);
        rows.Columns[1] = Ui.ReadableColumn<RenameRow>("New name", row => row.NewName);
        rows.Columns[2] = Ui.ReadableColumn<RenameRow>("Source folder", row => row.Folder);
        var feedback = Ui.Text("Preview the exact names before applying.", true); feedback.TextWrapping = TextWrapping.Wrap;
        var form = Dialogs.Form(("Find", find), ("Replace", replace), ("Prefix", prefix), ("Suffix", suffix), ("Case", casing), ("Number from", numbering), ("Number digits", digits));
        var options = new StackPanel { Spacing = 8, Children = { form, Ui.Row(regex, ignoreCase, extension) } };
        var grid = new Grid { Margin = new Thickness(16), RowDefinitions = new RowDefinitions("Auto,*,Auto,Auto"), RowSpacing = 12 };
        grid.Children.Add(options); Grid.SetRow(rows, 1); grid.Children.Add(rows);
        Grid.SetRow(feedback, 2); grid.Children.Add(feedback);
        var dialog = Dialogs.Window("Advanced Rename", grid, 880, 680);
        RenamePlan? plan = null;
        FileEntry[]? previewEntries = null;
        string?[]? previewNativeIds = null;
        var busy = false;
        var apply = Ui.Button("Apply rename", () => { }); apply.IsEnabled = false;
        var preview = Ui.Button("Preview", () => { });
        var buttons = Ui.Row(Ui.Button("Cancel", () => { if (busy) CancelWork(); else dialog.Close(); }), preview, apply);
        dialog.Closing += (_, e) => { if (busy) { e.Cancel = true; CancelWork(); feedback.Text = "Canceling rename; waiting for the operation to finish…"; } };
        buttons.HorizontalAlignment = HorizontalAlignment.Right;
        Grid.SetRow(buttons, 3); grid.Children.Add(buttons);
        void Invalidate()
        {
            plan = null; previewEntries = null; previewNativeIds = null;
            apply.IsEnabled = false; feedback.Text = "Options changed. Preview to verify new names.";
        }
        foreach (var box in new[] { find, replace, prefix, suffix, numbering, digits }) box.TextChanged += (_, _) => Invalidate();
        foreach (var box in new[] { regex, ignoreCase, extension }) box.IsCheckedChanged += (_, _) => Invalidate();
        casing.SelectionChanged += (_, _) => Invalidate();
        preview.Click += async (_, _) =>
        {
            preview.IsEnabled = false; apply.IsEnabled = false; options.IsEnabled = false;
            try
            {
                plan = null; previewEntries = null; previewNativeIds = null;
                var currentEntries = await ResolveSourceEntriesAsync();
                var currentSources = currentEntries.Select(entry => entry.FullPath).ToArray();
                var nativeIds = currentEntries.Select(entry => ListaryClient.ReadNativeItemIdentity(entry.FullPath)).ToArray();
                if (nativeIds.Any(string.IsNullOrWhiteSpace))
                    throw new IOException("The selected source identity cannot be verified. Refresh the folder before previewing again.");
                int? first = string.IsNullOrWhiteSpace(numbering.Text) ? null : int.Parse(numbering.Text);
                var option = new RenameOptions(find.Text ?? "", replace.Text ?? "", prefix.Text ?? "", suffix.Text ?? "",
                    regex.IsChecked == true, ignoreCase.IsChecked == true, extension.IsChecked == true,
                    casing.SelectedItem is RenameCase selected ? selected : RenameCase.Unchanged,
                    first, 1, int.Parse(digits.Text ?? "0"));
                plan = await Task.Run(() => new BatchRenamePlanner().CreatePlan(currentSources, option));
                previewEntries = currentEntries;
                previewNativeIds = nativeIds;
                rows.ItemsSource = plan.Items.Select(x => new RenameRow(x.OriginalName, x.NewName, Path.GetDirectoryName(x.Source) ?? "", string.Join("; ", x.Errors))).ToArray();
                apply.IsEnabled = plan.CanExecute;
                feedback.Text = !plan.IsValid ? string.Join("; ", plan.Errors.Concat(plan.Items.SelectMany(x => x.Errors)).Take(4))
                    : plan.CanExecute ? $"{plan.Items.Count(x => x.Changed):N0} item(s) will be renamed." : "No names would change.";
            }
            catch (Exception error) { feedback.Text = error.Message; }
            finally { preview.IsEnabled = true; options.IsEnabled = true; }
        };
        apply.Click += async (_, _) =>
        {
            if (plan?.CanExecute != true) return;
            busy = true; apply.IsEnabled = false; preview.IsEnabled = false; options.IsEnabled = false;
            try
            {
                var currentEntries = await ResolveSourceEntriesAsync();
                var currentSources = currentEntries.Select(entry => entry.FullPath).ToArray();
                var previewSources = plan.Items.Select(item => item.Source).ToArray();
                if (previewSources.Length != currentSources.Length ||
                    !previewSources.Zip(currentSources).All(pair => pathComparer.Equals(pair.First, pair.Second)))
                {
                    plan = null; previewEntries = null; previewNativeIds = null;
                    feedback.Text = "The selected item location changed after preview. Preview again before applying.";
                    return;
                }
                // The ordinary lister has no Listary indexed identity. Bind
                // each previewed file to its actual native object, not merely
                // its pathname, size and timestamp. Apply must refuse the
                // entire batch if even one source was replaced after review.
                if (previewEntries is null || previewNativeIds is null ||
                    currentEntries.Length != previewEntries.Length || currentEntries.Length != previewNativeIds.Length ||
                    currentEntries.Where((entry, index) =>
                        entry.IsDirectory != previewEntries[index].IsDirectory ||
                        entry.IsSymbolicLink != previewEntries[index].IsSymbolicLink ||
                        entry.Length != previewEntries[index].Length ||
                        entry.ModifiedUtc != previewEntries[index].ModifiedUtc ||
                        !string.Equals(ListaryClient.ReadNativeItemIdentity(entry.FullPath), previewNativeIds[index], StringComparison.Ordinal)).Any())
                {
                    plan = null; previewEntries = null; previewNativeIds = null;
                    feedback.Text = "The selected source changed since preview. Refresh the folder and preview again before applying.";
                    return;
                }
                var result = await RunOperationAsync(plan.ToRequest());
                if (result.Succeeded) { busy = false; dialog.Close(); }
                else feedback.Text = result.Canceled ? "Rename canceled; recorded changes are available in History." : string.Join("; ", result.Errors);
            }
            catch (Exception error) { feedback.Text = error.Message; }
            finally { busy = false; preview.IsEnabled = true; options.IsEnabled = true; }
        };
        rows.ItemsSource = sources.Select(path => new RenameRow(Path.GetFileName(path), Path.GetFileName(path), Path.GetDirectoryName(path) ?? "", "")).ToArray();
        await dialog.ShowDialog(this);
    }

    private async Task CreateArchiveAsync()
    {
        var pane = ActivePane;
        var directory = pane.CurrentDirectory;
        var selected = pane.ActionEntries
            .Select(entry => (Entry: entry, Identity: pane.IndexedIdentity(entry)))
            .ToArray();
        if (selected.Length == 0) throw new InvalidOperationException("Select a file or folder first.");
        async Task<FileEntry[]> ResolveSourceEntriesAsync() =>
            (await ResolveCapturedSelectionAsync(pane, selected)).ToArray();
        var initialEntries = await ResolveSourceEntriesAsync();
        var sources = initialEntries.Select(entry => entry.FullPath).ToArray();
        // An ordinary-folder selection has no indexed Listary identity. Bind
        // the actual OS object before showing Save: path, byte count and mtime
        // alone cannot detect an equal-sized replacement at the same path.
        var originalIds = initialEntries.Select(entry => ListaryClient.ReadNativeItemIdentity(entry.FullPath)).ToArray();
        if (originalIds.Any(string.IsNullOrWhiteSpace))
            throw new IOException("The selected source identity cannot be verified; refresh the folder before creating a ZIP.");
        var pathComparer = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;
        var initial = Path.Combine(directory, sources.Length == 1 ? Path.GetFileNameWithoutExtension(sources[0]) + ".zip" : "Archive.zip");
        string? target;
        if (StorageProvider.CanSave)
        {
            // Prefer the platform Save dialog for an ordinary user interaction.
            // Unlike a text-only path prompt it lets the user browse destinations
            // without granting search/index roots or modifying their selection.
            IStorageFolder? startingFolder = null;
            try
            {
                startingFolder = await StorageProvider.TryGetFolderFromPathAsync(
                    new Uri(Path.GetFullPath(directory) + Path.DirectorySeparatorChar));
            }
            catch (Exception error) when (error is ArgumentException or IOException or NotSupportedException or UnauthorizedAccessException)
            {
                // A missing or unavailable folder is only a placement hint.
                // The native picker still lets the user choose a valid location.
            }
            var chosen = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Create ZIP",
                SuggestedFileName = Path.GetFileName(initial),
                SuggestedStartLocation = startingFolder,
                DefaultExtension = "zip",
                FileTypeChoices = [new FilePickerFileType("ZIP archive") { Patterns = ["*.zip"] }],
                ShowOverwritePrompt = true
            });
            if (chosen is null) return;
            target = chosen.TryGetLocalPath()
                ?? throw new IOException("Choose a local filesystem destination for the ZIP archive.");
        }
        else
        {
            // A headless/unsupported storage provider has no native Save UI.
            // Keep the existing explicit path workflow rather than silently
            // failing or manufacturing a destination in unattended diagnostics.
            target = await Dialogs.PromptAsync(this, "Create ZIP", "New ZIP path", initial, "Create ZIP");
        }
        if (target is null) return;
        var currentEntries = await ResolveSourceEntriesAsync();
        var currentSources = currentEntries.Select(entry => entry.FullPath).ToArray();
        if (sources.Length != currentSources.Length ||
            !sources.Zip(currentSources).All(pair => pathComparer.Equals(pair.First, pair.Second)))
            throw new IOException("The selected item location changed while Create ZIP was being confirmed. Run the operation again.");
        for (var index = 0; index < currentEntries.Length; index++)
        {
            var before = initialEntries[index];
            var after = currentEntries[index];
            if (after.IsDirectory != before.IsDirectory || after.IsSymbolicLink != before.IsSymbolicLink ||
                after.Length != before.Length || after.ModifiedUtc != before.ModifiedUtc ||
                !string.Equals(originalIds[index], ListaryClient.ReadNativeItemIdentity(after.FullPath), StringComparison.Ordinal))
                throw new IOException("The selected source changed during the Save dialog. Refresh the folder and select it again before creating a ZIP.");
        }
        target = DirectoryBrowser.ResolvePath(target, directory);
        var targetParent = Path.GetDirectoryName(Path.GetFullPath(target))
            ?? throw new IOException("Choose an existing destination folder for the ZIP archive.");
        var expectedDestinationParentIdentity = ListaryClient.ReadNativeItemIdentity(targetParent);
        if (string.IsNullOrWhiteSpace(expectedDestinationParentIdentity))
            throw new IOException("The destination folder's native identity cannot be verified. Choose another folder before creating the ZIP.");
        var result = await RunToolAsync("Creating ZIP", (token, progress) => new ArchiveService().CreateZipAsync(
            currentSources, target, progress, token, expectedDestinationParentIdentity));
        await RefreshPanesAsync(afterFileOperation: true); pane.SelectPaths([result.Destination]);
        _message.Text = $"ZIP created · {result.EntryCount:N0} entries · {FileEntry.FormatSize(result.Bytes)} · {result.Destination}";
    }

    private async Task BrowseArchiveAsync(FileEntry? archiveEntry = null, ListarySearchResult? archiveIdentity = null)
    {
        var pane = ActivePane;
        var destinationAtStart = _settings.DualPane ? OtherPane.CurrentDirectory : ActivePane.CurrentDirectory;
        if (archiveEntry is null)
        {
            var displayed = pane.ActionEntries.ToArray();
            if (displayed.Length != 1 || !displayed[0].Extension.Equals(".zip", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Select one ZIP archive to browse or extract.");
            archiveIdentity = pane.IndexedIdentity(displayed[0]);
            archiveEntry = (await ResolveCapturedSelectionAsync(pane, [(displayed[0], archiveIdentity)])).Single();
        }
        else
            archiveEntry = (await ResolveCapturedSelectionAsync(pane, [(archiveEntry, archiveIdentity)])).Single();
        if (!archiveEntry.Extension.Equals(".zip", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Select one ZIP archive to browse or extract.");
        var reviewedArchiveEntry = archiveEntry ?? throw new InvalidOperationException("Select one ZIP archive to browse or extract.");
        var reviewedArchive = reviewedArchiveEntry.FullPath;
        var reviewedArchiveNativeId = ListaryClient.ReadNativeItemIdentity(reviewedArchive);
        if (string.IsNullOrWhiteSpace(reviewedArchiveNativeId))
            throw new IOException("The selected archive identity cannot be verified. Refresh the folder and reopen the archive before extracting.");
        var capturedArchive = (Entry: archiveEntry, Identity: archiveIdentity);
        var pathComparer = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;
        async Task<string> ResolveReviewedArchiveAsync()
        {
            var current = (await ResolveCapturedSelectionAsync(pane, [capturedArchive])).Single();
            if (!pathComparer.Equals(reviewedArchive, current.FullPath))
                throw new IOException("The selected archive location changed while extraction was being reviewed. Reopen the archive before extracting.");
            if (current.IsDirectory || current.IsSymbolicLink ||
                current.Length != reviewedArchiveEntry.Length ||
                current.ModifiedUtc != reviewedArchiveEntry.ModifiedUtc ||
                !string.Equals(reviewedArchiveNativeId, ListaryClient.ReadNativeItemIdentity(current.FullPath), StringComparison.Ordinal))
                throw new IOException("The selected archive changed since its contents were reviewed. Reopen the archive before extracting.");
            return current.FullPath;
        }
        var service = new ArchiveService();
        var listing = await RunToolAsync("Reading ZIP directory", (token, _) => service.ListAsync(reviewedArchive, cancellationToken: token));
        _ = await ResolveReviewedArchiveAsync();
        var rows = Ui.Grid(("Name", nameof(ArchiveEntryInfo.Name), -1), ("Size", nameof(ArchiveEntryInfo.Length), 110), ("Compressed", nameof(ArchiveEntryInfo.CompressedLength), 110), ("Modified", nameof(ArchiveEntryInfo.ModifiedUtc), 180));
        rows.Columns[0] = Ui.ReadableColumn<ArchiveEntryInfo>("Name", row => row.Name);
        rows.ItemsSource = listing.Entries;
        var destination = new TextBox { Text = Path.Combine(destinationAtStart, Path.GetFileNameWithoutExtension(reviewedArchive)) };
        var message = Ui.Text($"{listing.Entries.Count:N0} entries · {FileEntry.FormatSize(listing.TotalUncompressedBytes)} unpacked · extraction creates a new folder.", true);
        message.TextWrapping = TextWrapping.Wrap;
        var grid = new Grid { Margin = new Thickness(14), RowDefinitions = new RowDefinitions("Auto,*,Auto,Auto"), RowSpacing = 10 };
        grid.Children.Add(message); Grid.SetRow(rows, 1); grid.Children.Add(rows);
        var form = Dialogs.Form(("Extract to new folder", destination)); Grid.SetRow(form, 2); grid.Children.Add(form);
        var dialog = Dialogs.Window("ZIP — " + Path.GetFileName(reviewedArchive), grid, 890, 600);
        var extract = Ui.Button("Extract all", () => { });
        var cancel = Ui.Button("Cancel extraction", CancelWork); cancel.IsEnabled = false;
        var close = Ui.Button("Close", () => dialog.Close());
        var buttons = Ui.Row(close, cancel, extract); buttons.HorizontalAlignment = HorizontalAlignment.Right;
        Grid.SetRow(buttons, 3); grid.Children.Add(buttons);
        var busy = false;
        dialog.Closing += (_, e) => { if (busy) { e.Cancel = true; CancelWork(); message.Text = "Canceling extraction; waiting for cleanup…"; } };
        extract.Click += async (_, _) =>
        {
            if (busy) return;
            busy = true; extract.IsEnabled = false; destination.IsEnabled = false; cancel.IsEnabled = true;
            try
            {
                var currentArchive = await ResolveReviewedArchiveAsync();
                var path = DirectoryBrowser.ResolvePath(destination.Text ?? "", destinationAtStart);
                var destinationParent = Path.GetDirectoryName(Path.GetFullPath(path))
                    ?? throw new IOException("Choose an existing destination folder for extraction.");
                var expectedDestinationParentIdentity = ListaryClient.ReadNativeItemIdentity(destinationParent);
                if (string.IsNullOrWhiteSpace(expectedDestinationParentIdentity))
                    throw new IOException("The destination folder's native identity cannot be verified. Choose an existing folder before extracting.");
                var result = await RunToolAsync("Extracting ZIP", (token, progress) => service.ExtractZipAsync(
                    currentArchive, path, progress: progress, cancellationToken: token,
                    expectedArchiveIdentity: reviewedArchiveNativeId, expectedDestinationParentIdentity: expectedDestinationParentIdentity));
                await RefreshPanesAsync(afterFileOperation: true);
                message.Text = $"Extracted {result.EntryCount:N0} entries to {result.Destination}";
            }
            catch (OperationCanceledException) { message.Text = "Extraction canceled."; }
            catch (Exception error) { message.Text = error.Message; }
            finally { busy = false; extract.IsEnabled = true; destination.IsEnabled = true; cancel.IsEnabled = false; }
        };
        await dialog.ShowDialog(this);
    }

    private readonly Border _duplicatePanel = new() { Name = "DuplicateUtilityPanel", IsVisible = false };
    private Func<Task>? _scanDuplicates;
    private PaneView? _duplicatePanelPane;

    private void ShowDuplicatePanel()
    {
        if (!_duplicatePanel.IsEnabled) return;
        var pane = ActivePane;
        _duplicatePanelPane = pane;
        var initial = pane.CurrentDirectory;
        var rootInput = new TextBox { Name = "DuplicateScanFolder", Text = initial, AcceptsReturn = true, Height = 76,
            Watermark = "One folder per line" };
        var referenceInput = new TextBox { Name = "DuplicateReferenceFiles", AcceptsReturn = true, Height = 76,
            Watermark = "Optional. One reference file per line." };
        var modes = new[] { "MD5 checksum", "Filename only", "Filename (no extension)", "Filename and size", "Size" };
        DuplicateComparison[] comparisons = [DuplicateComparison.Md5Checksum, DuplicateComparison.Filename,
            DuplicateComparison.FilenameStem, DuplicateComparison.FilenameAndSize, DuplicateComparison.Size];
        var modeInput = new ComboBox { Name = "DuplicateComparisonMode", ItemsSource = modes, SelectedIndex = 0 };
        var minimumInput = new TextBox { Name = "DuplicateMinimumSize", Watermark = "Default", MaxLength = 20 };
        var filenameFilter = new TextBox { Name = "DuplicateFilenameFilter", Watermark = "Filename pattern", MaxLength = 1024 };
        var excludeLocations = new TextBox { Name = "DuplicateExcludedLocations", Watermark = "One name, path, or pattern per line",
            AcceptsReturn = true, Height = 48, MaxLength = 4096 };
        var savedFilters = new ComboBox
        {
            Name = "DuplicateSavedFilters", PlaceholderText = "Saved filters",
            ItemsSource = _settings.SavedFilenameFilters.OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase).ToArray()
        };
        savedFilters.SelectionChanged += (_, _) =>
        {
            if (savedFilters.SelectedItem is SavedFilenameFilter preset) filenameFilter.Text = preset.Pattern;
        };
        var accuracy = new Slider { Name = "DuplicateMd5Accuracy", Minimum = 1, Maximum = 100, Value = 100,
            TickFrequency = 1, IsSnapToTickEnabled = true, SmallChange = 1, LargeChange = 10 };
        Avalonia.Automation.AutomationProperties.SetName(accuracy, "MD5 accuracy");
        var percent = Ui.Text("100%");
        accuracy.ValueChanged += (_, _) => percent.Text = $"{accuracy.Value:0}%";
        var accuracyRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 6 };
        accuracyRow.Children.Add(accuracy);
        Grid.SetColumn(percent, 1); accuracyRow.Children.Add(percent);
        modeInput.SelectionChanged += (_, _) => accuracyRow.IsEnabled = modeInput.SelectedIndex == 0;
        var recursive = new CheckBox { Name = "DuplicateSearchSubfolders", Content = "Search inside subfolders", IsChecked = true };
        var ignoreOffline = new CheckBox { Name = "DuplicateIgnoreOffline", Content = "Ignore offline files", IsChecked = true };
        var ignoreHardLinks = new CheckBox { Name = "DuplicateIgnoreHardLinks", Content = "Ignore hard links" };
        var numberGroups = new CheckBox { Name = "DuplicateNumberGroups", Content = "Number groups" };
        var resultsPane = new ComboBox { Name = "DuplicateResultsPane",
            ItemsSource = new[] { "Source file display", "Destination file display" }, SelectedIndex = 0 };
        var resultsTab = new CheckBox { Name = "DuplicateResultsNewTab", Content = "New tab" };
        var feedback = Ui.Text("", true);
        feedback.Name = "DuplicateScanFeedback";
        DuplicateResult? scanResult = null;
        var details = Ui.AsyncButton("Scan details", () => Dialogs.MessageAsync(this, "Duplicate scan details",
            string.Join("\n", scanResult!.Errors.Concat(scanResult.SkippedPaths.Select(path => "Skipped: " + path)))),
            error => feedback.Text = error.Message);
        var select = Ui.Button("Select…", () => Dispatch("duplicate-panel-select"));
        details.IsEnabled = select.IsEnabled = false;
        feedback.TextWrapping = TextWrapping.Wrap;
        var prompt = new StackPanel { Margin = new Thickness(8), Spacing = 6 };
        var panel = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*"), ColumnSpacing = 12 };
        var locations = new StackPanel { Spacing = 4 };
        var options = new StackPanel { Spacing = 4 };
        panel.Children.Add(locations);
        Grid.SetColumn(options, 1); panel.Children.Add(options);
        static void AppendPaths(TextBox input, IEnumerable<IStorageItem> items)
        {
            var paths = items.Select(item => item.TryGetLocalPath() ??
                throw new IOException("Choose local files or folders for the duplicate scan.")).ToArray();
            if (paths.Length == 0) return;
            var existing = (input.Text ?? "").Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
            input.Text = string.Join(Environment.NewLine, existing.Concat(paths).Distinct(StringComparer.Ordinal));
        }
        var addFolders = Ui.AsyncButton("Add folders…", async () =>
        {
            var picked = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Select folders to search", AllowMultiple = true });
            AppendPaths(rootInput, picked);
        }, error => feedback.Text = error.Message);
        addFolders.IsEnabled = StorageProvider.CanPickFolder;
        var addReferences = Ui.AsyncButton("Add files…", async () =>
        {
            var picked = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { Title = "Find duplicates of these files", AllowMultiple = true });
            AppendPaths(referenceInput, picked);
        }, error => feedback.Text = error.Message);
        addReferences.IsEnabled = StorageProvider.CanOpen;
        static Grid WithAccessory(TextBox input, Control accessory)
        {
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 6 };
            row.Children.Add(input);
            accessory.VerticalAlignment = VerticalAlignment.Top;
            Grid.SetColumn(accessory, 1);
            row.Children.Add(accessory);
            return row;
        }
        locations.Children.Add(Ui.Text("Find in"));
        locations.Children.Add(WithAccessory(rootInput, addFolders));
        locations.Children.Add(Ui.Text("Find duplicates of"));
        locations.Children.Add(WithAccessory(referenceInput, addReferences));
        options.Children.Add(Dialogs.Form(("Comparison method", modeInput), ("Minimum size (KB)", minimumInput),
            ("MD5 accuracy", accuracyRow), ("Filter", WithAccessory(filenameFilter, savedFilters)), ("Exclude", excludeLocations)));
        options.Children.Add(recursive);
        options.Children.Add(ignoreOffline);
        options.Children.Add(ignoreHardLinks);
        options.Children.Add(numberGroups);
        options.Children.Add(Dialogs.Form(("Show results in", Ui.Row(resultsPane, resultsTab))));
        prompt.Children.Add(Ui.Text("Find Duplicate Files"));
        prompt.Children.Add(new ScrollViewer { Content = panel, MaxHeight = 224 });
        prompt.Children.Add(feedback);
        string[] ParsePaths(TextBox input) => (input.Text ?? "").Split(['\r', '\n'],
            StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(path => DirectoryBrowser.ResolvePath(path, initial)).ToArray();
        SavedDuplicateSearch Capture(string name)
        {
            var roots = ParsePaths(rootInput);
            var references = ParsePaths(referenceInput);
            if (roots.Length == 0 || roots.Any(path => !Directory.Exists(path)))
                throw new IOException("Enter at least one existing folder to search.");
            if (references.Any(path => !File.Exists(path)))
                throw new IOException("Every reference path must be an existing file.");
            var mode = comparisons[modeInput.SelectedIndex];
            long minimumBytes = DuplicateFinder.ComparesContents(mode) ? 1 : 0;
            if (!string.IsNullOrWhiteSpace(minimumInput.Text))
            {
                if (!long.TryParse(minimumInput.Text.Trim(), System.Globalization.NumberStyles.None,
                        System.Globalization.CultureInfo.InvariantCulture, out var kilobytes) || kilobytes > long.MaxValue / 1024)
                    throw new IOException("Enter a nonnegative whole number of kilobytes for the minimum size.");
                minimumBytes = kilobytes * 1024;
            }
            var options = new DuplicateOptions(Recursive: recursive.IsChecked == true, IncludeHidden: _settings.ShowHidden,
                MinimumSize: minimumBytes, Comparison: mode, ReferenceFiles: references, IgnoreOffline: ignoreOffline.IsChecked == true,
                IgnoreHardLinks: ignoreHardLinks.IsChecked == true, Md5Accuracy: (int)accuracy.Value, FilenameFilter: filenameFilter.Text,
                ExcludedLocations: (excludeLocations.Text ?? "").Split(['\r', '\n'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries));
            return new SavedDuplicateSearch(name, roots, options, numberGroups.IsChecked == true,
                resultsPane.SelectedIndex == 1, resultsTab.IsChecked == true);
        }
        var presets = new ComboBox { Name = "DuplicateSearchPresets", PlaceholderText = "Presets",
            ItemsSource = _settings.SavedDuplicateSearches.OrderBy(preset => preset.Name, StringComparer.OrdinalIgnoreCase).ToArray() };
        presets.SelectionChanged += (_, _) =>
        {
            if (presets.SelectedItem is not SavedDuplicateSearch preset) return;
            var options = preset.Options;
            rootInput.Text = string.Join(Environment.NewLine, preset.Roots);
            referenceInput.Text = string.Join(Environment.NewLine, options.ReferenceFiles ?? []);
            modeInput.SelectedIndex = Array.IndexOf(comparisons, options.Comparison);
            if (modeInput.SelectedIndex < 0) modeInput.SelectedIndex = 0;
            minimumInput.Text = options.MinimumSize == 1 ? "" :
                (options.MinimumSize / 1024).ToString(System.Globalization.CultureInfo.InvariantCulture);
            filenameFilter.Text = options.FilenameFilter;
            excludeLocations.Text = string.Join(Environment.NewLine, options.ExcludedLocations ?? []);
            accuracy.Value = options.Md5Accuracy;
            recursive.IsChecked = options.Recursive;
            ignoreOffline.IsChecked = options.IgnoreOffline;
            ignoreHardLinks.IsChecked = options.IgnoreHardLinks;
            numberGroups.IsChecked = preset.NumberGroups;
            resultsPane.SelectedIndex = preset.Destination ? 1 : 0;
            resultsTab.IsChecked = preset.NewTab;
        };
        async Task UpdatePresetsAsync(SavedDuplicateSearch? previous, SavedDuplicateSearch? replacement)
        {
            if (!_store.CanSave) throw new IOException("Settings are read-only. The preset was not changed.");
            var original = _settings.SavedDuplicateSearches;
            var next = original.Where(preset => preset != previous).ToList();
            if (replacement is not null) next.Add(replacement);
            _settings.SavedDuplicateSearches = next;
            _duplicatePanel.IsEnabled = false;
            try { await SaveSettingsAsync(); }
            catch { _settings.SavedDuplicateSearches = original; throw; }
            finally { _duplicatePanel.IsEnabled = true; }
            presets.ItemsSource = next.OrderBy(preset => preset.Name, StringComparer.OrdinalIgnoreCase).ToArray();
            presets.SelectedItem = replacement;
            feedback.Text = replacement is null ? "Preset removed." : $"Preset saved: {replacement.Name}";
        }
        var savePreset = Ui.AsyncButton("Save preset…", async () =>
        {
            var name = (await Dialogs.PromptAsync(this, "Save Duplicate Search Preset", "Preset name",
                (presets.SelectedItem as SavedDuplicateSearch)?.Name ?? "", "Save"))?.Trim();
            if (name is null) return;
            if (name.Length is < 1 or > 80) throw new ArgumentException("Enter a preset name up to 80 characters.");
            var replacement = Capture(name);
            var existing = _settings.SavedDuplicateSearches.FirstOrDefault(preset => preset.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (existing is null && _settings.SavedDuplicateSearches.Count >= 100)
                throw new InvalidOperationException("Remove a preset before adding another.");
            if (existing is not null && !await Dialogs.ConfirmAsync(this, "Replace Duplicate Search Preset", $"Replace {existing.Name}?", "Replace"))
                return;
            await UpdatePresetsAsync(existing, replacement);
        }, error => feedback.Text = error.Message);
        var removePreset = Ui.AsyncButton("Remove preset…", async () =>
        {
            if (presets.SelectedItem is SavedDuplicateSearch preset &&
                await Dialogs.ConfirmAsync(this, "Remove Duplicate Search Preset", $"Remove {preset.Name}?", "Remove"))
                await UpdatePresetsAsync(preset, null);
        }, error => feedback.Text = error.Message);
        removePreset.IsEnabled = false;
        presets.SelectionChanged += (_, _) => removePreset.IsEnabled = presets.SelectedItem is SavedDuplicateSearch;
        prompt.Children.Insert(1, Ui.Row(presets, savePreset, removePreset));
        _scanDuplicates = async () =>
        {
            if (!_duplicatePanel.IsEnabled) return;
            SavedDuplicateSearch request;
            try
            {
                request = Capture("");
            }
            catch (Exception error) when (error is IOException or ArgumentException or UnauthorizedAccessException)
            { feedback.Text = error.Message; return; }
            feedback.Text = "";
            details.IsEnabled = select.IsEnabled = false;
            _duplicatePanel.IsEnabled = false;
            try
            {
                var target = request.Destination ? _panes.Single(view => view != pane) : pane;
                scanResult = await RunDuplicateScanAsync(target, request.Roots, request.Options, modes[modeInput.SelectedIndex],
                    request.NumberGroups, request.NewTab, request.Destination, feedback);
                details.IsEnabled = scanResult.Errors.Count > 0 || scanResult.SkippedPaths.Count > 0;
                select.IsEnabled = scanResult.Groups.Count > 0 && !scanResult.Truncated && scanResult.Errors.Count == 0;
            }
            finally { _duplicatePanel.IsEnabled = true; }
        };
        var close = Ui.Button("Close panel", () =>
        {
            _scanDuplicates = null;
            _duplicatePanelPane = null;
            _duplicatePanel.IsVisible = false;
            _duplicatePanel.Child = null;
            pane.Focus();
        });
        prompt.Children.Add(Ui.Row(close, details, select, Ui.Button("Scan", () => Dispatch("duplicate-scan"))));
        _duplicatePanel.Child = Ui.Surface(prompt, "inset");
        _duplicatePanel.IsVisible = true;
        rootInput.Focus();
    }

    private async Task<DuplicateResult> RunDuplicateScanAsync(PaneView pane, string[] roots, DuplicateOptions options,
        string comparisonLabel, bool numberGroups, bool newTab, bool destination, TextBlock feedback)
    {
        EnsurePaneReady(pane);
        var scanRevision = pane.ViewRevision;
        var byContent = DuplicateFinder.ComparesContents(options.Comparison);
        var result = await RunToolAsync("Finding duplicate files", (token, progress) =>
            new DuplicateFinder().FindAsync(roots, options, progress, token));
        var outside = result.OutsideReferences.ToHashSet(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        var evidence = byContent ? $"Potential duplicate bytes: {FileEntry.FormatSize(result.Groups.Sum(x => x.ReclaimableBytes))}"
            : $"{comparisonLabel} matches · File contents may differ";
        if (result.Md5Accuracy < 100)
            evidence = $"MD5 accuracy {result.Md5Accuracy}% · File contents may differ";
        feedback.Text = $"{result.Groups.Count:N0} duplicate groups · {result.ScannedFiles:N0} files scanned · {evidence}"
            + (result.Truncated ? " · Scan limit reached" : "") + $" · {result.Errors.Count:N0} errors · {result.SkippedPaths.Count:N0} skipped paths"
            + (outside.Count > 0 ? " · Outside references are excluded from file-display selection" : "");
        if (result.Truncated || result.Errors.Count > 0) return result;
        if (pane.ViewRevision != scanRevision) throw new IOException("The file display changed during the duplicate scan. Scan again.");
        var groups = await RunToolAsync("Verifying duplicate results", (token, _) =>
            new DuplicateFinder().ReadReviewedGroupsAsync(result, verifyContents: false, token));
        EnsurePaneReady(pane);
        if (pane.ViewRevision != scanRevision) throw new IOException("The file display changed while verifying results. Scan again.");
        var rows = groups.SelectMany((group, index) =>
        {
            var match = result.Groups[index];
            var filename = Path.GetFileName(match.Paths[0]);
            var label = numberGroups ? (index + 1).ToString() : options.Comparison switch
            {
                DuplicateComparison.Filename => filename,
                DuplicateComparison.FilenameStem => Path.GetFileNameWithoutExtension(filename),
                DuplicateComparison.FilenameAndSize => $"{filename} ({FileEntry.FormatSize(match.Length)})",
                DuplicateComparison.Size => FileEntry.FormatSize(match.Length),
                _ => match.Hash
            };
            var header = new PaneView.DuplicateGroupHeader(index + 1, label);
            return group.Where(entry => !outside.Contains(entry.FullPath)).Select(entry => (Entry: entry, Group: header));
        }).ToArray();
        if (destination && !_settings.DualPane)
        {
            _settings.DualPane = true;
            ApplyLayout();
            ScheduleSave();
        }
        pane.ShowDuplicateResults(rows.Select(row => row.Entry).ToArray(), $"Duplicate Files · {result.Groups.Count:N0} groups",
            rows.ToDictionary(row => row.Entry.FullPath, row => row.Group, outside.Comparer), result, newTab);
        await pane.LoadingTask;
        if (ReferenceEquals(pane.CurrentDuplicateResult, result)) _duplicatePanelPane = pane;
        return result;
    }

    private async Task SelectDuplicatesAsync(PaneView pane)
    {
        EnsurePaneReady(pane);
        var revision = pane.ViewRevision;
        if (pane.CurrentDuplicateResult is not { } result)
            throw new InvalidOperationException("Run Find Duplicate Files and show its results in this file display first.");
        var choice = new ComboBox
        {
            Name = "DuplicateKeepRule",
            ItemsSource = new[] { "Keep oldest modified file", "Keep newest modified file", "Keep first path by name" },
            SelectedIndex = 0
        };
        var details = Ui.Text($"Choose which copy to leave unselected in each of the {result.Groups.Count:N0} duplicate groups.", true);
        details.TextWrapping = TextWrapping.Wrap;
        var layout = new StackPanel { Margin = new Thickness(16), Spacing = 10, Children = { details, choice } };
        var folders = new Dictionary<string, DuplicateFolderBranch>(FolderPair.Comparer);
        var roots = new List<DuplicateFolderBranch>();
        DuplicateFolderBranch Folder(string path)
        {
            if (folders.TryGetValue(path, out var branch)) return branch;
            var parentPath = Path.GetDirectoryName(path);
            var parent = parentPath is null ? null : Folder(parentPath);
            branch = new DuplicateFolderBranch(path, parent);
            folders.Add(path, branch);
            (parent?.Children ?? roots).Add(branch);
            return branch;
        }
        foreach (var entry in pane.VisibleEntries)
            if (Path.GetDirectoryName(entry.FullPath) is { } path) Folder(path).HasFiles = true;

        var rendered = new Dictionary<DuplicateFolderBranch, CheckBox>();
        var updatingTree = false;
        var tree = new TreeView { Name = "DuplicateSelectionFolders", Height = 144 };
        DuplicateFolderBranch? rangeAnchor = null;
        IEnumerable<DuplicateFolderBranch> ExpandedOrder(ItemsControl items)
        {
            foreach (var item in items.Items.OfType<TreeViewItem>())
            {
                if (item.Tag is not DuplicateFolderBranch branch) continue;
                yield return branch;
                if (!item.IsExpanded) continue;
                foreach (var child in ExpandedOrder(item)) yield return child;
            }
        }
        void SetBranch(DuplicateFolderBranch branch, bool included)
        {
            branch.Included = included;
            branch.State = included;
            if (rendered.TryGetValue(branch, out var box)) box.IsChecked = included;
            foreach (var child in branch.Children) SetBranch(child, included);
        }
        void UpdateAncestors(DuplicateFolderBranch branch)
        {
            for (var parent = branch.Parent; parent is not null; parent = parent.Parent)
            {
                var state = parent.HasFiles ? parent.Included : parent.Children[0].State;
                parent.State = parent.Children.All(child => child.State == state) ? state : null;
                if (rendered.TryGetValue(parent, out var parentBox)) parentBox.IsChecked = parent.State;
            }
        }
        TreeViewItem RenderBranch(DuplicateFolderBranch branch, bool root = false)
        {
            var box = new CheckBox { Content = root ? branch.Path : Path.GetFileName(branch.Path), IsChecked = branch.State, Tag = branch.Path };
            ToolTip.SetTip(box, branch.Path);
            rendered.Add(branch, box);
            var rangeRequested = false;
            box.AddHandler(PointerReleasedEvent, (_, args) => rangeRequested = args.KeyModifiers.HasFlag(KeyModifiers.Shift),
                RoutingStrategies.Tunnel);
            box.AddHandler(KeyDownEvent, (_, _) => rangeRequested = false, RoutingStrategies.Tunnel);
            box.IsCheckedChanged += (_, _) =>
            {
                if (updatingTree || box.IsChecked is not { } included) return;
                var useRange = rangeRequested;
                rangeRequested = false;
                updatingTree = true;
                try
                {
                    if (useRange && rangeAnchor is not null)
                    {
                        var ordered = ExpandedOrder(tree).ToList();
                        var from = ordered.IndexOf(rangeAnchor);
                        var to = ordered.IndexOf(branch);
                        if (from >= 0 && to >= 0)
                        {
                            for (var i = Math.Min(from, to); i <= Math.Max(from, to); i++)
                            {
                                SetBranch(ordered[i], included);
                                UpdateAncestors(ordered[i]);
                            }
                            return;
                        }
                    }
                    SetBranch(branch, included);
                    UpdateAncestors(branch);
                    rangeAnchor = branch;
                }
                finally { updatingTree = false; }
            };
            var item = new TreeViewItem { Header = box, Tag = branch };
            if (branch.Children.Count == 0) return item;
            item.Items.Add(new TreeViewItem());
            var loaded = false;
            item.PropertyChanged += (_, change) =>
            {
                if (change.Property != TreeViewItem.IsExpandedProperty || !item.IsExpanded || loaded) return;
                loaded = true;
                item.Items.Clear();
                foreach (var child in branch.Children.OrderBy(child => child.Path, NaturalNameComparer.Instance))
                    item.Items.Add(RenderBranch(child));
            };
            item.IsExpanded = root;
            return item;
        }
        foreach (var root in roots.OrderBy(branch => branch.Path, NaturalNameComparer.Instance))
        {
            var first = root;
            while (!first.HasFiles && first.Children.Count == 1) first = first.Children[0];
            tree.Items.Add(RenderBranch(first, root: true));
        }
        layout.Children.Add(Ui.Text("Folders to select from"));
        void SetAllBranches(bool included)
        {
            updatingTree = true;
            try
            {
                foreach (var root in roots) SetBranch(root, included);
                rangeAnchor = null;
            }
            finally { updatingTree = false; }
        }
        layout.Children.Add(Ui.Row(Ui.Button("Check all folders", () => SetAllBranches(true)),
            Ui.Button("Uncheck all folders", () => SetAllBranches(false))));
        layout.Children.Add(tree);
        var preferred = new TextBox { Name = "DuplicatePreferredLocations", AcceptsReturn = true, Height = 80,
            Watermark = "One folder per line", Text = string.Join(Environment.NewLine, _settings.DuplicatePreferredLocations) };
        var remember = new CheckBox { Name = "DuplicateRememberLocations", Content = "Remember preferred locations" };
        layout.Children.Add(Ui.Text("Preferred locations"));
        layout.Children.Add(preferred);
        var hint = Ui.Text("Folders higher in the list take priority, including their subfolders.", true);
        hint.TextWrapping = TextWrapping.Wrap;
        layout.Children.Add(hint);
        layout.Children.Add(remember);
        var byContent = DuplicateFinder.ComparesContents(result.Comparison);
        if (result.Md5Accuracy < 100)
            layout.Children.Add(Ui.Text($"These files matched at {result.Md5Accuracy}% MD5 accuracy. Their contents may differ.", true));
        else if (!byContent) layout.Children.Add(Ui.Text("These files matched by metadata. Their contents may differ.", true));
        var dialog = Dialogs.Window("Duplicates Selection", new ScrollViewer { Content = layout }, 560, 590);
        var validation = Ui.Text("", true);
        validation.TextWrapping = TextWrapping.Wrap;
        layout.Children.Add(validation);
        string[] preferredFolders = [];
        var select = Ui.Button("Select", () =>
        {
            try
            {
                preferredFolders = (preferred.Text ?? "").Split(['\r', '\n'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                    .Select(path => Path.TrimEndingDirectorySeparator(DirectoryBrowser.ResolvePath(path, pane.CurrentDirectory)))
                    .Distinct(FolderPair.Comparer).ToArray();
                if (preferredFolders.Length > 100) throw new ArgumentException("Enter up to 100 preferred locations.");
                if (remember.IsChecked == true && !_store.CanSave) throw new IOException("Settings are read-only.");
                dialog.Close(true);
            }
            catch (Exception error) when (error is ArgumentException or IOException or NotSupportedException)
            { validation.Text = error.Message; }
        });
        layout.Children.Add(Ui.Row(Ui.Button("Cancel", () => dialog.Close(false)), select));
        if (!await dialog.ShowDialog<bool>(this)) return;
        EnsurePaneReady(pane);
        if (pane.ViewRevision != revision) throw new IOException("The duplicate display or filter changed. Run Select Duplicates again.");
        var groups = await RunToolAsync(byContent ? "Rechecking duplicate contents" : "Rechecking duplicate matches", (token, _) =>
            new DuplicateFinder().ReadReviewedGroupsAsync(result, verifyContents: byContent, token));
        EnsurePaneReady(pane);
        if (pane.ViewRevision != revision) throw new IOException("The duplicate display or filter changed while verifying files.");
        var folderPrefixes = preferredFolders.Select(folder => Path.EndsInDirectorySeparator(folder)
            ? folder : folder + Path.DirectorySeparatorChar).ToArray();
        var pathComparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        int Preference(string path)
        {
            var rank = Array.FindIndex(folderPrefixes, prefix => path.StartsWith(prefix, pathComparison));
            return rank < 0 ? folderPrefixes.Length : rank;
        }
        bool ExcludedByTree(FileEntry entry) => Path.GetDirectoryName(entry.FullPath) is { } path &&
            folders.TryGetValue(path, out var folder) && !folder.Included;
        var paths = groups.SelectMany(group =>
        {
            var ordered = group.OrderByDescending(ExcludedByTree).ThenBy(entry => Preference(entry.FullPath));
            var keeper = choice.SelectedIndex switch
            {
                0 => ordered.ThenBy(entry => entry.ModifiedUtc).ThenBy(entry => entry.FullPath, StringComparer.Ordinal).First(),
                1 => ordered.ThenByDescending(entry => entry.ModifiedUtc).ThenBy(entry => entry.FullPath, StringComparer.Ordinal).First(),
                _ => ordered.ThenBy(entry => entry.FullPath, NaturalNameComparer.Instance).First()
            };
            return group.Where(entry => entry != keeper && !ExcludedByTree(entry)).Select(entry => entry.FullPath);
        });
        pane.SelectActionPaths(paths);
        if (remember.IsChecked == true)
        {
            var previous = _settings.DuplicatePreferredLocations;
            _settings.DuplicatePreferredLocations = preferredFolders.ToList();
            try { await SaveSettingsAsync(); }
            catch { _settings.DuplicatePreferredLocations = previous; throw; }
        }
        _message.Text = $"Selected {pane.ActionEntries.Count:N0} duplicate files";
    }

    private sealed class DuplicateFolderBranch(string path, DuplicateFolderBranch? parent)
    {
        public string Path { get; } = path;
        public DuplicateFolderBranch? Parent { get; } = parent;
        public List<DuplicateFolderBranch> Children { get; } = [];
        public bool HasFiles { get; set; }
        public bool Included { get; set; } = true;
        public bool? State { get; set; } = true;
    }

    private async Task CalculateFolderSizesAsync(PaneView pane)
    {
        EnsurePaneReady(pane);
        var selected = pane.ActionEntries.Where(entry => entry.IsDirectory).ToArray();
        var folders = (selected.Length == 0 ? pane.VisibleEntries.Where(entry => entry.IsDirectory) : selected).ToArray();
        var followNestedLinks = !_settings.IgnoreLinksBelowSizedFolders;
        if (folders.Length == 0) { _message.Text = "No folders to size."; return; }
        var revision = pane.ViewRevision;
        var searchGeneration = _searchRequestGeneration;
        var indexed = folders.Select(entry => pane.IndexedIdentity(entry)).OfType<ListarySearchResult>().ToArray();
        var result = await RunToolAsync("Calculating folder sizes", async (token, progress) =>
        {
            var rejected = new Dictionary<string, string>(FolderPair.Comparer);
            async Task VerifyIndexedFoldersAsync()
            {
                foreach (var identity in indexed)
                {
                    if (rejected.ContainsKey(identity.Entry.FullPath)) continue;
                    try
                    {
                        var verified = await _listary.RevalidateAsync(identity, token);
                        if (!verified.IsDirectory || verified.IsSymbolicLink ||
                            !FolderPair.Comparer.Equals(verified.FullPath, identity.Entry.FullPath))
                            rejected[identity.Entry.FullPath] = $"{identity.Entry.Name}: Indexed folder changed. Refresh the search.";
                    }
                    catch (ListaryException error)
                    { rejected[identity.Entry.FullPath] = $"{identity.Entry.Name}: {error.Message}"; }
                }
            }
            await VerifyIndexedFoldersAsync();
            var admitted = folders.Where(folder => !rejected.ContainsKey(folder.FullPath)).ToArray();
            var result = await Task.Run(() => DirectoryBrowser.CalculateFolderSizes(admitted, token, count =>
                progress.Report(new ToolProgress("Calculating folder sizes", count, admitted.Length)),
                followNestedLinks), token);
            await VerifyIndexedFoldersAsync();
            foreach (var (path, error) in rejected)
            {
                result.Sizes.Remove(path);
                result.Errors.Add(error);
            }
            return result;
        });
        if (revision.Navigation != pane.ViewRevision.Navigation || searchGeneration != _searchRequestGeneration) return;
        var pagingCurrent = ReferenceEquals(_searchPane, pane) && _publishedSearchRevision == pane.ViewRevision;
        var beforePublish = pane.ViewRevision;
        await pane.ShowCalculatedFolderSizesAsync(result.Sizes, folders.Select(folder => folder.FullPath).ToHashSet(FolderPair.Comparer));
        if (pagingCurrent && searchGeneration == _searchRequestGeneration &&
            pane.ViewRevision == (beforePublish.Navigation, beforePublish.Filter + 1))
            _publishedSearchRevision = pane.ViewRevision;
        _message.Text = result.Errors.Count == 0
            ? $"Calculated sizes for {result.Sizes.Count:N0} folders"
            : $"Calculated {result.Sizes.Count:N0} · {result.Errors.Count:N0} failed · {result.Errors[0]}";
    }

    private async Task FolderSizeSettingsAsync()
    {
        var mode = new ComboBox
        {
            Name = "AutomaticFolderSizeMode",
            ItemsSource = new[] { "Off", "Local and removable drives", "All drives" },
            SelectedIndex = (int)_settings.AutomaticFolderSizes
        };
        var cacheHistory = new CheckBox
        {
            Content = "Cache folder sizes when going back and forward in the history",
            IsChecked = _settings.CacheFolderSizesInHistory
        };
        var updateAfterOperations = new CheckBox
        {
            Content = "Update folder sizes after file operations where possible",
            IsChecked = _settings.UpdateFolderSizesAfterOperations
        };
        var ignoreNestedLinks = new CheckBox
        {
            Content = "Ignore junctions below a folder when calculating its size", IsChecked = _settings.IgnoreLinksBelowSizedFolders
        };
        var ignoreLinkedFolders = new CheckBox
        {
            Content = "Skip automatic sizing of linked folders", IsChecked = _settings.IgnoreLinkedFoldersForAutomaticSizing
        };
        var rules = _settings.FolderSizeOverrides.ToList();
        var list = new ListBox { Name = "FolderSizeOverrides", ItemsSource = rules.ToArray() };
        var path = new TextBox { Name = "FolderSizeOverridePath", Text = ActivePane.CurrentDirectory };
        var action = new ComboBox { Name = "FolderSizeOverrideAction", ItemsSource = new[] { "Calculate sizes", "Skip sizes" }, SelectedIndex = 0 };
        var status = Ui.Text("Overrides affect subfolders shown inside the matching folder. Exact paths take priority.", true);
        status.TextWrapping = TextWrapping.Wrap;
        var panel = new Grid { Margin = new Thickness(16), RowSpacing = 8, RowDefinitions = new RowDefinitions("Auto,*,Auto,Auto") };
        panel.Children.Add(new StackPanel { Spacing = 6,
            Children = { Dialogs.Form(("Calculate folder sizes automatically", mode)), cacheHistory, updateAfterOperations,
                ignoreNestedLinks, ignoreLinkedFolders } });
        Grid.SetRow(list, 1);
        panel.Children.Add(list);
        var form = new StackPanel { Spacing = 4, Children = { Dialogs.Form(("Folder or wildcard", path), ("Override", action)), status } };
        Grid.SetRow(form, 2);
        panel.Children.Add(form);
        var dialog = Dialogs.Window("Folder Sizes", panel, 640, 440);
        list.SelectionChanged += (_, _) =>
        {
            if (list.SelectedItem is not FolderSizeOverride rule) return;
            path.Text = rule.Path;
            action.SelectedIndex = rule.Enabled ? 0 : 1;
        };
        void Stage(bool update)
        {
            var index = update ? list.SelectedIndex : rules.Count;
            if (index < 0) { status.Text = "Select a rule to update."; return; }
            if (!update && rules.Count >= 500) { status.Text = "The folder override limit is 500."; return; }
            var rule = new FolderSizeOverride(path.Text ?? "", action.SelectedIndex == 0).Normalize();
            if (rule is null || rules.Where((_, position) => position != index)
                .Any(existing => FolderPair.Comparer.Equals(existing.Path, rule.Path)))
            { status.Text = "Enter a unique absolute folder path or wildcard."; return; }
            if (update) rules[index] = rule;
            else rules.Add(rule);
            list.ItemsSource = rules.ToArray();
            list.SelectedIndex = index;
        }
        var buttons = Ui.Row(Ui.Button("Add override", () => Stage(false)), Ui.Button("Update override", () => Stage(true)),
            Ui.Button("Remove override", () =>
            {
                if (list.SelectedIndex < 0) return;
                rules.RemoveAt(list.SelectedIndex);
                list.ItemsSource = rules.ToArray();
            }), Ui.Button("Cancel", () => dialog.Close(false)), Ui.Button("Apply", () => dialog.Close(true)));
        Grid.SetRow(buttons, 3);
        panel.Children.Add(buttons);
        if (!await dialog.ShowDialog<bool>(this) || mode.SelectedIndex < 0) return;
        var previous = _settings.AutomaticFolderSizes;
        var previousRules = _settings.FolderSizeOverrides;
        var previousCache = _settings.CacheFolderSizesInHistory;
        var previousUpdates = _settings.UpdateFolderSizesAfterOperations;
        var previousNestedLinks = _settings.IgnoreLinksBelowSizedFolders;
        var previousLinkedFolders = _settings.IgnoreLinkedFoldersForAutomaticSizing;
        _settings.AutomaticFolderSizes = (FolderSizeMode)mode.SelectedIndex;
        _settings.FolderSizeOverrides = rules;
        _settings.CacheFolderSizesInHistory = cacheHistory.IsChecked == true;
        _settings.UpdateFolderSizesAfterOperations = updateAfterOperations.IsChecked == true;
        _settings.IgnoreLinksBelowSizedFolders = ignoreNestedLinks.IsChecked == true;
        _settings.IgnoreLinkedFoldersForAutomaticSizing = ignoreLinkedFolders.IsChecked == true;
        try { await SaveSettingsAsync(); }
        catch
        {
            _settings.AutomaticFolderSizes = previous;
            _settings.FolderSizeOverrides = previousRules;
            _settings.CacheFolderSizesInHistory = previousCache;
            _settings.UpdateFolderSizesAfterOperations = previousUpdates;
            _settings.IgnoreLinksBelowSizedFolders = previousNestedLinks;
            _settings.IgnoreLinkedFoldersForAutomaticSizing = previousLinkedFolders;
            throw;
        }
        foreach (var pane in _panes) pane.ClearFolderSizeHistory();
        await RefreshPanesAsync();
    }

    private async Task SetHomeFolderAsync(string? path)
    {
        var previous = _settings.HomeFolder;
        _settings.HomeFolder = path;
        try { await SaveSettingsAsync(); }
        catch { _settings.HomeFolder = previous; throw; }
        _message.Text = path is null ? "Home uses the first folder opened in each tab." : $"Home folder set to {path}";
    }

    private async Task HomeFolderSettingsAsync()
    {
        var mode = new ComboBox
        {
            ItemsSource = new[] { "First folder opened in the tab", "Specific folder" },
            SelectedIndex = _settings.HomeFolder is null ? 0 : 1
        };
        var path = new TextBox { Name = "HomeFolderPath", Text = _settings.HomeFolder ?? ActivePane.CurrentDirectory };
        var status = Ui.Text("", true);
        var panel = new StackPanel { Margin = new Thickness(16), Spacing = 10,
            Children = { Dialogs.Form(("Home folder", mode), ("Folder", path)), status } };
        var dialog = Dialogs.Window("Home Folder", panel, 570, 250);
        var browse = Ui.AsyncButton("Browse…", async () =>
        {
            var folders = await dialog.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Choose Home folder" });
            if (folders.FirstOrDefault()?.TryGetLocalPath() is { } selected)
            {
                path.Text = selected;
                mode.SelectedIndex = 1;
            }
        }, error => status.Text = error.Message);
        string? selectedPath = null;
        panel.Children.Add(Ui.Row(browse, Ui.Button("Cancel", () => dialog.Close(false)), Ui.Button("Apply", () =>
        {
            if (mode.SelectedIndex == 1)
            {
                if (!FolderPair.TryNormalize(path.Text, out var normalized))
                { status.Text = "Enter an absolute folder path."; return; }
                selectedPath = normalized;
            }
            dialog.Close(true);
        })));
        if (await dialog.ShowDialog<bool>(this)) await SetHomeFolderAsync(selectedPath);
    }

    private async Task ManageFolderAliasesAsync()
    {
        var aliases = _settings.FolderAliases.ToList();
        var list = new ListBox { Name = "FolderAliases", ItemsSource = aliases.ToArray() };
        var filter = new TextBox { Name = "FolderAliasFilter", Watermark = "Filter by alias name or path" };
        var name = new TextBox { Name = "FolderAliasName", Watermark = "Alias name" };
        var target = new TextBox { Name = "FolderAliasPath", Text = ActivePane.CurrentDirectory };
        var showBuiltIn = new CheckBox { Content = "Show built-in aliases" };
        var browse = Ui.Button("Browse…", () => { });
        var pathRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 6 };
        pathRow.Children.Add(target);
        Grid.SetColumn(browse, 1);
        pathRow.Children.Add(browse);
        var status = Ui.Text("Enter /alias or /alias/subfolder in the location field.", true);
        var grid = new Grid { Margin = new Thickness(14), RowDefinitions = new RowDefinitions("*,Auto,Auto"), RowSpacing = 8 };
        var aliasList = new Grid { RowDefinitions = new RowDefinitions("Auto,*"), RowSpacing = 6 };
        aliasList.Children.Add(filter);
        Grid.SetRow(list, 1);
        aliasList.Children.Add(list);
        grid.Children.Add(aliasList);
        var form = new StackPanel { Spacing = 6, Children = { Dialogs.Form(("Name", name), ("Folder", pathRow)), showBuiltIn, status } };
        Grid.SetRow(form, 1);
        grid.Children.Add(form);
        var dialog = Dialogs.Window("Folder Aliases", grid, 660, 440);
        browse.Click += (_, _) => Observe(BrowseAliasTargetAsync());
        async Task BrowseAliasTargetAsync()
        {
            var folders = await dialog.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "Choose a folder for this alias", AllowMultiple = false
            });
            if (dialog.IsVisible && folders.FirstOrDefault()?.TryGetLocalPath() is { } path) target.Text = path;
        }
        int SelectedCustomIndex() => aliases.FindIndex(alias => ReferenceEquals(alias, list.SelectedItem));
        void RefreshList(FolderAlias? selection = null)
        {
            var rows = showBuiltIn.IsChecked == true
                ? aliases.Concat(AppSettings.BuiltInFolderAliases(_store.StateDirectory))
                : aliases;
            var query = (filter.Text ?? "").Trim().TrimStart('/');
            var visible = rows.Where(alias => alias.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                alias.Path.Contains(query, StringComparison.OrdinalIgnoreCase)).ToArray();
            list.ItemsSource = visible;
            list.SelectedIndex = Array.FindIndex(visible, alias => ReferenceEquals(alias, selection));
        }
        showBuiltIn.IsCheckedChanged += (_, _) => RefreshList(list.SelectedItem as FolderAlias);
        filter.TextChanged += (_, _) => RefreshList(list.SelectedItem as FolderAlias);
        list.SelectionChanged += (_, _) =>
        {
            if (list.SelectedItem is not FolderAlias alias) return;
            name.Text = alias.Name;
            target.Text = alias.Path;
            status.Text = SelectedCustomIndex() < 0
                ? "Built-in alias. Add a custom alias with this name to override it."
                : "Enter /alias or /alias/subfolder in the location field.";
        };
        void StageAlias(bool update)
        {
            var index = update ? SelectedCustomIndex() : aliases.Count;
            if (index < 0 || (!update && aliases.Count >= 500))
            {
                status.Text = index < 0
                    ? list.SelectedItem is null ? "Select an alias to update." : "Built-in aliases cannot be edited."
                    : "The folder alias limit is 500.";
                return;
            }
            var alias = new FolderAlias(name.Text ?? "", target.Text ?? "").Normalize();
            if (alias is null || aliases.Where((_, position) => position != index)
                .Any(existing => string.Equals(existing.Name, alias.Name, StringComparison.OrdinalIgnoreCase)))
            {
                status.Text = "Enter a unique alias name and an absolute folder path.";
                return;
            }
            if (update) aliases[index] = alias;
            else aliases.Add(alias);
            RefreshList(alias);
        }
        var buttons = Ui.Row(Ui.Button("Add alias", () => StageAlias(false)), Ui.Button("Update alias", () => StageAlias(true)),
            Ui.Button("Remove alias", () =>
            {
                if (list.SelectedIndex < 0) return;
                var index = SelectedCustomIndex();
                if (index < 0) { status.Text = "Built-in aliases cannot be removed."; return; }
                aliases.RemoveAt(index);
                RefreshList();
            }), Ui.Button("Cancel", () => dialog.Close(false)), Ui.Button("Apply", () => dialog.Close(true)));
        Grid.SetRow(buttons, 2);
        grid.Children.Add(buttons);
        if (!await dialog.ShowDialog<bool>(this)) return;
        var before = _settings.FolderAliases;
        _settings.FolderAliases = aliases;
        try { await SaveSettingsAsync(); }
        catch { _settings.FolderAliases = before; throw; }
        UpdateNavigationLockControls();
    }

    private async Task ManageFolderPairsAsync()
    {
        var pairs = _settings.FolderPairs.ToList();
        var list = new ListBox { Name = "PairedFolders", ItemsSource = pairs.ToArray() };
        var source = new TextBox { Name = "PairSource", Text = ActivePane.CurrentDirectory };
        var destination = new TextBox { Name = "PairDestination", Text = OtherPane.CurrentDirectory };
        var descendants = new CheckBox { Content = "Include subfolders", IsChecked = true };
        var regex = new CheckBox { Content = "Regular expression" };
        var expandAliases = new CheckBox { Content = "Expand folder aliases", IsEnabled = false };
        regex.IsCheckedChanged += (_, _) =>
        {
            var pattern = regex.IsChecked == true;
            descendants.IsEnabled = !pattern;
            expandAliases.IsEnabled = pattern;
            source.Watermark = pattern ? "Match pattern" : "Primary absolute path";
            destination.Watermark = pattern ? "Replacement pattern" : "Secondary absolute path";
        };
        var dual = new CheckBox { Content = "Default dual-display folder" };
        var lockOnDual = new CheckBox { Content = "Turn on Navigation Lock when opening dual display" };
        var primaryOnLeft = new CheckBox { Content = "Always display primary folder at the left/top", IsEnabled = false };
        var readIntoDual = new CheckBox { Content = "Read into existing dual display", IsEnabled = false };
        var openDual = new CheckBox { Content = "Switch to dual display automatically", IsEnabled = false };
        lockOnDual.IsEnabled = false;
        dual.IsCheckedChanged += (_, _) => lockOnDual.IsEnabled = primaryOnLeft.IsEnabled =
            readIntoDual.IsEnabled = openDual.IsEnabled = dual.IsChecked == true;
        var linkTabs = new CheckBox { Content = "Automatically link paired tabs" };
        var nav = new CheckBox { Content = "Default Navigation Lock target" };
        var sync = new CheckBox { Content = "Default Synchronize target" };
        var missingTarget = new ComboBox
        {
            Name = "MissingPairedFolderTarget",
            ItemsSource = new[] { "Use path even if it doesn't exist", "Go up to first existing parent", "Ignore the pair" },
            SelectedIndex = 0
        };
        var testFolder = new TextBox { Name = "PairTestFolder", Text = ActivePane.CurrentDirectory };
        var testResult = new TextBox { Name = "PairTestResult", IsReadOnly = true };
        var browseTest = Ui.Button("Browse…", () => { });
        var testInputRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 6 };
        testInputRow.Children.Add(testFolder);
        Grid.SetColumn(browseTest, 1);
        testInputRow.Children.Add(browseTest);
        foreach (var input in new[] { source, destination, testFolder })
            input.TextChanged += (_, _) => testResult.Text = "";
        regex.IsCheckedChanged += (_, _) => testResult.Text = "";
        expandAliases.IsCheckedChanged += (_, _) => testResult.Text = "";
        descendants.IsCheckedChanged += (_, _) => testResult.Text = "";
        missingTarget.SelectionChanged += (_, _) => testResult.Text = "";
        var status = Ui.Text("Pairs are evaluated in list order. Select a pair to edit it.", true);
        var grid = new Grid { Margin = new Thickness(14), RowDefinitions = new RowDefinitions("*,Auto,Auto"), RowSpacing = 8 };
        grid.Children.Add(list);
        var form = new StackPanel
        {
            Spacing = 4, Children = { Dialogs.Form(("Source", source), ("Destination", destination), ("If non-existent", missingTarget)),
                regex, expandAliases, descendants, dual, primaryOnLeft, readIntoDual, openDual, lockOnDual, linkTabs, nav, sync,
                Dialogs.Form(("Test folder", testInputRow), ("Result", testResult)), status }
        };
        Grid.SetRow(form, 1); grid.Children.Add(form);
        var dialog = Dialogs.Window("Paired Folders", grid, 750, 745);
        browseTest.Click += (_, _) => Observe(BrowseTestFolderAsync());
        async Task BrowseTestFolderAsync()
        {
            var folders = await dialog.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "Choose a test folder", AllowMultiple = false
            });
            if (dialog.IsVisible && folders.FirstOrDefault()?.TryGetLocalPath() is { } path) testFolder.Text = path;
        }
        list.SelectionChanged += (_, _) =>
        {
            if (list.SelectedItem is not FolderPair pair) return;
            source.Text = pair.Source;
            destination.Text = pair.Destination;
            regex.IsChecked = pair.RegularExpression;
            expandAliases.IsChecked = pair.ExpandRegexAliases;
            descendants.IsChecked = pair.IncludeSubfolders;
            dual.IsChecked = pair.DefaultDual;
            lockOnDual.IsChecked = pair.LockOnDual;
            primaryOnLeft.IsChecked = pair.PrimaryOnLeft;
            readIntoDual.IsChecked = pair.ReadIntoExistingDual;
            openDual.IsChecked = pair.SwitchToDualAutomatically;
            linkTabs.IsChecked = pair.LinkPairedTabs;
            nav.IsChecked = pair.DefaultNavigationLock;
            sync.IsChecked = pair.DefaultSynchronize;
            missingTarget.SelectedIndex = (int)pair.MissingTarget;
        };
        FolderPair? ReadEditor() => new FolderPair(source.Text ?? "", destination.Text ?? "", descendants.IsChecked == true, dual.IsChecked == true,
                nav.IsChecked == true, sync.IsChecked == true, dual.IsChecked == true && lockOnDual.IsChecked == true,
                (MissingFolderPairTarget)Math.Max(0, missingTarget.SelectedIndex),
                dual.IsChecked == true && primaryOnLeft.IsChecked == true, regex.IsChecked == true,
                readIntoDual.IsChecked == true, openDual.IsChecked == true, linkTabs.IsChecked == true,
                expandAliases.IsChecked == true).Normalize();
        async Task TestPairAsync()
        {
            var pair = ReadEditor();
            var testInput = testFolder.Text;
            var expandedInput = await ExpandLocationAliasAsync(testInput ?? "");
            if (pair is null || !FolderPair.TryNormalize(expandedInput, out var path))
            {
                testResult.Text = "Enter a valid pair and an absolute test folder.";
                return;
            }
            testResult.Text = "Checking…";
            var result = await ResolveFolderPairAsync(path, [pair]);
            if (!dialog.IsVisible) return;
            testResult.Text = testInput == testFolder.Text && Equals(pair, ReadEditor())
                ? result?.Target ?? "No matching destination."
                : "The rule changed. Test it again.";
        }

        void SavePair(bool replace)
        {
            var index = list.SelectedIndex;
            if (replace && index < 0) { status.Text = "Select a pair to update."; return; }
            if (!replace && pairs.Count >= 500) { status.Text = "The folder pair limit is 500."; return; }
            var pair = ReadEditor();
            if (pair is null)
            {
                status.Text = regex.IsChecked == true ? "Enter a valid match and replacement pattern." : "Enter two different absolute paths or folder aliases.";
                return;
            }
            if (replace) pairs[index] = pair;
            else { index = pairs.Count; pairs.Add(pair); }
            list.ItemsSource = pairs.ToArray();
            list.SelectedIndex = index;
            status.Text = "Pair staged. Apply to save.";
        }
        void MovePair(int offset)
        {
            var index = list.SelectedIndex;
            var next = index + offset;
            if (index < 0 || next < 0 || next >= pairs.Count) return;
            (pairs[index], pairs[next]) = (pairs[next], pairs[index]);
            list.ItemsSource = pairs.ToArray();
            list.SelectedIndex = next;
            status.Text = "Order staged. Apply to save.";
        }
        var buttons = Ui.Row(Ui.Button("Add pair", () => SavePair(false)), Ui.Button("Update pair", () => SavePair(true)),
            Ui.Button("Remove pair", () =>
            {
                if (list.SelectedIndex < 0) return;
                pairs.RemoveAt(list.SelectedIndex);
                list.ItemsSource = pairs.ToArray();
                status.Text = "Removal staged. Apply to save.";
            }), Ui.Button("Move up", () => MovePair(-1)), Ui.Button("Move down", () => MovePair(1)),
            Ui.AsyncButton("Test pair", TestPairAsync, error => testResult.Text = error.Message),
            Ui.Button("Cancel", () => dialog.Close(false)), Ui.Button("Apply", () => dialog.Close(true)));
        Grid.SetRow(buttons, 2); grid.Children.Add(buttons);
        if (!await dialog.ShowDialog<bool>(this)) return;
        _settings.FolderPairs = pairs;
        UpdateNavigationLockControls();
        await SaveSettingsAsync();
    }

    private async Task CompareFoldersAsync()
    {
        var sourceDirectory = ActivePane.CurrentDirectory;
        var destinationDirectory = OtherPane.CurrentDirectory;
        var pair = await ResolveFolderPairAsync(sourceDirectory, rule => rule.DefaultSynchronize);
        var left = new TextBox { Text = sourceDirectory };
        var right = new TextBox { Text = pair?.Target ?? destinationDirectory };
        var contents = new CheckBox { Content = "Compare file contents (otherwise metadata only)", IsChecked = true };
        var direction = new ComboBox { ItemsSource = new[] { "Source → Destination", "Destination → Source" }, SelectedIndex = 0 };
        var replace = new CheckBox { Content = "Include changed files and replace their destination contents", IsChecked = false };
        var rows = Ui.Grid(("Relative path", nameof(ComparisonItem.RelativePath), -1), ("Status", nameof(ComparisonItem.Status), 120), ("Source bytes", nameof(ComparisonItem.LeftLength), 105), ("Destination bytes", nameof(ComparisonItem.RightLength), 120));
        var message = Ui.Text("Compare the folders, then review the copy plan. Files are never deleted by synchronization.", true); message.TextWrapping = TextWrapping.Wrap;
        var grid = new Grid { Margin = new Thickness(16), RowDefinitions = new RowDefinitions("Auto,*,Auto,Auto,Auto"), RowSpacing = 10 };
        var form = new StackPanel { Spacing = 8, Children = { Dialogs.Form(("Source", left), ("Destination", right)), contents } };
        grid.Children.Add(form); Grid.SetRow(rows, 1); grid.Children.Add(rows); Grid.SetRow(message, 2); grid.Children.Add(message);
        var options = Ui.Row(direction, replace); Grid.SetRow(options, 3); grid.Children.Add(options);
        var dialog = Dialogs.Window("Synchronize / Compare Folders", grid, 1000, 680);
        var compare = Ui.Button("Compare", () => { }); var synchronize = Ui.Button("Review copy plan…", () => { }); synchronize.IsEnabled = false;
        var cancel = Ui.Button("Cancel work", CancelWork); cancel.IsEnabled = false;
        var buttons = Ui.Row(Ui.Button("Close", () => dialog.Close()), cancel, compare, synchronize); buttons.HorizontalAlignment = HorizontalAlignment.Right;
        Grid.SetRow(buttons, 4); grid.Children.Add(buttons);
        FolderComparisonResult? result = null;
        var service = new FolderComparisonService(); var busy = false;
        void Invalidate() { result = null; synchronize.IsEnabled = false; }
        left.TextChanged += (_, _) => Invalidate(); right.TextChanged += (_, _) => Invalidate(); contents.IsCheckedChanged += (_, _) => Invalidate();
        dialog.Closing += (_, e) => { if (busy) { e.Cancel = true; CancelWork(); message.Text = "Canceling work; waiting for completion…"; } };
        compare.Click += async (_, _) =>
        {
            if (busy) return;
            busy = true; compare.IsEnabled = false; synchronize.IsEnabled = false; form.IsEnabled = false; cancel.IsEnabled = true;
            try
            {
                var source = DirectoryBrowser.ResolvePath(left.Text ?? "", ActivePane.CurrentDirectory);
                var destination = DirectoryBrowser.ResolvePath(right.Text ?? "", OtherPane.CurrentDirectory);
                result = await RunToolAsync("Comparing folders", (token, progress) => service.CompareAsync(source, destination,
                    new FolderComparisonOptions(CompareContents: contents.IsChecked == true, IncludeHidden: _settings.ShowHidden), progress, token));
                rows.ItemsSource = result.Items;
                message.Text = $"{(result.ComparedContents ? "Content comparison" : "Metadata-only comparison")} · {result.Items.Count:N0} entries · {result.Errors.Count:N0} errors"
                    + (result.Truncated ? " · Scan limit reached" : "");
                synchronize.IsEnabled = !result.Truncated && result.Errors.Count == 0;
                if (result.Errors.Count > 0) message.Text += " · " + result.Errors[0];
            }
            catch (OperationCanceledException) { message.Text = "Comparison canceled"; }
            catch (Exception error) { message.Text = error.Message; }
            finally { busy = false; compare.IsEnabled = true; form.IsEnabled = true; cancel.IsEnabled = false; }
        };
        synchronize.Click += async (_, _) =>
        {
            if (busy || result is null) return;
            try
            {
                var plan = service.BuildCopyPlan(result, direction.SelectedIndex == 0 ? ComparisonDirection.LeftToRight : ComparisonDirection.RightToLeft, replace.IsChecked == true);
                if (plan.Requests.Count == 0) { message.Text = "No copy operations are needed. " + string.Join("; ", plan.Warnings); return; }
                var sourceRoot = direction.SelectedIndex == 0 ? result.LeftDirectory : result.RightDirectory;
                var destinationRoot = direction.SelectedIndex == 0 ? result.RightDirectory : result.LeftDirectory;
                if (!await ConfirmCopyPlanAsync(dialog, plan, sourceRoot, destinationRoot)) return;
                busy = true; compare.IsEnabled = false; synchronize.IsEnabled = false; form.IsEnabled = false; options.IsEnabled = false; cancel.IsEnabled = true;
                var completed = await RunToolAsync("Applying synchronization plan", async (token, _) =>
                {
                    var count = 0;
                    foreach (var request in plan.Requests)
                    {
                        token.ThrowIfCancellationRequested();
                        var operation = await RunOperationAsync(request, token, refresh: false, rememberSelection: false);
                        if (operation.Canceled) throw new OperationCanceledException(token);
                        if (!operation.Succeeded) throw new IOException($"Stopped after {count} operation(s). " + string.Join("; ", operation.Errors));
                        count++;
                    }
                    return count;
                });
                message.Text = $"Synchronization completed · {completed} operation(s). Compare again to refresh the result.";
                result = null;
            }
            catch (OperationCanceledException) { message.Text = "Synchronization canceled; no further operations will start. Completed changes remain recorded in History."; }
            catch (Exception error) { message.Text = error.Message; }
            finally { if (busy) await RefreshPanesAsync(afterFileOperation: true); busy = false; compare.IsEnabled = true; form.IsEnabled = true; options.IsEnabled = true; cancel.IsEnabled = false; }
        };
        await dialog.ShowDialog(this);
    }

    private static async Task<bool> ConfirmCopyPlanAsync(Window owner, FolderCopyPlan plan, string sourceRoot, string destinationRoot,
        string title = "Confirm Synchronization")
    {
        var rows = Ui.Grid(("Operation", nameof(CopyPlanRow.Operation), 130), ("Source", nameof(CopyPlanRow.Source), -1), ("Destination", nameof(CopyPlanRow.Destination), -1));
        rows.Columns[1] = Ui.ReadableColumn<CopyPlanRow>("Source", row => row.Source);
        rows.Columns[2] = Ui.ReadableColumn<CopyPlanRow>("Destination", row => row.Destination);
        rows.ItemsSource = plan.Requests.Select(request =>
        {
            var destinationPaths = request.NewName is not null ? new[] { Path.Combine(request.DestinationDirectory ?? "", request.NewName) }
                : request.Sources.Select(source => Path.Combine(request.DestinationDirectory ?? "", Path.GetFileName(source))).ToArray();
            return new CopyPlanRow(request.Kind == FileOperationKind.CreateDirectory ? "Create folder" : request.Kind.ToString(),
                string.Join("; ", request.Sources.Select(source => Path.GetRelativePath(sourceRoot, source))),
                string.Join("; ", destinationPaths.Select(destination => Path.GetRelativePath(destinationRoot, destination))),
                string.Join("\n", request.Sources), string.Join("\n", destinationPaths));
        }).ToArray();
        var text = Ui.Text($"{plan.Requests.Count:N0} operation(s). " + (plan.IncludesReplacement ? "Changed destination files will be replaced. " : "")
            + $"Review every operation below.\nSource: {sourceRoot}\nDestination: {destinationRoot}", true);
        text.TextWrapping = TextWrapping.Wrap;
        var detail = new TextBox { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Height = 90 };
        rows.SelectionChanged += (_, _) => { if (rows.SelectedItem is CopyPlanRow row) detail.Text = "Source:\n" + row.FullSource + "\nDestination:\n" + row.FullDestination; };
        rows.SelectedIndex = 0;
        var warnings = new TextBox { IsReadOnly = true, AcceptsReturn = true, Text = string.Join("\n", plan.Warnings), Height = 90, IsVisible = plan.Warnings.Count > 0 };
        var grid = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto,Auto,Auto"), Margin = new Thickness(16), RowSpacing = 10 };
        grid.Children.Add(text); Grid.SetRow(rows, 1); grid.Children.Add(rows); Grid.SetRow(detail, 2); grid.Children.Add(detail); Grid.SetRow(warnings, 3); grid.Children.Add(warnings);
        var dialog = Dialogs.Window(title, grid, 970, 610);
        var buttons = Ui.Row(Ui.Button("Cancel", () => dialog.Close(false)), Ui.Button("Apply copy plan", () => dialog.Close(true)));
        buttons.HorizontalAlignment = HorizontalAlignment.Right; Grid.SetRow(buttons, 4); grid.Children.Add(buttons);
        return await dialog.ShowDialog<bool>(owner);
    }

    private sealed record RenameRow(string OldName, string NewName, string Folder, string Issue);
    private sealed record CopyPlanRow(string Operation, string Source, string Destination, string FullSource, string FullDestination);
}
