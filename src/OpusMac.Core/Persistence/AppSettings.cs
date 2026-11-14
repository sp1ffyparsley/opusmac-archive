using System.IO.Enumeration;
using System.Text.RegularExpressions;
using OpusMac.Core.Tools;

namespace OpusMac.Core.Persistence;

public sealed class AppSettings
{
    public int SchemaVersion { get; set; } = 2;
    public bool DualPane { get; set; } = true;
    public string SplitOrientation { get; set; } = "Vertical";
    public bool ShowTree { get; set; } = true;
    public bool ShowFavoritesBar { get; set; } = true;
    public string? HomeFolder { get; set; }
    public FolderSizeMode AutomaticFolderSizes { get; set; }
    public bool CacheFolderSizesInHistory { get; set; }
    public bool UpdateFolderSizesAfterOperations { get; set; }
    public bool IgnoreLinksBelowSizedFolders { get; set; } = true;
    public bool IgnoreLinkedFoldersForAutomaticSizing { get; set; } = true;
    public List<FolderSizeOverride> FolderSizeOverrides { get; set; } = [];
    public LockedFolderFormat? PrintFolderFormat { get; set; }
    public FolderListingPrintFont? PrintFolderFont { get; set; }
    public List<SavedFilenameFilter> SavedFilenameFilters { get; set; } = [];
    public List<SavedDuplicateSearch> SavedDuplicateSearches { get; set; } = [];
    public List<string> DuplicatePreferredLocations { get; set; } = [];

    public FolderSizeMode ResolveFolderSizeMode(string directory)
    {
        var path = Path.TrimEndingDirectorySeparator(directory);
        var rule = FolderSizeOverrides.FirstOrDefault(item => !item.IsWildcard && FolderPair.Comparer.Equals(item.Path, path))
            ?? FolderSizeOverrides.FirstOrDefault(item => item.IsWildcard &&
                FileSystemName.MatchesSimpleExpression(item.Path.Replace(Path.DirectorySeparatorChar, '/'),
                    path.Replace(Path.DirectorySeparatorChar, '/'), ignoreCase: OperatingSystem.IsWindows()));
        return rule is null ? AutomaticFolderSizes : rule.Enabled ? FolderSizeMode.All : FolderSizeMode.Off;
    }
    public List<FolderAlias> FolderAliases { get; set; } = [];
    public List<FolderPair> FolderPairs { get; set; } = [];
    public bool WarnWhenNavigationLockOutOfSync { get; set; } = true;
    public bool CancelNavigationLockWhenNavigatingAway { get; set; }
    public bool IndependentFolderTrees { get; set; }
    public bool ShowViewer { get; set; } = true;
    public bool ShowHidden { get; set; }
    public string Theme { get; set; } = "Light";
    public double StackedPaneShare { get; set; } = .5;
    public double SideBySidePaneShare { get; set; } = .5;
    public double? TreeWidth { get; set; }
    public double? DestinationTreeWidth { get; set; }
    public double? ViewerWidth { get; set; }
    public double WindowWidth { get; set; } = 1400;
    public double WindowHeight { get; set; } = 860;
    public int ActivePane { get; set; }
    public List<PaneSettings> Panes { get; set; } = [new(), new()];
    public List<FavoriteFolder> Favorites { get; set; } = [];
    public List<FolderColumnRule> FolderColumnRules { get; set; } = [];
    public LockedFolderFormat? UserDefaultFolderFormat { get; set; }

    public string ExpandFolderAlias(string input, string stateDirectory, bool resolveVolumeLabel = false,
        string? initialHome = null)
    {
        var value = input.Trim();
        if (!value.StartsWith('/') || value.StartsWith("//", StringComparison.Ordinal)) return input;
        var separator = value.IndexOfAny(['/', '\\'], 1);
        var name = separator < 0 ? value[1..] : value[1..separator];
        var target = resolveVolumeLabel && name.StartsWith('$')
            ? FindMountedVolume(name[1..]) : FindFolderAlias(name, stateDirectory, initialHome);
        if (string.IsNullOrWhiteSpace(target)) return input;
        if (separator < 0) return target;
        var suffix = value[(separator + 1)..].TrimStart('/', '\\').Replace('\\', Path.DirectorySeparatorChar);
        return Path.IsPathRooted(suffix) ? input : Path.Combine(target, suffix);
    }

    private static string? FindMountedVolume(string label)
    {
        if (label.Length == 0 || (!OperatingSystem.IsWindows() && !OperatingSystem.IsMacOS())) return null;
        string? result = null;
        foreach (var drive in DriveInfo.GetDrives())
        {
            var root = Path.TrimEndingDirectorySeparator(drive.Name);
            try
            {
                string? name = null;
                if (OperatingSystem.IsWindows())
                {
                    if (drive.IsReady) name = drive.VolumeLabel;
                }
                else if (string.Equals(Path.GetDirectoryName(root), "/Volumes", StringComparison.Ordinal))
                    name = Path.GetFileName(root);
                if (!string.Equals(name, label, StringComparison.OrdinalIgnoreCase)) continue;
                if (result is not null) return null;
                result = drive.Name;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
        return result;
    }

    public string? ExpandRegexFolderAlias(string pattern, string stateDirectory, bool replacement)
    {
        var start = !replacement && pattern.StartsWith('^') ? 1 : 0;
        if (start >= pattern.Length || pattern[start] != '/') return pattern;
        var end = start + 1;
        string? target;
        if (end < pattern.Length && pattern[end] == '$')
        {
            var labelStart = end + 1;
            end = pattern.IndexOfAny(['/', '\\'], labelStart);
            if (end < 0) end = !replacement && pattern.EndsWith('$') ? pattern.Length - 1 : pattern.Length;
            if (end < labelStart) return null;
            target = FindMountedVolume(pattern[labelStart..end]);
            if (target is null) return null;

            // A Windows volume root already has its separator. The escaped
            // separator in the rule must not create a doubled path boundary.
            if (OperatingSystem.IsWindows() && Path.EndsInDirectorySeparator(target) &&
                pattern.AsSpan(end).StartsWith(@"\\", StringComparison.Ordinal)) end += 2;
        }
        else
        {
            while (end < pattern.Length && (char.IsLetterOrDigit(pattern[end]) || pattern[end] is '_' or '-' or '.'))
                end++;
            target = FindFolderAlias(pattern[(start + 1)..end], stateDirectory);
            if (string.IsNullOrWhiteSpace(target)) return pattern;
        }
        var escaped = replacement ? target.Replace(@"\", @"\\", StringComparison.Ordinal) : Regex.Escape(target);
        return pattern[..start] + escaped + pattern[end..];
    }

    public static IEnumerable<FolderAlias> BuiltInFolderAliases(string stateDirectory)
    {
        foreach (var name in new[] { "home", "desktop", "mydocuments", "dopusdata" })
            if (FindBuiltInFolderAlias(name, stateDirectory) is { Length: > 0 } target)
                yield return new FolderAlias(name, target);
    }

    private string? FindFolderAlias(string name, string stateDirectory, string? initialHome = null) =>
        FolderAliases.FirstOrDefault(alias => string.Equals(alias.Name, name, StringComparison.OrdinalIgnoreCase))?.Path
        ?? (string.Equals(name, "homedir", StringComparison.OrdinalIgnoreCase)
            ? HomeFolder ?? initialHome : FindBuiltInFolderAlias(name, stateDirectory));

    private static string? FindBuiltInFolderAlias(string name, string stateDirectory) =>
        name.ToLowerInvariant() switch
            {
                "home" => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "desktop" => Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                "mydocuments" => Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                "dopusdata" => stateDirectory,
                _ => null
            };

    public FolderColumnRule? ResolveFolderFormat(string directory, Func<FolderColumnRule, bool>? suppliesSetting)
    {
        var rule = FolderColumnRule.Resolve(FolderColumnRules, directory, suppliesSetting);
        if (rule is not null || UserDefaultFolderFormat is not { } format) return rule;
        var fallback = new FolderColumnRule("", false, format.Columns, format.SortColumn, format.Descending, format.ViewMode,
            CheckboxMode: format.CheckboxMode);
        return suppliesSetting is null || suppliesSetting(fallback) ? fallback : null;
    }
    public List<SavedLister> SavedListers { get; set; } = [];
}

public enum FolderSizeMode { Off, LocalAndRemovable, All }

public sealed record FolderSizeOverride(string Path, bool Enabled)
{
    public bool IsWildcard => Path.Contains('*') || Path.Contains('?');
    public override string ToString() => $"{(Enabled ? "Calculate" : "Skip")} · {Path}";

    public FolderSizeOverride? Normalize()
    {
        var text = Path?.Trim();
        if (string.IsNullOrWhiteSpace(text) || text.Length > 4096 || text.Contains('\0')) return null;
        if (!text.Contains('*') && !text.Contains('?'))
            return FolderPair.TryNormalize(text, out var path) ? this with { Path = path } : null;
        if (!System.IO.Path.IsPathFullyQualified(text)) return null;
        return this with
        {
            Path = System.IO.Path.TrimEndingDirectorySeparator(
                text.Replace(System.IO.Path.AltDirectorySeparatorChar, System.IO.Path.DirectorySeparatorChar))
        };
    }
}

public sealed class PaneSettings
{
    public List<TabSettings> Tabs { get; set; } = [new()];
    public int ActiveTab { get; set; }
}

public sealed class TabSettings
{
    public string Path { get; set; } = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    public string ViewMode { get; set; } = "Details";
    public bool CheckboxMode { get; set; }
    // The installed 13.22.11 reference at 96 DPI presents both fresh
    // listers by Modified descending. These are initialization defaults only:
    // saved per-tab sort/direction still deserialize over these values.
    public string SortColumn { get; set; } = "Modified";
    public bool Descending { get; set; } = true;
    public bool Locked { get; set; }
    public Guid? LinkedTabGroup { get; set; }
    public LockedFolderFormat? FormatLock { get; set; }
    public List<FileColumnSettings> Columns { get; set; } = FileColumnSettings.CreateDefaultLayout();
}

public sealed record FolderListingPrintFont(string? PostScriptName, double PointSize)
{
    public static FolderListingPrintFont Default { get; } = new(null, 9);

    public FolderListingPrintFont Normalize()
    {
        var name = PostScriptName?.Trim();
        return new FolderListingPrintFont(
            name is { Length: > 0 and <= 256 } && !name.Contains('\0') ? name : null,
            double.IsFinite(PointSize) ? Math.Clamp(PointSize, 6, 72) : Default.PointSize);
    }
}

public sealed record SavedDuplicateSearch(string Name, string[] Roots, DuplicateOptions Options,
    bool NumberGroups, bool Destination, bool NewTab)
{
    public override string ToString() => Name;
}

public sealed record SavedFilenameFilter(string Name, string Pattern)
{
    public override string ToString() => Name;
}

public sealed record LockedFolderFormat(string ViewMode, string SortColumn, bool Descending, List<FileColumnSettings> Columns,
    bool? CheckboxMode = null)
{
    public LockedFolderFormat Copy() => this with { Columns = Columns.Select(column => column.Copy()).ToList() };
}

public sealed record FolderAlias(string Name, string Path)
{
    public override string ToString() => $"/{Name} → {Path}";

    public FolderAlias? Normalize()
    {
        var name = (Name ?? "").Trim().TrimStart('/');
        if (name.Length is 0 or > 64 || name is "." or ".." ||
            !name.All(character => char.IsLetterOrDigit(character) || character is '_' or '-' or '.')) return null;
        return FolderPair.TryNormalize(Path, out var path) ? new FolderAlias(name, path) : null;
    }
}

public enum MissingFolderPairTarget { UsePath, ExistingParent, IgnorePair }

public sealed record FolderPair(string Source, string Destination, bool IncludeSubfolders,
    bool DefaultDual, bool DefaultNavigationLock, bool DefaultSynchronize, bool LockOnDual,
    MissingFolderPairTarget MissingTarget = MissingFolderPairTarget.UsePath, bool PrimaryOnLeft = false, bool RegularExpression = false,
    bool ReadIntoExistingDual = false, bool SwitchToDualAutomatically = false, bool LinkPairedTabs = false, bool ExpandRegexAliases = false)
{
    public override string ToString() => RegularExpression ? $"{Source} → {Destination}" : $"{Source} ↔ {Destination}";

    public FolderPair? Normalize()
    {
        var rule = this with
        {
            LockOnDual = DefaultDual && LockOnDual, PrimaryOnLeft = DefaultDual && PrimaryOnLeft,
            ReadIntoExistingDual = DefaultDual && ReadIntoExistingDual,
            SwitchToDualAutomatically = DefaultDual && SwitchToDualAutomatically,
            ExpandRegexAliases = RegularExpression && ExpandRegexAliases,
            MissingTarget = Enum.IsDefined(MissingTarget) ? MissingTarget : MissingFolderPairTarget.UsePath
        };
        if (RegularExpression)
        {
            if (string.IsNullOrWhiteSpace(Source) || Source.Length > 2048 ||
                string.IsNullOrWhiteSpace(Destination) || Destination.Length > 4096 ||
                Source.Contains('\0') || Destination.Contains('\0')) return null;
            try { _ = CreateRegex(Source); }
            catch (ArgumentException) { return null; }
            return rule with { IncludeSubfolders = false };
        }
        return TryNormalizeLiteral(Source, out var source) && TryNormalizeLiteral(Destination, out var destination) &&
            !Comparer.Equals(source, destination) ? rule with { Source = source, Destination = destination } : null;
    }

    private static bool TryNormalizeLiteral(string? value, out string path)
    {
        var text = value?.Trim();
        if (text is { Length: > 1 } && text[0] == '/' && !text.StartsWith("//", StringComparison.Ordinal) &&
            !text.Contains('\0'))
        {
            path = text;
            return true;
        }
        return TryNormalize(value, out path);
    }

    private static Regex CreateRegex(string pattern) => new($"^(?:{pattern})$",
        RegexOptions.ECMAScript | RegexOptions.CultureInvariant |
        (OperatingSystem.IsWindows() ? RegexOptions.IgnoreCase : RegexOptions.None), TimeSpan.FromMilliseconds(25));

    private string? ResolveRegex(string path)
    {
        try
        {
            var match = CreateRegex(Source).Match(path);
            if (!match.Success || match.Index != 0 || match.Length != path.Length) return null;
            var replacement = Regex.Replace(Destination, @"\\(\\|[0-9]+)|\$", token => token.Value switch
            {
                "$" => "$$",
                @"\\" => @"\",
                _ when int.TryParse(token.Groups[1].Value, out var index) && index < match.Groups.Count =>
                    "$" + "{" + index + "}",
                _ => throw new ArgumentException("The replacement refers to an undefined capture group.")
            });
            return TryNormalize(match.Result(replacement), out var target) && !Comparer.Equals(path, target) ? target : null;
        }
        catch (Exception error) when (error is ArgumentException or RegexMatchTimeoutException) { return null; }
    }

    public static bool TryNormalize(string? value, out string path)
    {
        path = "";
        if (string.IsNullOrWhiteSpace(value) || !Path.IsPathFullyQualified(value.Trim()) || value.Contains('\0'))
            return false;
        try { path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(value.Trim())); }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
        return true;
    }

    public static StringComparer Comparer => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    public string? Resolve(string currentDirectory) => Resolve(currentDirectory, out _);

    public string? Resolve(string currentDirectory, out bool fromPrimary)
    {
        var target = RegularExpression ? ResolveRegex(currentDirectory) : ResolveSide(Source, Destination);
        fromPrimary = target is not null;
        return RegularExpression ? target : target ?? ResolveSide(Destination, Source);

        string? ResolveSide(string source, string destination)
        {
            if (Comparer.Equals(source, Path.TrimEndingDirectorySeparator(currentDirectory))) return destination;
            if (!IncludeSubfolders) return null;
            var prefix = Path.EndsInDirectorySeparator(source) ? source : source + Path.DirectorySeparatorChar;
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            return currentDirectory.StartsWith(prefix, comparison)
                ? Path.Combine(destination, currentDirectory[prefix.Length..]) : null;
        }
    }
}

public sealed class FileColumnSettings
{
    public string Id { get; set; } = "Name";
    public bool Visible { get; set; } = true;
    public double Width { get; set; } = 180;
    public string WidthMode { get; set; } = "Pixel";

    public FileColumnSettings Copy() => new()
    {
        Id = Id,
        Visible = Visible,
        Width = Width,
        WidthMode = WidthMode
    };

    public static List<FileColumnSettings> CreateDefaultLayout() =>
    [
        new() { Id = "Name", Visible = true, Width = 1, WidthMode = "Star" },
        // Installed Opus reference at 2176px uses a readable Type and Attr
        // span; the previous defaults gave ~66% of the pane to Name alone.
        // These are initial widths only. Saved per-tab user widths still win.
        new() { Id = "Size", Visible = true, Width = 85 },
        new() { Id = "Type", Visible = true, Width = 240 },
        new() { Id = "Modified", Visible = true, Width = 145 },
        new() { Id = "Attr", Visible = true, Width = 180 },
        new() { Id = "Created", Visible = false, Width = 148 },
        new() { Id = "Path", Visible = false, Width = 260 },
        new() { Id = "Thumbnail", Visible = false, Width = 96 }
    ];
}

public sealed record FolderColumnRule(string Path, bool IncludeSubfolders, List<FileColumnSettings> Columns, string? SortColumn = null, bool SortDescending = false, string? ViewMode = null, bool InheritColumns = false,
    bool InheritSort = false, bool InheritView = false, bool Disabled = false, bool? CheckboxMode = null)
{
    public static FolderColumnRule? Resolve(IEnumerable<FolderColumnRule> rules, string directory, Func<FolderColumnRule, bool>? suppliesSetting = null) =>
        rules.Where(rule => !rule.Disabled && (suppliesSetting is null || suppliesSetting(rule))).Where(rule =>
        {
            var relative = System.IO.Path.GetRelativePath(rule.Path, directory);
            return relative == "." || rule.IncludeSubfolders &&
                !System.IO.Path.IsPathRooted(relative) && relative != ".." &&
                !relative.StartsWith(".." + System.IO.Path.DirectorySeparatorChar, StringComparison.Ordinal);
        }).MaxBy(rule => rule.Path.Length);
}

public sealed record FavoriteFolder(string Label, string Path);

public sealed class SavedLister
{
    public string Name { get; set; } = "";
    public DateTimeOffset SavedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public bool DualPane { get; set; } = true;
    public string SplitOrientation { get; set; } = "Vertical";
    public bool ShowTree { get; set; } = true;
    public bool IndependentFolderTrees { get; set; }
    public bool ShowViewer { get; set; } = true;
    public double StackedPaneShare { get; set; } = .5;
    public double SideBySidePaneShare { get; set; } = .5;
    public double? TreeWidth { get; set; }
    public double? DestinationTreeWidth { get; set; }
    public double? ViewerWidth { get; set; }
    public double WindowWidth { get; set; } = 1400;
    public double WindowHeight { get; set; } = 860;
    public int ActivePane { get; set; }
    public List<PaneSettings> Panes { get; set; } = [new(), new()];
}
