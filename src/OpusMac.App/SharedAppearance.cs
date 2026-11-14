using System.Text.Json;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;

namespace OpusMac.App;

internal sealed class SharedAppearance
{
    private const string Schema = "spiffy_shared_appearance_v1";
    private const long MaximumBytes = 64 * 1024;
    private static readonly IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> Palettes =
        new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.Ordinal)
        {
            ["broadcast_dark"] = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["OpusSurface"] = "#1C1C1C",
                ["OpusInset"] = "#111111",
                ["OpusLine"] = "#3A3A3A",
                ["OpusMuted"] = "#8E8E8E",
                ["OpusInactive"] = "#242424"
            },
            ["listary_slate"] = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["OpusSurface"] = "#1E242D",
                ["OpusInset"] = "#1A2029",
                ["OpusLine"] = "#3C4351",
                ["OpusMuted"] = "#9AA7B8",
                ["OpusInactive"] = "#28303C"
            }
        };

    private SharedAppearance(
        bool usesSharedProfile,
        string profileId,
        string displayName,
        string status,
        string settingsPath,
        string detail,
        string legacyTheme)
    {
        UsesSharedProfile = usesSharedProfile;
        ProfileId = profileId;
        DisplayName = displayName;
        Status = status;
        SettingsPath = settingsPath;
        Detail = detail;
        LegacyTheme = legacyTheme;
    }

    public bool UsesSharedProfile { get; }
    public string ProfileId { get; }
    public string DisplayName { get; }
    public string Status { get; }
    public string SettingsPath { get; }
    public string Detail { get; }
    public string LegacyTheme { get; }

    public static SharedAppearance Load(string legacyTheme)
    {
        legacyTheme = string.Equals(legacyTheme, "Light", StringComparison.OrdinalIgnoreCase) ? "Light" : "Dark";
        var path = ResolveSettingsPath();
        if (!File.Exists(path))
            return Fallback(legacyTheme, path, "shared_missing", "Shared appearance settings are absent; using the existing Directory Opus preference.");

        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 8192);
            if (stream.Length > MaximumBytes)
                return Fallback(legacyTheme, path, "shared_invalid", "Shared appearance settings exceed the 64 KB read limit; using the existing Directory Opus preference.");
            using var document = JsonDocument.Parse(stream, new JsonDocumentOptions { MaxDepth = 8 });
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                return Fallback(legacyTheme, path, "shared_invalid", "Shared appearance settings are not a JSON object; using the existing Directory Opus preference.");

            var root = document.RootElement;
            var schema = root.TryGetProperty("schema", out var schemaValue) && schemaValue.ValueKind == JsonValueKind.String
                ? schemaValue.GetString() ?? ""
                : "";
            if (!string.Equals(schema, Schema, StringComparison.Ordinal))
                return Fallback(legacyTheme, path, "shared_stale_schema", "Shared appearance schema is unsupported; using the existing Directory Opus preference.");

            var profile = root.TryGetProperty("profile", out var profileValue) && profileValue.ValueKind == JsonValueKind.String
                ? (profileValue.GetString() ?? "").Trim().ToLowerInvariant()
                : "";
            if (!Palettes.ContainsKey(profile))
                return Fallback(legacyTheme, path, "shared_invalid_profile", "Shared appearance profile is unknown; using the existing Directory Opus preference.");

            return new SharedAppearance(
                true,
                profile,
                profile == "listary_slate" ? "Listary Slate" : "Broadcast Dark",
                "shared_loaded",
                path,
                "Loaded the canonical Spiffy shared appearance profile.",
                legacyTheme);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            return Fallback(
                legacyTheme,
                path,
                "shared_invalid",
                $"Shared appearance settings could not be read ({error.GetType().Name}); using the existing Directory Opus preference.");
        }
    }

    public void Apply(Application application)
    {
        ArgumentNullException.ThrowIfNull(application);
        if (!UsesSharedProfile)
        {
            application.RequestedThemeVariant = LegacyTheme == "Light" ? ThemeVariant.Light : ThemeVariant.Dark;
            return;
        }

        application.RequestedThemeVariant = ThemeVariant.Dark;
        foreach (var (key, value) in Palettes[ProfileId])
            application.Resources[key] = new SolidColorBrush(Color.Parse(value));
    }

    public void ApplyWindowChrome(Window window)
    {
        // Window decorations are owned by Windows, outside Avalonia's brushes.
        // Dark title-bar rendering follows the existing shared/legacy dark preference.
        if (!OperatingSystem.IsWindows() || (!UsesSharedProfile && LegacyTheme == "Light"))
            return;
        var handle = window.TryGetPlatformHandle()?.Handle ?? nint.Zero;
        if (handle == nint.Zero)
            return;
        try
        {
            var dark = 1;
            if (DwmSetWindowAttribute(handle, 20, ref dark, sizeof(int)) != 0)
                _ = DwmSetWindowAttribute(handle, 19, ref dark, sizeof(int));
        }
        catch (DllNotFoundException) { }
        catch (EntryPointNotFoundException) { }
    }

    [DllImport("dwmapi.dll", ExactSpelling = true)]
    private static extern int DwmSetWindowAttribute(nint window, int attribute, ref int value, int size);

    private static SharedAppearance Fallback(string legacyTheme, string path, string status, string detail) =>
        new(
            false,
            legacyTheme == "Light" ? "legacy_light" : "legacy_dark",
            legacyTheme,
            status,
            path,
            detail,
            legacyTheme);

    private static string ResolveSettingsPath()
    {
        var overrideRoot = Environment.GetEnvironmentVariable("SS_WORKFLOW_SETTINGS_ROOT")?.Trim();
        if (!string.IsNullOrWhiteSpace(overrideRoot))
            return Path.GetFullPath(Path.Combine(Environment.ExpandEnvironmentVariables(overrideRoot), "appearance.json"));

        string root;
        if (OperatingSystem.IsWindows())
        {
            root = Environment.GetEnvironmentVariable("APPDATA")?.Trim() ?? "";
            if (string.IsNullOrWhiteSpace(root))
                root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "AppData", "Roaming");
        }
        else if (OperatingSystem.IsMacOS())
        {
            root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Application Support");
        }
        else
        {
            root = Environment.GetEnvironmentVariable("XDG_DATA_HOME")?.Trim() ?? "";
            if (string.IsNullOrWhiteSpace(root))
                root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");
        }
        return Path.GetFullPath(Path.Combine(root, "SpiffyScenes", "settings", "appearance.json"));
    }
}
