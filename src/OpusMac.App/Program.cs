using Avalonia;
using Avalonia.Headless;
using Avalonia.Threading;
using System.Diagnostics;
using System.Text.Json;
using OpusMac.Core.Integration;
using OpusMac.Core.Persistence;

namespace OpusMac.App;

internal static class Program
{
    public static LaunchOptions Options { get; private set; } = new();
    public static AppInstanceBroker? Broker { get; private set; }

    [STAThread]
    public static int Main(string[] args)
    {
        try
        {
            Options = LaunchOptions.Parse(args);
            if (Options.Help)
            {
                Console.WriteLine("OpusMac [folder [destination]] [--state-dir <directory>] [--self-test] [--render-check <output-directory>] [--preview-check <HEIF-path>] [--preview-cache-regression <HEIF-path>]");
                return 0;
            }
            if (Options.PreviewCheckFile is not null)
                return RunPreviewCheck(Options.PreviewCheckFile);
            if (Options.PreviewCacheRegressionFile is not null)
                return RunPreviewCacheRegression(Options.PreviewCacheRegressionFile);
            if (Options.SelfTest || Options.RenderDirectory is not null)
                return RunHeadless();
            using var broker = new AppInstanceBroker(new SettingsStore(Options.StateDirectory).StateDirectory);
            if (!broker.TryBecomePrimary())
            {
                broker.ForwardAsync(new OpenRequest(Options.Paths.ToArray())).GetAwaiter().GetResult();
                return 0;
            }
            Broker = broker;
            try { return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args); }
            finally { Broker = null; }
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            WriteStartupError(error);
            return 1;
        }
    }

    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>().UsePlatformDetect().LogToTrace();

    private static void WriteStartupError(Exception error)
    {
        try
        {
            var directory = Options.SelfTest || Options.RenderDirectory is not null
                ? Options.RenderDirectory ?? Path.Combine(Options.StateDirectory ?? Path.GetTempPath(), "opusmac-diagnostic-errors")
                : new SettingsStore(Options.StateDirectory).StateDirectory;
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, "startup-error-" + DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss-fff") + ".log");
            File.WriteAllText(path, error.ToString());
            Console.Error.WriteLine("Startup error recorded at: " + path);
        }
        catch (Exception logError) { Console.Error.WriteLine("Could not record the startup error: " + logError.Message); }
    }

    private static int RunHeadless()
    {
        AppBuilder.Configure<App>().UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
            .SetupWithoutStarting();
        using var lifetime = new CancellationTokenSource();
        var exitCode = 1;
        Dispatcher.UIThread.Post(async () =>
        {
            try { exitCode = await AppDiagnostics.RunAsync(Options); }
            catch (Exception error) { Console.Error.WriteLine(error); }
            finally { lifetime.Cancel(); }
        });
        try { Dispatcher.UIThread.MainLoop(lifetime.Token); }
        catch (OperationCanceledException) { }
        return exitCode;
    }

    private static int RunPreviewCheck(string path)
    {
        if (!NativeHeifPreview.Handles(path))
            throw new ArgumentException("--preview-check requires a .heic, .heif, or .hif file.");
        // Use the identical platform image backend as the actual viewer. No source
        // writes, dialog windows, filesystem watchers, or background tasks.
        AppBuilder.Configure<App>().UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
            .SetupWithoutStarting();
        NativeHeifPreview.ClearCache();
        var clock = Stopwatch.StartNew();
        using var first = NativeHeifPreview.Decode(path, 1600, CancellationToken.None);
        var coldMs = clock.ElapsedMilliseconds;
        var width = first.PixelSize.Width;
        var height = first.PixelSize.Height;
        clock.Restart();
        using var again = NativeHeifPreview.Decode(path, 1600, CancellationToken.None);
        var cachedMs = clock.ElapsedMilliseconds;
        clock.Restart();
        using var tile = NativeHeifPreview.Decode(path, 128, CancellationToken.None);
        var tileMs = clock.ElapsedMilliseconds;
        var cache = NativeHeifPreview.CacheStats();
        if (width < 1 || height < 1 || width > 1600 || height > 1600 ||
            again.PixelSize.Width != width || again.PixelSize.Height != height ||
            tile.PixelSize.Width < 1 || tile.PixelSize.Height < 1 ||
            tile.PixelSize.Width > 128 || tile.PixelSize.Height > 128 ||
            cache.Entries != 2 || cache.NativeDecodes != 1 ||
            cache.CacheHits != 2 || cache.DerivedThumbnails != 1)
            throw new IOException("The HEIF preview/cache verification failed.");
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        try
        {
            using var unexpectedlyDecoded = NativeHeifPreview.Decode(path, 1600, cancelled.Token);
            throw new IOException("A cancelled HEIF preview request was not refused.");
        }
        catch (OperationCanceledException) { }
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            format = "heif_preview_check_v2",
            result = "PASS",
            file = Path.GetFileName(path),
            width, height,
            firstDecodeMilliseconds = coldMs,
            cachedPreviewMilliseconds = cachedMs,
            derivedTileMilliseconds = tileMs,
            tileWidth = tile.PixelSize.Width,
            tileHeight = tile.PixelSize.Height,
            compressedCacheBytes = cache.Bytes,
            nativeDecodes = cache.NativeDecodes,
            cacheHits = cache.CacheHits,
            derivedThumbnails = cache.DerivedThumbnails,
            cancellationRefused = true
        }));
        return 0;
    }

    private static int RunPreviewCacheRegression(string original)
    {
        if (!NativeHeifPreview.Handles(original))
            throw new ArgumentException("--preview-cache-regression requires an HEIC/HEIF/HIF sample.");
        var sampleInfo = new FileInfo(original);
        if (!sampleInfo.Exists || sampleInfo.Length > 32L * 1024 * 1024)
            throw new IOException("The fixture must be an existing HEIF image no larger than 32 MB.");
        AppBuilder.Configure<App>().UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
            .SetupWithoutStarting();
        var tempFolder = Path.Combine(Path.GetTempPath(), "OpusMac-HEIF-regression-" +
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempFolder);
        var fixture = Path.Combine(tempFolder, Path.GetFileName(original));
        try
        {
            File.Copy(original, fixture);
            NativeHeifPreview.ClearCache();
            using var image = NativeHeifPreview.Decode(fixture, 1600, CancellationToken.None);
            using var tile = NativeHeifPreview.Decode(fixture, 128, CancellationToken.None);
            var before = NativeHeifPreview.CacheStats();
            if (before.NativeDecodes != 1 || before.DerivedThumbnails != 1 ||
                before.Entries != 2)
                throw new IOException("Cache setup failed before the file-modification check.");

            var stamp = File.GetLastWriteTimeUtc(fixture).AddSeconds(10);
            File.SetLastWriteTimeUtc(fixture, stamp);
            using var updated = NativeHeifPreview.Decode(fixture, 128, CancellationToken.None);
            var after = NativeHeifPreview.CacheStats();
            if (updated.PixelSize.Width > 128 || updated.PixelSize.Height > 128 ||
                after.NativeDecodes != 2 || after.CacheHits != 1 ||
                after.Entries != 1 || after.DerivedThumbnails != 1)
                throw new IOException("Changing a source did not invalidate all cached resolutions.");

            // Some filesystems cannot change birth/creation time. Check this
            // separate metadata invalidation path only where the host supports
            // changing it; never touch the original sample.
            using var fullAgain = NativeHeifPreview.Decode(fixture, 1600, CancellationToken.None);
            var originalCreated = File.GetCreationTimeUtc(fixture);
            var modifiedBeforeCreationTest = File.GetLastWriteTimeUtc(fixture);
            var creationTimeChecked = false;
            try
            {
                File.SetCreationTimeUtc(fixture, originalCreated.AddMinutes(5));
                File.SetLastWriteTimeUtc(fixture, modifiedBeforeCreationTest);
                creationTimeChecked = File.GetCreationTimeUtc(fixture) != originalCreated;
            }
            catch (Exception error) when (error is PlatformNotSupportedException or
                   UnauthorizedAccessException or IOException)
            {
                // Creation time is not writable on every filesystem.
            }
            if (creationTimeChecked)
            {
                using var creationChanged = NativeHeifPreview.Decode(
                    fixture, 128, CancellationToken.None);
                var afterCreation = NativeHeifPreview.CacheStats();
                if (afterCreation.NativeDecodes != 4 || afterCreation.Entries != 1 ||
                    creationChanged.PixelSize.Width > 128 ||
                    creationChanged.PixelSize.Height > 128)
                    throw new IOException("Changing creation time did not invalidate cached previews.");
            }
            var finalStats = NativeHeifPreview.CacheStats();
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                format = "heif_cache_regression_v2",
                result = "PASS",
                originalFileUnmodified = true,
                originalFile = Path.GetFileName(original),
                staleDimensionsEvicted = 2,
                creationTimeInvalidationChecked = creationTimeChecked,
                nativeDecodes = finalStats.NativeDecodes,
                derivedThumbnails = finalStats.DerivedThumbnails,
                cachedEntriesAfterInvalidation = finalStats.Entries
            }));
            return 0;
        }
        finally
        {
            NativeHeifPreview.ClearCache();
            Directory.Delete(tempFolder, recursive: true);
        }
    }
}

public sealed class LaunchOptions
{
    public string? StateDirectory { get; set; }
    public string? RenderDirectory { get; set; }
    public string? PreviewCheckFile { get; set; }
    public string? PreviewCacheRegressionFile { get; set; }
    public bool SelfTest { get; set; }
    public bool Help { get; set; }
    public List<string> Paths { get; } = [];

    public static LaunchOptions Parse(string[] args)
    {
        var result = new LaunchOptions();
        for (var index = 0; index < args.Length; index++)
        {
            var argument = args[index];
            switch (argument)
            {
                case "--state-dir":
                    result.StateDirectory = RequireValue(args, ref index);
                    break;
                case "--render-check":
                    result.RenderDirectory = RequireValue(args, ref index);
                    break;
                case "--preview-check":
                    result.PreviewCheckFile = RequireValue(args, ref index);
                    break;
                case "--preview-cache-regression":
                    result.PreviewCacheRegressionFile = RequireValue(args, ref index);
                    break;
                case "--self-test": result.SelfTest = true; break;
                case "--help" or "-h": result.Help = true; break;
                default:
                    if (argument.StartsWith("--", StringComparison.Ordinal))
                        throw new ArgumentException($"Unknown option: {argument}");
                    result.Paths.Add(Path.GetFullPath(argument));
                    break;
            }
        }
        return result;
    }

    private static string RequireValue(string[] args, ref int index)
    {
        if (++index >= args.Length || string.IsNullOrWhiteSpace(args[index]))
            throw new ArgumentException("An option is missing its directory argument.");
        return Path.GetFullPath(args[index]);
    }
}
