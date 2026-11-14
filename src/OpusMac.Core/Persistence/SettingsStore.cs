using System.Text.Json;
using OpusMac.Core.Browsing;

namespace OpusMac.Core.Persistence;

public sealed class SettingsStore
{
    private const int CurrentSchemaVersion = 2;
    private const long MaximumBytes = 2 * 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, PropertyNameCaseInsensitive = true, MaxDepth = 32 };
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _preserveCorruptFile;
    public string StateDirectory { get; }
    public string SettingsPath => Path.Combine(StateDirectory, "settings.json");
    public string? LastLoadWarning { get; private set; }
    public bool CanSave { get; private set; } = true;

    public SettingsStore(string? stateDirectory = null)
    {
        StateDirectory = Path.GetFullPath(stateDirectory ?? DefaultStateDirectory());
    }

    public static string DefaultStateDirectory() => OperatingSystem.IsMacOS()
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Application Support", "OpusMac")
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OpusMac");

    public AppSettings Load()
    {
        LastLoadWarning = null;
        CanSave = true;
        _preserveCorruptFile = false;
        if (!File.Exists(SettingsPath)) return new AppSettings();
        try { return ReadSettings(SettingsPath); }
        catch (UnsupportedSettingsException e)
        {
            CanSave = false;
            LastLoadWarning = e.Message + " The stored settings will not be overwritten.";
            return new AppSettings();
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException or ArgumentException)
        {
            _preserveCorruptFile = true;
            LastLoadWarning = $"Settings could not be loaded: {e.Message} The original will be retained.";
            try
            {
                var backup = ReadSettings(SettingsPath + ".bak");
                LastLoadWarning += " Loaded the last saved backup.";
                return backup;
            }
            catch (Exception backupError) when (backupError is JsonException or IOException or UnauthorizedAccessException or ArgumentException or UnsupportedSettingsException)
            {
                return new AppSettings();
            }
        }
    }

    private static AppSettings ReadSettings(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 8192);
        if (stream.Length > MaximumBytes) throw new IOException("The settings file exceeds the 2 MB limit.");
        var settings = JsonSerializer.Deserialize<AppSettings>(stream, JsonOptions) ?? throw new JsonException("Settings must contain a JSON object.");
        if (settings.SchemaVersion is < 1 or > CurrentSchemaVersion)
            throw new UnsupportedSettingsException($"Settings schema {settings.SchemaVersion} is not supported by this version.");
        if (settings.SchemaVersion == 1) settings.SchemaVersion = CurrentSchemaVersion;
        Normalize(settings);
        return settings;
    }

    public async Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (!CanSave) throw new InvalidOperationException("A newer settings schema is present; saving is disabled.");
        // Serialize before awaiting: callers can continue changing their live window state.
        var snapshot = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(settings, JsonOptions), JsonOptions)!;
        if (snapshot.SchemaVersion == 1) snapshot.SchemaVersion = CurrentSchemaVersion;
        if (snapshot.SchemaVersion != CurrentSchemaVersion) throw new InvalidOperationException("Unsupported settings schema.");
        Normalize(snapshot);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(snapshot, JsonOptions);
        if (bytes.LongLength > MaximumBytes) throw new IOException("Settings exceed the 2 MB limit.");
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        string? temporary = null;
        try
        {
            Directory.CreateDirectory(StateDirectory);
            using var lease = new FileStream(Path.Combine(StateDirectory, "settings.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            // A separate application instance may have written a future schema after Load.
            if (File.Exists(SettingsPath) && !_preserveCorruptFile)
            {
                try { _ = ReadSettings(SettingsPath); }
                catch (UnsupportedSettingsException) { CanSave = false; throw; }
                catch (Exception e) when (e is JsonException or IOException or ArgumentException) { _preserveCorruptFile = true; }
            }
            temporary = Path.Combine(StateDirectory, $".settings-{Guid.NewGuid():N}.tmp");
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 8192, FileOptions.WriteThrough | FileOptions.Asynchronous))
            {
                await output.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                output.Flush(true);
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (File.Exists(SettingsPath))
            {
                if (_preserveCorruptFile)
                    File.Move(SettingsPath, SettingsPath + $".unreadable-{DateTime.UtcNow:yyyyMMddTHHmmss}-{Guid.NewGuid():N}");
                else File.Copy(SettingsPath, SettingsPath + ".bak", overwrite: true);
            }
            File.Move(temporary, SettingsPath, overwrite: true);
            temporary = null;
            _preserveCorruptFile = false;
        }
        finally
        {
            if (temporary is not null) { try { File.Delete(temporary); } catch (IOException) { } }
            _gate.Release();
        }
    }

    private static void Normalize(AppSettings settings)
    {
        (settings.WindowWidth, settings.WindowHeight) = NormalizeWindowSize(settings.WindowWidth, settings.WindowHeight);
        settings.StackedPaneShare = NormalizePaneShare(settings.StackedPaneShare);
        settings.SideBySidePaneShare = NormalizePaneShare(settings.SideBySidePaneShare);
        settings.TreeWidth = NormalizeSidebarWidth(settings.TreeWidth, 120, 420);
        settings.DestinationTreeWidth = NormalizeSidebarWidth(settings.DestinationTreeWidth, 120, 420);
        settings.ViewerWidth = NormalizeSidebarWidth(settings.ViewerWidth, 300, 960);
        settings.Theme = settings.Theme?.Trim().ToLowerInvariant() == "dark" ? "Dark" : "Light";
        (settings.SplitOrientation, settings.ActivePane, settings.Panes) =
            NormalizeLayout(settings.SplitOrientation, settings.ActivePane, settings.Panes);
        settings.Favorites ??= [];
        settings.Favorites = settings.Favorites.Where(f => f is not null && !string.IsNullOrWhiteSpace(f.Label) && !string.IsNullOrWhiteSpace(f.Path))
            .Take(500).DistinctBy(f => f.Path, OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal).ToList();
        settings.FolderColumnRules = (settings.FolderColumnRules ?? [])
            .Where(rule => rule is not null && !string.IsNullOrWhiteSpace(rule.Path) &&
                Path.IsPathFullyQualified(rule.Path) && !rule.Path.Contains('\0'))
            .Take(500)
            .Select(rule => new FolderColumnRule(
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(rule.Path)),
                rule.IncludeSubfolders, NormalizeColumns(rule.Columns), CanonicalColumnId(rule.SortColumn), rule.SortDescending,
                rule.ViewMode is "Details" or "Tiles" ? rule.ViewMode : null, rule.InheritColumns, rule.InheritSort,
                rule.InheritView, rule.Disabled, rule.CheckboxMode))
            .DistinctBy(rule => rule.Path, OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal)
            .ToList();
        if (!Enum.IsDefined(settings.AutomaticFolderSizes)) settings.AutomaticFolderSizes = FolderSizeMode.Off;
        settings.FolderSizeOverrides = (settings.FolderSizeOverrides ?? []).Where(rule => rule is not null).Take(500)
            .Select(rule => rule.Normalize()).OfType<FolderSizeOverride>()
            .DistinctBy(rule => rule.Path, FolderPair.Comparer).ToList();
        settings.HomeFolder = FolderPair.TryNormalize(settings.HomeFolder, out var home) ? home : null;
        settings.PrintFolderFont = settings.PrintFolderFont?.Normalize();
        settings.SavedFilenameFilters = (settings.SavedFilenameFilters ?? [])
            .Where(filter => filter is not null && !string.IsNullOrWhiteSpace(filter.Name) && !string.IsNullOrWhiteSpace(filter.Pattern))
            .Select(filter => new SavedFilenameFilter(filter.Name.Trim(), filter.Pattern.Trim()))
            .Where(filter => filter.Name.Length <= 80 && filter.Pattern.Length <= 1024)
            .DistinctBy(filter => filter.Name, StringComparer.OrdinalIgnoreCase).Take(200).ToList();
        settings.SavedDuplicateSearches = (settings.SavedDuplicateSearches ?? [])
            .Where(preset => preset is not null && !string.IsNullOrWhiteSpace(preset.Name) &&
                preset.Name.Trim().Length <= 80 && preset.Roots is { Length: > 0 } && preset.Options is not null &&
                Enum.IsDefined(preset.Options.Comparison) && preset.Options.Md5Accuracy is >= 1 and <= 100 &&
                preset.Options.MinimumSize >= 0)
            .Select(preset => preset with { Name = preset.Name.Trim() })
            .DistinctBy(preset => preset.Name, StringComparer.OrdinalIgnoreCase).Take(100).ToList();
        settings.DuplicatePreferredLocations = (settings.DuplicatePreferredLocations ?? [])
            .Where(path => !string.IsNullOrWhiteSpace(path) && Path.IsPathFullyQualified(path))
            .Distinct(FolderPair.Comparer).Take(100).ToList();
        settings.FolderAliases = (settings.FolderAliases ?? []).Where(alias => alias is not null).Take(500)
            .Select(alias => alias.Normalize()).OfType<FolderAlias>()
            .DistinctBy(alias => alias.Name, StringComparer.OrdinalIgnoreCase).ToList();
        settings.FolderPairs = (settings.FolderPairs ?? []).Where(pair => pair is not null).Take(500)
            .Select(pair => pair.Normalize())
            .OfType<FolderPair>().ToList();
        settings.UserDefaultFolderFormat = NormalizeFormat(settings.UserDefaultFolderFormat);
        settings.SavedListers ??= [];
        var seenListerNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var normalizedListers = new List<SavedLister>();
        foreach (var lister in settings.SavedListers.Where(lister => lister is not null))
        {
            var name = (lister.Name ?? "").Trim();
            if (name.Length == 0) continue;
            if (name.Length > 100) name = name[..100];
            if (!seenListerNames.Add(name)) continue;
            lister.Name = name;
            lister.StackedPaneShare = NormalizePaneShare(lister.StackedPaneShare);
            lister.SideBySidePaneShare = NormalizePaneShare(lister.SideBySidePaneShare);
            lister.TreeWidth = NormalizeSidebarWidth(lister.TreeWidth, 120, 420);
            lister.DestinationTreeWidth = NormalizeSidebarWidth(lister.DestinationTreeWidth, 120, 420);
            lister.ViewerWidth = NormalizeSidebarWidth(lister.ViewerWidth, 300, 960);
            (lister.WindowWidth, lister.WindowHeight) = NormalizeWindowSize(lister.WindowWidth, lister.WindowHeight);
            (lister.SplitOrientation, lister.ActivePane, lister.Panes) =
                NormalizeLayout(lister.SplitOrientation, lister.ActivePane, lister.Panes);
            normalizedListers.Add(lister);
            if (normalizedListers.Count == 100) break;
        }
        settings.SavedListers = normalizedListers;
    }

    public static double NormalizePaneShare(double share) => double.IsFinite(share) ? Math.Clamp(share, .2, .8) : .5;

    private static double? NormalizeSidebarWidth(double? width, double minimum, double maximum) =>
        width is { } value && double.IsFinite(value) && value >= minimum ? Math.Min(value, maximum) : null;

    private static (double Width, double Height) NormalizeWindowSize(double width, double height) =>
        (double.IsFinite(width) ? Math.Clamp(width, 800, 10000) : 1400,
         double.IsFinite(height) ? Math.Clamp(height, 500, 10000) : 860);

    private static (string SplitOrientation, int ActivePane, List<PaneSettings> Panes) NormalizeLayout(
        string splitOrientation, int activePane, List<PaneSettings>? panes)
    {
        splitOrientation = splitOrientation == "Horizontal" ? "Horizontal" : "Vertical";
        panes ??= [];
        panes = panes.Where(p => p is not null).Take(2).ToList();
        while (panes.Count < 2) panes.Add(new PaneSettings());
        activePane = Math.Clamp(activePane, 0, 1);
        foreach (var pane in panes) NormalizePane(pane);
        var tabs = panes.SelectMany((pane, index) => pane.Tabs.Select(tab => (Tab: tab, Side: index)));
        foreach (var group in tabs.Where(entry => entry.Tab.LinkedTabGroup.HasValue).GroupBy(entry => entry.Tab.LinkedTabGroup))
            if (group.Key == Guid.Empty || group.Count() != 2 || group.Select(entry => entry.Side).Distinct().Count() != 2)
                foreach (var entry in group) entry.Tab.LinkedTabGroup = null;
        return (splitOrientation, activePane, panes);
    }

    private static void NormalizePane(PaneSettings pane)
    {
        pane.Tabs ??= [];
        pane.Tabs = pane.Tabs.Where(t => t is not null).Take(100).ToList();
        if (pane.Tabs.Count == 0) pane.Tabs.Add(new TabSettings());
        pane.ActiveTab = Math.Clamp(pane.ActiveTab, 0, pane.Tabs.Count - 1);
        foreach (var tab in pane.Tabs)
        {
            try { tab.Path = DirectoryBrowser.ResolvePath(tab.Path); }
            catch (Exception e) when (e is ArgumentException or NotSupportedException or System.Security.SecurityException) { tab.Path = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile); }
            tab.ViewMode = tab.ViewMode?.Trim().ToLowerInvariant() switch
            {
                "tiles" or "icons" or "thumbnails" => "Tiles",
                "filmstrip" => "Filmstrip",
                _ => "Details"
            };
            tab.SortColumn = tab.SortColumn?.Trim().ToLowerInvariant() switch
            {
                "size" or "length" => "Size",
                "type" or "kind" => "Type",
                "modified" => "Modified",
                "created" => "Created",
                "attr" or "attribute" or "attributes" => "Attr",
                "path" => "Path",
                _ => "Name"
            };
            tab.Columns = NormalizeColumns(tab.Columns);
            tab.FormatLock = NormalizeFormat(tab.FormatLock);
        }
    }

    private static LockedFolderFormat? NormalizeFormat(LockedFolderFormat? format) => format is null ? null : format with
    {
        ViewMode = format.ViewMode is "Tiles" or "Filmstrip" ? format.ViewMode : "Details",
        SortColumn = CanonicalColumnId(format.SortColumn) ?? "Name",
        Columns = NormalizeColumns(format.Columns)
    };

    private static List<FileColumnSettings> NormalizeColumns(List<FileColumnSettings>? columns)
    {
        var defaults = FileColumnSettings.CreateDefaultLayout();
        var sources = (columns ?? []).Where(column => column is not null).Take(32).ToList();
        if (IsUntouchedLegacyDefault(sources)) return defaults;
        var fallbackById = defaults.ToDictionary(column => column.Id, StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var normalized = new List<FileColumnSettings>(defaults.Count);
        foreach (var source in sources)
        {
            var id = string.Equals(source.Id?.Trim(), "Thumbnail", StringComparison.OrdinalIgnoreCase)
                ? "Thumbnail" : CanonicalColumnId(source.Id);
            if (id is null || !seen.Add(id)) continue;
            var fallback = fallbackById[id];
            var widthMode = string.Equals(source.WidthMode?.Trim(), "Star", StringComparison.OrdinalIgnoreCase) ? "Star" : "Pixel";
            var width = double.IsFinite(source.Width) ? source.Width : fallback.Width;
            width = widthMode == "Star"
                ? Math.Clamp(width, 0.25, 8)
                : Math.Clamp(width, MinimumColumnWidth(id), 1200);
            normalized.Add(new FileColumnSettings
            {
                Id = id,
                Visible = id == "Name" || source.Visible,
                Width = width,
                WidthMode = widthMode
            });
        }
        foreach (var fallback in defaults)
        {
            if (!seen.Add(fallback.Id)) continue;
            var appended = fallback.Copy();
            // Existing custom six-column layouts predate Attr. Preserve their
            // visible surface and append the new capability hidden; fresh tabs
            // and the untouched old default take the reference seven-column layout.
            if (sources.Count > 0 && appended.Id == "Attr") appended.Visible = false;
            normalized.Add(appended);
        }
        return normalized;
    }

    private static bool IsUntouchedLegacyDefault(IReadOnlyList<FileColumnSettings> columns)
    {
        if (columns.Count != 6) return false;
        var expected = new (string Id, bool Visible, double Width, string WidthMode)[]
        {
            ("Name", true, 1, "Star"), ("Size", true, 85, "Pixel"),
            ("Modified", true, 148, "Pixel"), ("Type", true, 100, "Pixel"),
            ("Created", false, 148, "Pixel"), ("Path", false, 260, "Pixel")
        };
        for (var index = 0; index < expected.Length; index++)
        {
            var column = columns[index];
            var item = expected[index];
            if (!string.Equals(column.Id, item.Id, StringComparison.OrdinalIgnoreCase) || column.Visible != item.Visible)
                return false;
            var mode = string.Equals(column.WidthMode?.Trim(), "Star", StringComparison.OrdinalIgnoreCase) ? "Star" : "Pixel";
            if (mode != item.WidthMode || !double.IsFinite(column.Width) || Math.Abs(column.Width - item.Width) >= 0.01)
                return false;
        }
        return true;
    }

    private static string? CanonicalColumnId(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "name" => "Name",
        "size" or "length" => "Size",
        "modified" or "date modified" => "Modified",
        "created" or "date created" => "Created",
        "type" or "kind" => "Type",
        "attr" or "attribute" or "attributes" => "Attr",
        "path" => "Path",
        _ => null
    };

    internal static double MinimumColumnWidth(string id) => id switch
    {
        "Name" => 180,
        "Modified" or "Created" => 110,
        "Path" => 140,
        _ => 60
    };

    private sealed class UnsupportedSettingsException(string message) : Exception(message);
}
