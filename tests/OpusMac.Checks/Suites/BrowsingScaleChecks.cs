using System.Diagnostics;
using System.Text;
using System.Text.Json;
using OpusMac.Core;
using OpusMac.Core.Browsing;

namespace OpusMac.Checks.Suites;

public sealed class BrowsingScaleChecks : ICheckSuite
{
    private const int FileCount = 24000;
    private const int FolderCount = 32;
    private const int Repetitions = 3;

    public async Task RunAsync(CheckRunner checks, string sandbox)
    {
        var fixture = Path.Combine(sandbox, "listing-" + Guid.NewGuid().ToString("N"));
        var measurements = new List<object>();
        DirectoryListing? listing = null;
        FileEntry[] fileRows = [];

        await checks.CheckAsync("Browsing scale: real 24,032-entry directory retains metadata and natural folder order", async () =>
        {
            var watch = Stopwatch.StartNew();
            Directory.CreateDirectory(fixture);
            for (var i = 0; i < FileCount; i++)
            {
                using var file = new FileStream(Path.Combine(fixture, NameForIndex(i)), FileMode.CreateNew, FileAccess.Write, FileShare.None);
            }
            for (var i = 0; i < FolderCount; i++) Directory.CreateDirectory(Path.Combine(fixture, "folder" + i));
            measurements.Add(new { operation = "fixture_create", entries = FileCount + FolderCount, elapsed_ms = watch.Elapsed.TotalMilliseconds });

            watch.Restart();
            listing = await new DirectoryBrowser().ReadAsync(fixture, new BrowseOptions(ShowHidden: true));
            measurements.Add(new { operation = "real_listing", entries = listing.Entries.Count, elapsed_ms = watch.Elapsed.TotalMilliseconds });
            CheckRunner.Equal(FileCount + FolderCount, listing.Entries.Count);
            CheckRunner.Equal(0, listing.Errors.Count, "A scale listing must not hide inaccessible entries.");
            CheckRunner.True(!listing.Truncated, "The 24,032-entry fixture is below the listing limit.");
            CheckFolderOrder(listing.Entries, descending: false);
            fileRows = listing.Entries.Where(entry => !entry.IsDirectory).ToArray();
            CheckRunner.True(fileRows.All(entry => entry.Length == 0 && !entry.IsSymbolicLink && File.Exists(entry.FullPath)), "Rows must describe the real disposable files.");
            AssertNames(fileRows, _ => true);
            CheckNumericPair(listing.Entries, descending: false);
        });

        await checks.CheckAsync("Browsing scale: Unicode, wildcard unions, exclusions and empty terms over 24,000 real rows", () =>
        {
            RequireListing();
            var queries = new (string Filter, Func<int, bool> Expected)[]
            {
                (" cafe\u0301 ; 한글 ; !*draft* ", i => i % 6 is 0 or 2 or 5),
                ("*.png;*.JPG;!café_draft*;!Snow*", i => i % 6 is 0 or 2),
                ("!*.txt;!*draft*", i => i % 6 is 0 or 2 or 3 or 5),
                (" ;!; ", _ => true),
                ("Café_scene?.png", i => i is 0 or 6),
                ("!*", _ => false),
                ("*.txt", i => i % 6 == 4)
            };
            foreach (var query in queries)
            {
                var selected = Measure("filter", query.Filter, () => DirectoryBrowser.FilterEntries(fileRows, query.Filter), measurements);
                AssertNames(selected, query.Expected);
                foreach (var entry in selected.Take(3))
                    CheckRunner.True(DirectoryBrowser.MatchesFilter(entry.Name, query.Filter), "The public single-name filter must agree with the bulk result.");
            }
            CheckRunner.True(DirectoryBrowser.MatchesFilter("report.txt", null), "Null filters must match.");
            CheckRunner.True(DirectoryBrowser.MatchesFilter("report.txt", " \t "), "Whitespace filters must match.");
            CheckRunner.True(DirectoryBrowser.MatchesFilter("keep.txt", "! foo"), "The space after ! belongs to the excluded literal.");
            CheckRunner.True(!DirectoryBrowser.MatchesFilter("a foo.txt", "! foo"), "Literal exclusions must keep their internal spaces.");
            return Task.CompletedTask;
        });

        await checks.CheckAsync("Browsing scale: actual enumeration uses the same composed and decomposed exclusion query", async () =>
        {
            RequireListing();
            const string query = "cafe\u0301;한글;!*draft*";
            var watch = Stopwatch.StartNew();
            var filtered = await new DirectoryBrowser().ReadAsync(fixture, new BrowseOptions(ShowHidden: true, Filter: query));
            measurements.Add(new { operation = "real_filtered_listing", query, entries = filtered.Entries.Count, elapsed_ms = watch.Elapsed.TotalMilliseconds });
            CheckRunner.Equal(0, filtered.Errors.Count);
            CheckRunner.True(!filtered.Truncated, "A filtered scan below the inspection cap must finish.");
            AssertNames(filtered.Entries, i => i % 6 is 0 or 2 or 5);
        });

        await checks.CheckAsync("Browsing scale: paired legacy and native-metadata scans return identical rows", async () =>
        {
            RequireListing();
            const string query = "cafe\u0301;한글;!*draft*";
            var browser = new DirectoryBrowser();
            var legacyWarm = ReadLegacyFilteredListing(fixture, query);
            var optimizedWarm = await browser.ReadAsync(fixture, new BrowseOptions(ShowHidden: true, Filter: query));
            CheckRunner.Equal(0, optimizedWarm.Errors.Count);
            CheckRunner.True(!optimizedWarm.Truncated, "The optimized comparison listing must be complete.");
            CheckRunner.True(legacyWarm.SequenceEqual(optimizedWarm.Entries), "Legacy and optimized metadata must agree for every returned row.");

            for (var iteration = 0; iteration < Repetitions; iteration++)
            {
                IReadOnlyList<FileEntry> legacy;
                DirectoryListing optimized;
                if (iteration % 2 == 0)
                {
                    legacy = MeasureLegacy(iteration);
                    optimized = await MeasureOptimized(iteration);
                }
                else
                {
                    optimized = await MeasureOptimized(iteration);
                    legacy = MeasureLegacy(iteration);
                }

                CheckRunner.True(legacy.SequenceEqual(optimized.Entries), "Paired legacy and optimized scans must return identical ordered metadata.");

                IReadOnlyList<FileEntry> MeasureLegacy(int sample)
                {
                    var watch = Stopwatch.StartNew();
                    var rows = ReadLegacyFilteredListing(fixture, query);
                    measurements.Add(new { operation = "paired_legacy_metadata_scan", query, iteration = sample + 1, entries = rows.Count, elapsed_ms = watch.Elapsed.TotalMilliseconds });
                    return rows;
                }

                async Task<DirectoryListing> MeasureOptimized(int sample)
                {
                    var watch = Stopwatch.StartNew();
                    var result = await browser.ReadAsync(fixture, new BrowseOptions(ShowHidden: true, Filter: query));
                    measurements.Add(new { operation = "paired_native_metadata_scan", query, iteration = sample + 1, entries = result.Entries.Count, elapsed_ms = watch.Elapsed.TotalMilliseconds });
                    CheckRunner.Equal(0, result.Errors.Count);
                    CheckRunner.True(!result.Truncated, "The optimized paired scan must be complete.");
                    return result;
                }
            }
        });

        await checks.CheckAsync("Browsing scale: paired unfiltered legacy and native-metadata scans return identical rows", async () =>
        {
            RequireListing();
            var browser = new DirectoryBrowser();
            var legacyWarm = ReadLegacyFilteredListing(fixture, null);
            var optimizedWarm = await browser.ReadAsync(fixture, new BrowseOptions(ShowHidden: true));
            CheckRunner.Equal(0, optimizedWarm.Errors.Count);
            CheckRunner.True(!optimizedWarm.Truncated, "The optimized unfiltered comparison listing must be complete.");
            CheckRunner.True(legacyWarm.SequenceEqual(optimizedWarm.Entries), "Legacy and optimized unfiltered metadata must agree for every returned row.");

            for (var iteration = 0; iteration < Repetitions; iteration++)
            {
                IReadOnlyList<FileEntry> legacy;
                DirectoryListing optimized;
                if (iteration % 2 == 0)
                {
                    legacy = MeasureLegacy(iteration);
                    optimized = await MeasureOptimized(iteration);
                }
                else
                {
                    optimized = await MeasureOptimized(iteration);
                    legacy = MeasureLegacy(iteration);
                }
                CheckRunner.True(legacy.SequenceEqual(optimized.Entries), "Paired unfiltered scans must return identical ordered metadata.");

                IReadOnlyList<FileEntry> MeasureLegacy(int sample)
                {
                    var watch = Stopwatch.StartNew();
                    var rows = ReadLegacyFilteredListing(fixture, null);
                    measurements.Add(new { operation = "paired_legacy_unfiltered_scan", iteration = sample + 1, entries = rows.Count, elapsed_ms = watch.Elapsed.TotalMilliseconds });
                    return rows;
                }

                async Task<DirectoryListing> MeasureOptimized(int sample)
                {
                    var watch = Stopwatch.StartNew();
                    var result = await browser.ReadAsync(fixture, new BrowseOptions(ShowHidden: true));
                    measurements.Add(new { operation = "paired_native_unfiltered_scan", iteration = sample + 1, entries = result.Entries.Count, elapsed_ms = watch.Elapsed.TotalMilliseconds });
                    CheckRunner.Equal(0, result.Errors.Count);
                    CheckRunner.True(!result.Truncated, "The optimized unfiltered paired scan must be complete.");
                    return result;
                }
            }
        });

        await checks.CheckAsync("Browsing scale: optimized enumeration preserves real file size and UTC timestamps", async () =>
        {
            var root = Path.Combine(sandbox, "metadata-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var path = Path.Combine(root, "metadata-proof.bin");
            await File.WriteAllBytesAsync(path, Enumerable.Range(0, 37).Select(value => (byte)value).ToArray());
            var expectedModified = new DateTime(2024, 3, 14, 15, 9, 26, DateTimeKind.Utc);
            var expectedCreated = new DateTime(2023, 2, 13, 14, 8, 25, DateTimeKind.Utc);
            File.SetLastWriteTimeUtc(path, expectedModified);
            File.SetCreationTimeUtc(path, expectedCreated);

            var result = await new DirectoryBrowser().ReadAsync(root, new BrowseOptions(ShowHidden: true));
            CheckRunner.Equal(0, result.Errors.Count);
            CheckRunner.Equal(1, result.Entries.Count);
            var entry = result.Entries[0];
            CheckRunner.Equal(37L, entry.Length, "Enumeration lost the native file length.");
            CheckRunner.Equal(new DateTimeOffset(expectedModified), entry.ModifiedUtc, "Enumeration changed the UTC modification timestamp.");
            CheckRunner.Equal(new DateTimeOffset(expectedCreated), entry.CreatedUtc, "Enumeration changed the UTC creation timestamp.");
            CheckRunner.True((entry.Attributes & FileAttributes.Archive) != 0, "Enumeration lost the native archive attribute.");
        });

        await checks.CheckAsync("Browsing scale: directory junctions are listed as links and never recursively traversed", async () =>
        {
            if (!OperatingSystem.IsWindows()) CheckRunner.NotVerified("Directory junction fixture is Windows-specific.");
            var root = Path.Combine(sandbox, "junction-root-" + Guid.NewGuid().ToString("N"));
            var target = Path.Combine(sandbox, "junction-target-" + Guid.NewGuid().ToString("N"));
            var link = Path.Combine(root, "linked-folder");
            var backLink = Path.Combine(target, "back-to-root");
            Directory.CreateDirectory(root);
            Directory.CreateDirectory(target);
            await File.WriteAllBytesAsync(Path.Combine(root, "root-proof.bin"), Enumerable.Range(0, 11).Select(value => (byte)value).ToArray());
            await File.WriteAllBytesAsync(Path.Combine(target, "must-not-be-enumerated.bin"), Enumerable.Range(0, 17).Select(value => (byte)value).ToArray());
            try
            {
                await CreateJunctionAsync(link, target);
                await CreateJunctionAsync(backLink, root);

                var listing = await new DirectoryBrowser().ReadAsync(root, new BrowseOptions(ShowHidden: true, Recursive: true));
                CheckRunner.Equal(0, listing.Errors.Count, "A directory junction must not create a traversal error.");
                CheckRunner.Equal(2, listing.Entries.Count, "Recursive browsing must report the junction itself and the root file, not target contents.");
                var entry = listing.Entries.Single(item => item.Name == "linked-folder");
                CheckRunner.True(entry.IsDirectory && entry.IsSymbolicLink, "A junction must be represented as a directory link.");
                CheckRunner.True((entry.Attributes & FileAttributes.ReparsePoint) != 0, "The junction reparse-point attribute was lost.");
                CheckRunner.True(!listing.Entries.Any(item => item.Name == "must-not-be-enumerated.bin"), "Recursive browsing followed a directory junction.");

                using var sizeCancellation = new CancellationTokenSource();
                CheckRunner.Equal(11L, DirectoryBrowser.CalculateFolderSize(root, sizeCancellation.Token),
                    "Default folder sizing must skip directory junction contents.");
                CheckRunner.Equal(28L, DirectoryBrowser.CalculateFolderSize(root, sizeCancellation.Token, followNestedLinks: true),
                    "Opt-in folder sizing must follow the target once and stop when the junction points back to an already visited directory.");
            }
            finally
            {
                if (Directory.Exists(backLink)) Directory.Delete(backLink, recursive: false);
                if (Directory.Exists(link)) Directory.Delete(link, recursive: false);
                if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
                if (Directory.Exists(target)) Directory.Delete(target, recursive: true);
            }

            static async Task CreateJunctionAsync(string path, string targetPath)
            {
                var start = new ProcessStartInfo("cmd.exe") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
                start.ArgumentList.Add("/d");
                start.ArgumentList.Add("/c");
                start.ArgumentList.Add("mklink");
                start.ArgumentList.Add("/J");
                start.ArgumentList.Add(path);
                start.ArgumentList.Add(targetPath);
                using var process = Process.Start(start) ?? throw new IOException("Could not start the Windows junction fixture command.");
                var stdout = await process.StandardOutput.ReadToEndAsync();
                var stderr = await process.StandardError.ReadToEndAsync();
                await process.WaitForExitAsync();
                if (process.ExitCode != 0) CheckRunner.NotVerified($"Disposable junction creation is unavailable: {stderr} {stdout}".Trim());
            }
        });

        await checks.CheckAsync("Browsing scale: mixed-case sort columns preserve direction, numeric names and folder priority", () =>
        {
            var rows = RequireListing().Entries.Reverse().ToArray();
            var ascending = Measure("sort", "NaMe ascending", () => DirectoryBrowser.SortEntries(rows, "NaMe"), measurements);
            CheckFolderOrder(ascending, descending: false);
            CheckNumericPair(ascending, descending: false);
            var descending = Measure("sort", "NaMe descending", () => DirectoryBrowser.SortEntries(rows, "NaMe", descending: true), measurements);
            CheckFolderOrder(descending, descending: true);
            CheckNumericPair(descending, descending: true);
            var chronological = Measure("sort", "Date Modified ascending", () => DirectoryBrowser.SortEntries(rows, "Date Modified"), measurements);
            CheckRunner.True(chronological.Take(FolderCount).All(entry => entry.IsBrowsable), "Folders must lead date-sorted files.");
            CheckRunner.True(chronological.Skip(FolderCount).Zip(chronological.Skip(FolderCount + 1), (a, b) => a.ModifiedUtc <= b.ModifiedUtc).All(value => value), "Modified sort must be chronological.");
            var withoutPriority = DirectoryBrowser.SortEntries(rows, foldersFirst: false);
            CheckRunner.True(!withoutPriority[0].IsBrowsable, "Disabling folder priority must use the selected natural name order.");
            foreach (var sorted in new[] { ascending, descending, chronological, withoutPriority })
                CheckRunner.Equal(FileCount + FolderCount, sorted.Count, "Sorting must retain every row.");
            return Task.CompletedTask;
        });

        await checks.CheckAsync("Browsing scale: cancellation from a delivered batch aborts pending real enumeration", async () =>
        {
            RequireListing();
            using var cancel = new CancellationTokenSource();
            var callbacks = 0;
            var progress = new InlineProgress<DirectoryBatch>(batch =>
            {
                CheckRunner.True(batch.Entries.Count > 0, "A progress batch must contain entries.");
                callbacks++;
                cancel.Cancel();
            });
            var watch = Stopwatch.StartNew();
            await CheckRunner.ThrowsAsync<OperationCanceledException>(() => new DirectoryBrowser().ReadAsync(fixture, new BrowseOptions(ShowHidden: true), progress, cancel.Token));
            measurements.Add(new { operation = "cancel_after_first_batch", callbacks, elapsed_ms = watch.Elapsed.TotalMilliseconds });
            CheckRunner.Equal(1, callbacks, "Canceled enumeration must not report another batch.");
        });

        await checks.CheckAsync("Browsing scale: cancellation from the final batch prevents a stale completed listing", async () =>
        {
            var root = Path.Combine(sandbox, "final-batch-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            await File.WriteAllTextAsync(Path.Combine(root, "last.txt"), "final batch");
            using var cancel = new CancellationTokenSource();
            var callbacks = 0;
            var progress = new InlineProgress<DirectoryBatch>(_ => { callbacks++; cancel.Cancel(); });
            await CheckRunner.ThrowsAsync<OperationCanceledException>(() => new DirectoryBrowser().ReadAsync(root, new BrowseOptions(), progress, cancel.Token));
            CheckRunner.Equal(1, callbacks, "The final batch must trigger cancellation before the task returns its listing.");
        });

        var receipt = new
        {
            platform = System.Runtime.InteropServices.RuntimeInformation.OSDescription,
            framework = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
            macos_runtime_executed = OperatingSystem.IsMacOS(),
            fixture, files = FileCount, folders = FolderCount,
            measured_repetitions = Repetitions,
            timing_is_informational = true,
            allocation_scope = "Synchronous filter/sort call on the measuring thread; excludes fixture, validation and receipt writing.",
            measurements,
            utc = DateTimeOffset.UtcNow
        };
        var receiptPath = Path.Combine(sandbox, "browsing-scale.json");
        await File.WriteAllTextAsync(receiptPath, JsonSerializer.Serialize(receipt, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine("Browsing scale measurements: " + receiptPath);

        DirectoryListing RequireListing()
        {
            if (listing is null || fileRows.Length != FileCount) CheckRunner.NotVerified("The real scale fixture did not initialize successfully.");
            return listing;
        }
    }

    private static IReadOnlyList<FileEntry> Measure(string operation, string query,
        Func<IReadOnlyList<FileEntry>> action, List<object> measurements)
    {
        _ = action(); // Warm the path before collecting samples; there is no timing pass threshold.
        IReadOnlyList<FileEntry> result = [];
        for (var iteration = 0; iteration < Repetitions; iteration++)
        {
            var allocated = GC.GetAllocatedBytesForCurrentThread();
            var started = Stopwatch.GetTimestamp();
            result = action();
            var elapsed = Stopwatch.GetElapsedTime(started);
            allocated = GC.GetAllocatedBytesForCurrentThread() - allocated;
            measurements.Add(new { operation, query, iteration = iteration + 1, entries = result.Count, elapsed_ms = elapsed.TotalMilliseconds, allocated_bytes = allocated });
        }
        return result;
    }

    private static IReadOnlyList<FileEntry> ReadLegacyFilteredListing(string root, string? query)
    {
        var entries = Directory.EnumerateFileSystemEntries(root)
            .Select(DirectoryBrowser.ReadEntry)
            .ToArray();
        return DirectoryBrowser.SortEntries(DirectoryBrowser.FilterEntries(entries, query ?? string.Empty));
    }

    private static string NameForIndex(int index) => (index % 6) switch
    {
        0 => $"café_scene{index}.png",
        1 => $"cafe\u0301_draft{index}.png",
        2 => $"한글_scene{index}.jpg",
        3 => $"Snow_scene{index}.png",
        4 => $"notes{index}.txt",
        _ => $"cafe\u0301_scene{index}.jpeg"
    };

    private static void AssertNames(IReadOnlyList<FileEntry> entries, Func<int, bool> expected)
    {
        var names = Enumerable.Range(0, FileCount).Where(expected).Select(index => NameForIndex(index).Normalize(NormalizationForm.FormC)).ToHashSet(StringComparer.Ordinal);
        var actual = entries.Select(entry => entry.Name.Normalize(NormalizationForm.FormC)).ToHashSet(StringComparer.Ordinal);
        CheckRunner.Equal(names.Count, entries.Count, "The filter returned an unexpected count or duplicate.");
        CheckRunner.True(names.SetEquals(actual), "Filter membership must match the known independent fixture categories.");
    }

    private static void CheckFolderOrder(IReadOnlyList<FileEntry> entries, bool descending)
    {
        for (var i = 0; i < FolderCount; i++)
        {
            var index = descending ? FolderCount - 1 - i : i;
            CheckRunner.Equal("folder" + index, entries[i].Name, "Folders must remain first in natural numeric order.");
            CheckRunner.True(entries[i].IsBrowsable, "A real folder must remain browsable.");
        }
        CheckRunner.True(entries.Skip(FolderCount).All(entry => !entry.IsBrowsable), "A folder escaped its leading partition.");
    }

    private static void CheckNumericPair(IReadOnlyList<FileEntry> entries, bool descending)
    {
        var names = entries.Select(entry => entry.Name.Normalize(NormalizationForm.FormC)).ToList();
        var six = names.IndexOf("café_scene6.png");
        var twelve = names.IndexOf("café_scene12.png");
        CheckRunner.True(six >= 0 && twelve >= 0 && (descending ? six > twelve : six < twelve), "Numeric names must order scene6 before scene12, reversing only within the file partition.");
    }

    private sealed class InlineProgress<T>(Action<T> action) : IProgress<T>
    {
        public void Report(T value) => action(value);
    }
}
