using System.Text;
using System.Text.Json;
using OpusMac.Core;
using OpusMac.Core.Browsing;
using OpusMac.Core.Persistence;

namespace OpusMac.Checks.Suites;

public sealed class BrowsingChecks : ICheckSuite
{
    public async Task RunAsync(CheckRunner checks, string sandbox)
    {
        await checks.CheckAsync("Browsing: real files, Unicode names, natural ordering and hidden visibility", async () =>
        {
            var root = Fresh(sandbox, "listing");
            Directory.CreateDirectory(Path.Combine(root, "Z folder"));
            await File.WriteAllTextAsync(Path.Combine(root, "scene10.txt"), "ten");
            await File.WriteAllTextAsync(Path.Combine(root, "scene2.txt"), "two");
            await File.WriteAllTextAsync(Path.Combine(root, "한글 café.txt"), "unicode");
            await File.WriteAllTextAsync(Path.Combine(root, ".private"), "hidden");
            var listing = await new DirectoryBrowser().ReadAsync(root, new BrowseOptions());
            CheckRunner.Equal(0, listing.Errors.Count);
            CheckRunner.Equal(4, listing.Entries.Count);
            CheckRunner.Equal("Z folder", listing.Entries[0].Name);
            CheckRunner.True(listing.Entries.ToList().FindIndex(x => x.Name == "scene2.txt") < listing.Entries.ToList().FindIndex(x => x.Name == "scene10.txt"), "Numeric names must sort numerically.");
            var visible = await new DirectoryBrowser().ReadAsync(root, new BrowseOptions(ShowHidden: true));
            CheckRunner.Equal(5, visible.Entries.Count);
            CheckRunner.True(visible.Entries.Single(e => e.Name == "한글 café.txt").Length == Encoding.UTF8.GetByteCount("unicode"), "File metadata must come from the real file.");
        });

        await checks.CheckAsync("Browsing: ShowHidden includes Windows Hidden and System entries, including recursive folders", async () =>
        {
            var root = Fresh(sandbox, "hidden-attributes");
            var visiblePath = Path.Combine(root, "visible.txt");
            var hiddenPath = Path.Combine(root, "hidden.txt");
            var systemPath = Path.Combine(root, "system.txt");
            var hiddenFolder = Path.Combine(root, "hidden-folder");
            await File.WriteAllTextAsync(visiblePath, "visible");
            await File.WriteAllTextAsync(hiddenPath, "hidden");
            await File.WriteAllTextAsync(systemPath, "system");
            Directory.CreateDirectory(hiddenFolder);
            await File.WriteAllTextAsync(Path.Combine(hiddenFolder, "nested.txt"), "nested");

            if (OperatingSystem.IsWindows())
            {
                File.SetAttributes(hiddenPath, File.GetAttributes(hiddenPath) | FileAttributes.Hidden);
                File.SetAttributes(systemPath, File.GetAttributes(systemPath) | FileAttributes.System);
                File.SetAttributes(hiddenFolder, File.GetAttributes(hiddenFolder) | FileAttributes.Hidden);
            }

            var ordinary = await new DirectoryBrowser().ReadAsync(root, new BrowseOptions(Recursive: true));
            CheckRunner.True(ordinary.Entries.Any(entry => entry.FullPath == visiblePath), "The visible file disappeared from the ordinary listing.");
            CheckRunner.True(ordinary.Entries.All(entry => entry.FullPath != hiddenPath && entry.FullPath != systemPath && entry.FullPath != hiddenFolder && entry.Name != "nested.txt"),
                "Hidden/system entries or children of a hidden folder leaked into the ordinary listing.");

            var inclusive = await new DirectoryBrowser().ReadAsync(root, new BrowseOptions(ShowHidden: true, Recursive: true));
            CheckRunner.True(inclusive.Entries.Any(entry => entry.FullPath == hiddenPath), "ShowHidden omitted a Hidden file.");
            CheckRunner.True(inclusive.Entries.Any(entry => entry.FullPath == systemPath), "ShowHidden omitted a System file.");
            CheckRunner.True(inclusive.Entries.Any(entry => entry.FullPath == hiddenFolder), "ShowHidden omitted a Hidden directory.");
            CheckRunner.True(inclusive.Entries.Any(entry => entry.FullPath == Path.Combine(hiddenFolder, "nested.txt")),
                "Recursive ShowHidden did not descend into the Hidden directory.");
        });

        await checks.CheckAsync("Browsing: quoted, relative and file-URI paths preserve Unicode folder names", async () =>
        {
            var root = Fresh(sandbox, "path resolution café 東京");
            var folder = Path.Combine(root, " spaced folder");
            Directory.CreateDirectory(folder);
            await File.WriteAllTextAsync(Path.Combine(folder, "path-proof.txt"), "path resolution");

            var absolute = Path.GetFullPath(folder);
            CheckRunner.Equal(absolute, DirectoryBrowser.ResolvePath($"\"{folder}\""),
                "A quoted absolute path must resolve to the same native folder.");
            CheckRunner.Equal(absolute, DirectoryBrowser.ResolvePath(new Uri(folder).AbsoluteUri),
                "A file URI must decode spaces and Unicode without changing the target folder.");
            CheckRunner.Equal(Path.GetFullPath(Path.Combine(root, " spaced folder")),
                DirectoryBrowser.ResolvePath("\" spaced folder\"", root),
                "Quoted relative paths must preserve whitespace inside the quotes.");

            var listing = await new DirectoryBrowser().ReadAsync($"\"{folder}\"", new BrowseOptions());
            CheckRunner.Equal(0, listing.Errors.Count);
            CheckRunner.Equal("path-proof.txt", listing.Entries.Single().Name,
                "Browsing through a quoted path must open the same real directory.");

            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            CheckRunner.Equal(Path.GetFullPath(home), DirectoryBrowser.ResolvePath("~"),
                "The home shortcut must resolve to the current profile.");
            CheckRunner.Equal(Path.GetFullPath(Path.Combine(home, "Documents")), DirectoryBrowser.ResolvePath("~/Documents"),
                "A home-relative path must resolve from the current profile.");
        });

        await checks.CheckAsync("Browsing: recursive filtering and explicit bounded partial state", async () =>
        {
            var root = Fresh(sandbox, "recursive");
            Directory.CreateDirectory(Path.Combine(root, "nested"));
            await File.WriteAllTextAsync(Path.Combine(root, "keep.txt"), "a");
            await File.WriteAllTextAsync(Path.Combine(root, "nested", "keep2.txt"), "b");
            await File.WriteAllTextAsync(Path.Combine(root, "nested", "skip.bin"), "c");
            var listing = await new DirectoryBrowser().ReadAsync(root, new BrowseOptions(Recursive: true, Filter: "*.txt"));
            CheckRunner.Equal(2, listing.Entries.Count);
            CheckRunner.True(!listing.Truncated, "Complete listing reported truncated.");
            var bounded = await new DirectoryBrowser().ReadAsync(root, new BrowseOptions(Recursive: true, MaxEntries: 1));
            CheckRunner.True(bounded.Truncated, "A capped scan must expose its incomplete state.");
            CheckRunner.True(bounded.Entries.Count <= 1, "Listing cap was exceeded.");
        });

        await checks.CheckAsync("Browsing: missing directory is an error, cancellation aborts", async () =>
        {
            var listing = await new DirectoryBrowser().ReadAsync(Path.Combine(sandbox, "not-present"), new BrowseOptions());
            CheckRunner.True(listing.Errors.Count > 0, "A missing folder must not masquerade as an empty directory.");
            CheckRunner.True(!string.IsNullOrWhiteSpace(listing.RootError), "A root-folder read failure must be distinguishable from partial child-entry errors.");
            using var cancel = new CancellationTokenSource();
            cancel.Cancel();
            await CheckRunner.ThrowsAsync<OperationCanceledException>(() => new DirectoryBrowser().ReadAsync(sandbox, new BrowseOptions(), cancellationToken: cancel.Token));
        });

        await checks.CheckAsync("Browsing: Unicode-normalized wildcard exclusions and folders-first descending sort", () =>
        {
            CheckRunner.True(DirectoryBrowser.MatchesFilter("cafe\u0301.txt", "café"), "Composed/decomposed text should match.");
            CheckRunner.True(DirectoryBrowser.MatchesFilter("scene.png", "*.jpg;*.png;!draft*"), "Wildcard union should match.");
            CheckRunner.True(!DirectoryBrowser.MatchesFilter("draft.png", "*.png;!draft*"), "Exclusions must win.");
            var now = DateTimeOffset.UtcNow;
            FileEntry[] entries = [
                new("/a/file1", "file1", false, false, 1, now, now, FileAttributes.Normal),
                new("/a/folder", "folder", true, false, 0, now, now, FileAttributes.Directory),
                new("/a/file20", "file20", false, false, 20, now, now, FileAttributes.Archive)
            ];
            var sorted = DirectoryBrowser.SortEntries(entries, "Size", descending: true);
            CheckRunner.Equal("folder", sorted[0].Name);
            CheckRunner.Equal("file20", sorted[1].Name);
            var byAttributes = DirectoryBrowser.SortEntries(entries, "Attr", foldersFirst: false);
            CheckRunner.Equal("folder", byAttributes[0].Name);
            CheckRunner.Equal("file20", byAttributes[1].Name);
            CheckRunner.True(NaturalNameComparer.Instance.Compare("a9999999999999999999999", "a10000000000000000000000") < 0, "Natural sorting must not overflow long numeric runs.");
            return Task.CompletedTask;
        });

        await checks.CheckAsync("Settings: complete window/tab/favorite state survives independent reload", async () =>
        {
            var root = Fresh(sandbox, "settings");
            var store = new SettingsStore(root);
            var state = store.Load();
            state.DualPane = false;
            state.ShowViewer = false;
            state.SplitOrientation = "Horizontal";
            state.Theme = "Light";
            state.Panes[0].Tabs.Add(new TabSettings { Path = sandbox, ViewMode = "Thumbnails", SortColumn = "Size", Descending = true, Locked = true });
            state.Panes[0].ActiveTab = 1;
            state.Favorites.Add(new FavoriteFolder("Test favorites", sandbox));
            await store.SaveAsync(state);
            var loaded = new SettingsStore(root).Load();
            CheckRunner.Equal(2, loaded.SchemaVersion, "Current settings must persist as schema v2.");
            CheckRunner.True(!loaded.DualPane && !loaded.ShowViewer, "Layout switches were lost.");
            CheckRunner.Equal("Horizontal", loaded.SplitOrientation);
            CheckRunner.Equal(sandbox, loaded.Panes[0].Tabs[1].Path);
            CheckRunner.True(loaded.Panes[0].Tabs[1].Locked && loaded.Panes[0].Tabs[1].Descending, "Tab flags were lost.");
            CheckRunner.Equal(1, loaded.Panes[0].ActiveTab);
            CheckRunner.Equal(sandbox, loaded.Favorites.Single().Path);
        });

        await checks.CheckAsync("Settings: named Listers roundtrip independently from global preferences", async () =>
        {
            var root = Fresh(sandbox, "saved-listers");
            var left = Fresh(root, "left");
            var right = Fresh(root, "right");
            var store = new SettingsStore(root);
            var state = store.Load();
            state.Theme = "Dark";
            state.Favorites.Add(new FavoriteFolder("Global favorite", left));
            state.SavedListers.Add(new SavedLister
            {
                Name = "Editing",
                DualPane = true,
                SplitOrientation = "Horizontal",
                ShowTree = false,
                ShowViewer = true,
                WindowWidth = 1680,
                WindowHeight = 920,
                ActivePane = 1,
                Panes =
                [
                    new PaneSettings { Tabs = [new TabSettings { Path = left, ViewMode = "Tiles", SortColumn = "Size", Descending = true, Locked = true,
                        Columns =
                        [
                            new() { Id = "Name", Visible = true, Width = 1, WidthMode = "Star" },
                            new() { Id = "Path", Visible = true, Width = 240 },
                            new() { Id = "Size", Visible = true, Width = 85 },
                            new() { Id = "Modified", Visible = true, Width = 148 },
                            new() { Id = "Type", Visible = false, Width = 100 },
                            new() { Id = "Created", Visible = false, Width = 148 }
                        ] }] },
                    new PaneSettings { Tabs = [new TabSettings { Path = right, ViewMode = "Details", SortColumn = "Modified",
                        Columns =
                        [
                            new() { Id = "Name", Visible = true, Width = 1, WidthMode = "Star" },
                            new() { Id = "Created", Visible = true, Width = 180 },
                            new() { Id = "Modified", Visible = true, Width = 148 },
                            new() { Id = "Type", Visible = true, Width = 100 },
                            new() { Id = "Size", Visible = false, Width = 85 },
                            new() { Id = "Path", Visible = false, Width = 260 }
                        ] }] }
                ]
            });
            await store.SaveAsync(state);

            var loaded = new SettingsStore(root).Load();
            CheckRunner.Equal("Dark", loaded.Theme, "Saving a Lister must not replace the global theme preference.");
            CheckRunner.Equal(left, loaded.Favorites.Single().Path);
            var lister = loaded.SavedListers.Single();
            CheckRunner.Equal("Editing", lister.Name);
            CheckRunner.True(lister.DualPane && !lister.ShowTree && lister.ShowViewer, "Saved Lister layout switches were lost.");
            CheckRunner.Equal("Horizontal", lister.SplitOrientation);
            CheckRunner.Equal(1, lister.ActivePane);
            CheckRunner.Equal(1680d, lister.WindowWidth);
            CheckRunner.Equal(920d, lister.WindowHeight);
            CheckRunner.Equal(left, lister.Panes[0].Tabs.Single().Path);
            CheckRunner.Equal("Tiles", lister.Panes[0].Tabs.Single().ViewMode);
            CheckRunner.True(lister.Panes[0].Tabs.Single().Locked && lister.Panes[0].Tabs.Single().Descending, "Saved tab flags were lost.");
            CheckRunner.Equal(right, lister.Panes[1].Tabs.Single().Path);
            CheckRunner.Equal("Path", lister.Panes[0].Tabs.Single().Columns[1].Id, "Saved Lister source column order was lost.");
            CheckRunner.True(lister.Panes[0].Tabs.Single().Columns[1].Visible && lister.Panes[0].Tabs.Single().Columns[1].Width == 240,
                "Saved Lister source column visibility/width was lost.");
            CheckRunner.Equal("Created", lister.Panes[1].Tabs.Single().Columns[1].Id, "Saved Lister destination format was lost.");
            CheckRunner.True(!lister.Panes[1].Tabs.Single().Columns.Single(column => column.Id == "Size").Visible,
                "Saved Lister destination hidden-column state was lost.");
        });

        await checks.CheckAsync("Settings: corrupt primary recovers backup and preserves original bytes", async () =>
        {
            var root = Fresh(sandbox, "recovery");
            var store = new SettingsStore(root);
            await store.SaveAsync(new AppSettings { Theme = "Light" });
            await store.SaveAsync(new AppSettings { Theme = "Dark" });
            const string corrupt = "{broken";
            await File.WriteAllTextAsync(store.SettingsPath, corrupt);
            var recovered = new SettingsStore(root);
            var loaded = recovered.Load();
            CheckRunner.Equal("Light", loaded.Theme);
            CheckRunner.True(recovered.LastLoadWarning is not null, "Recovery must be visible.");
            await recovered.SaveAsync(loaded);
            var preserved = Directory.GetFiles(root, "settings.json.unreadable-*");
            CheckRunner.Equal(1, preserved.Length);
            CheckRunner.Equal(corrupt, await File.ReadAllTextAsync(preserved[0]));
        });

        await checks.CheckAsync("Settings: tile views sort aliases and theme choices survive real roundtrips", async () =>
        {
            var root = Fresh(sandbox, "settings-display");
            var store = new SettingsStore(root);
            var initial = store.Load();
            CheckRunner.Equal("Light", initial.Theme, "A fresh profile must use the reference light appearance.");
            initial.Theme = "Dark";
            initial.Panes[0].Tabs =
            [
                new TabSettings { Path = sandbox, ViewMode = "Tiles", SortColumn = "Length", Descending = true, Locked = true },
                new TabSettings { Path = sandbox, ViewMode = "Details", SortColumn = "Kind", Descending = true },
                new TabSettings { Path = sandbox, ViewMode = "Icons", SortColumn = "Created" },
                new TabSettings { Path = sandbox, ViewMode = "Thumbnails", SortColumn = "Modified" }
            ];
            await store.SaveAsync(initial);
            var loaded = new SettingsStore(root).Load();
            CheckRunner.Equal("Dark", loaded.Theme, "A deliberate dark theme selection must survive restart.");
            CheckRunner.Equal("Tiles", loaded.Panes[0].Tabs[0].ViewMode, "The live tile view must remain selected after restart.");
            CheckRunner.Equal("Size", loaded.Panes[0].Tabs[0].SortColumn, "UI Length sorting must persist as canonical Size.");
            CheckRunner.Equal("Type", loaded.Panes[0].Tabs[1].SortColumn, "UI Kind sorting must persist as canonical Type.");
            CheckRunner.True(loaded.Panes[0].Tabs[0].Descending && loaded.Panes[0].Tabs[0].Locked && loaded.Panes[0].Tabs[1].Descending, "Sort direction and locked tab state were lost.");
            CheckRunner.Equal("Tiles", loaded.Panes[0].Tabs[2].ViewMode, "Legacy Icons must map to the implemented Tiles view.");
            CheckRunner.Equal("Tiles", loaded.Panes[0].Tabs[3].ViewMode, "Legacy Thumbnails must map to the implemented Tiles view.");
            CheckRunner.Equal("Created", loaded.Panes[0].Tabs[2].SortColumn);
            CheckRunner.Equal("Modified", loaded.Panes[0].Tabs[3].SortColumn);
            CheckRunner.Equal(8, loaded.Panes[0].Tabs[0].Columns.Count, "Every tab must carry a complete bounded column layout.");
            CheckRunner.True(loaded.Panes[0].Tabs[0].Columns.Single(column => column.Id == "Name").Visible,
                "The filename column must remain visible.");
            loaded.Theme = "System";
            loaded.Panes[0].Tabs[0].ViewMode = "Unknown view";
            loaded.Panes[0].Tabs[0].SortColumn = "Unknown column";
            await new SettingsStore(root).SaveAsync(loaded);
            var fallback = new SettingsStore(root).Load();
            CheckRunner.Equal("Light", fallback.Theme, "Unsupported theme values must fall back to Light.");
            CheckRunner.Equal("Details", fallback.Panes[0].Tabs[0].ViewMode);
            CheckRunner.Equal("Name", fallback.Panes[0].Tabs[0].SortColumn);
            CheckRunner.Equal("Tiles", fallback.Panes[0].Tabs[2].ViewMode, "Normalizing one invalid tab must preserve other usable tabs.");
        });

        await checks.CheckAsync("Settings: schema-v1 folder formats migrate and malformed columns normalize safely", async () =>
        {
            var root = Fresh(sandbox, "column-layout-migration");
            var path = Path.Combine(root, "settings.json");
            await File.WriteAllTextAsync(path, "{\"SchemaVersion\":1,\"Panes\":[{\"Tabs\":[{\"Path\":" + JsonEscape(sandbox) + "}]}]}");
            var migrated = new SettingsStore(root).Load();
            CheckRunner.Equal(2, migrated.SchemaVersion, "Schema v1 must migrate in memory to v2.");
            CheckRunner.Equal(8, migrated.Panes[0].Tabs[0].Columns.Count, "Migrated tabs must receive the canonical column layout.");
            CheckRunner.Equal("Name", migrated.Panes[0].Tabs[0].Columns[0].Id);
            CheckRunner.True(migrated.Panes[0].Tabs[0].Columns[0].Visible, "Migrated Name must remain visible.");

            var legacyRoot = Fresh(sandbox, "legacy-reference-columns");
            var legacyPath = Path.Combine(legacyRoot, "settings.json");
            var legacySettings = new
            {
                SchemaVersion = 2,
                Panes = new[]
                {
                    new
                    {
                        Tabs = new[]
                        {
                            new
                            {
                                Path = sandbox,
                                Columns = new object[]
                                {
                                    new { Id = "Name", Visible = true, Width = 1d, WidthMode = "Star" },
                                    new { Id = "Size", Visible = true, Width = 85d, WidthMode = "Pixel" },
                                    new { Id = "Modified", Visible = true, Width = 148d, WidthMode = "Pixel" },
                                    new { Id = "Type", Visible = true, Width = 100d, WidthMode = "Pixel" },
                                    new { Id = "Created", Visible = false, Width = 148d, WidthMode = "Pixel" },
                                    new { Id = "Path", Visible = false, Width = 260d, WidthMode = "Pixel" }
                                }
                            }
                        }
                    }
                }
            };
            await File.WriteAllTextAsync(legacyPath, JsonSerializer.Serialize(legacySettings));
            var upgradedLegacy = new SettingsStore(legacyRoot).Load().Panes[0].Tabs[0].Columns;
            CheckRunner.True(upgradedLegacy.Where(column => column.Visible).Select(column => column.Id)
                    .SequenceEqual(new[] { "Name", "Size", "Type", "Modified", "Attr" }),
                "The untouched pre-Attr default did not upgrade to the measured reference column order.");

            migrated.Panes[0].Tabs[0].Columns =
            [
                new() { Id = "path", Visible = true, Width = 500, WidthMode = "pixel" },
                new() { Id = "NAME", Visible = false, Width = 1, WidthMode = "pixel" },
                new() { Id = "Path", Visible = false, Width = 10 },
                new() { Id = "unknown", Visible = true, Width = 90 },
                new() { Id = "size", Visible = false, Width = 5000 },
                new() { Id = "created", Visible = true, Width = 0.01, WidthMode = "star" }
            ];
            await new SettingsStore(root).SaveAsync(migrated);
            var normalized = new SettingsStore(root).Load().Panes[0].Tabs[0].Columns;
            CheckRunner.Equal(8, normalized.Count, "Column layouts must remain bounded to the eight known IDs.");
            CheckRunner.Equal(8, normalized.Select(column => column.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count(), "Duplicate column IDs survived normalization.");
            CheckRunner.True(normalized.All(column => column.Id is "Name" or "Size" or "Modified" or "Created" or "Type" or "Attr" or "Path" or "Thumbnail"), "An unknown column ID survived normalization.");
            CheckRunner.True(!normalized.Single(column => column.Id == "Thumbnail").Visible, "Existing layouts must keep the thumbnail column hidden.");
            CheckRunner.Equal("Path", normalized[0].Id, "Valid user column order was not preserved.");
            var name = normalized.Single(column => column.Id == "Name");
            CheckRunner.True(name.Visible && name.Width >= 180, "Name must be visible with a safe fixed minimum width.");
            var size = normalized.Single(column => column.Id == "Size");
            CheckRunner.True(!size.Visible && size.Width == 1200, "Oversized widths must clamp without forcing hidden columns visible.");
            var created = normalized.Single(column => column.Id == "Created");
            CheckRunner.True(created.Visible && created.WidthMode == "Star" && created.Width == 0.25, "Star widths must normalize to the bounded fill range.");
            CheckRunner.True(!normalized.Single(column => column.Id == "Attr").Visible,
                "Adding Attr must not force a new visible column into an existing custom layout.");
        });

        await checks.CheckAsync("Settings: future schema is never silently overwritten", async () =>
        {
            var root = Fresh(sandbox, "future");
            var path = Path.Combine(root, "settings.json");
            const string future = "{\"SchemaVersion\":99,\"FutureData\":\"retain\"}";
            await File.WriteAllTextAsync(path, future);
            var store = new SettingsStore(root);
            _ = store.Load();
            CheckRunner.True(!store.CanSave, "A future schema must prevent writes.");
            await CheckRunner.ThrowsAsync<InvalidOperationException>(() => store.SaveAsync(new AppSettings()));
            CheckRunner.Equal(future, await File.ReadAllTextAsync(path));
        });

        await checks.CheckAsync("Settings: malformed null/oversized selections normalize without losing usable state", async () =>
        {
            var root = Fresh(sandbox, "normalize");
            await File.WriteAllTextAsync(Path.Combine(root, "settings.json"), "{\"SchemaVersion\":1,\"Panes\":[null,{\"Tabs\":null,\"ActiveTab\":999}],\"Favorites\":[null],\"WindowWidth\":2,\"WindowHeight\":2}");
            var state = new SettingsStore(root).Load();
            CheckRunner.Equal(2, state.SchemaVersion, "Legacy schema must migrate before use.");
            CheckRunner.Equal(2, state.Panes.Count);
            CheckRunner.Equal(0, state.Panes[0].ActiveTab);
            CheckRunner.Equal(1, state.Panes[0].Tabs.Count);
            CheckRunner.Equal(800d, state.WindowWidth);
            CheckRunner.Equal(500d, state.WindowHeight);
            CheckRunner.Equal(0, state.Favorites.Count);
        });

        await checks.CheckAsync("Settings: saved Listers remain schema-v1 compatible and normalize bounded unique names", async () =>
        {
            var root = Fresh(sandbox, "saved-lister-normalize");
            var path = Path.Combine(root, "settings.json");
            await File.WriteAllTextAsync(path, "{\"SchemaVersion\":1,\"Theme\":\"Dark\",\"Panes\":[]}");
            var old = new SettingsStore(root).Load();
            CheckRunner.Equal(2, old.SchemaVersion, "Schema-v1 settings did not migrate to v2 in memory.");
            CheckRunner.Equal(0, old.SavedListers.Count, "Older schema-v1 settings should load with an empty saved-Lister library.");
            CheckRunner.Equal(8, old.Panes[0].Tabs[0].Columns.Count, "Schema-v1 tabs did not receive default columns.");

            var store = new SettingsStore(root);
            var state = store.Load();
            state.SavedListers =
            [
                new SavedLister { Name = "  Work  ", SplitOrientation = "Horizontal", WindowWidth = 200, WindowHeight = 20,
                    ActivePane = 99, Panes = [new PaneSettings { Tabs = [new TabSettings { Path = sandbox, ViewMode = "Icons", SortColumn = "Kind" }] }] },
                new SavedLister { Name = "work", Panes = [] },
                new SavedLister { Name = "   ", Panes = [] },
                new SavedLister { Name = new string('x', 130), Panes = [] }
            ];
            for (var index = 0; index < 110; index++)
                state.SavedListers.Add(new SavedLister { Name = "Lister " + index, Panes = [] });
            await store.SaveAsync(state);

            var loaded = new SettingsStore(root).Load();
            CheckRunner.Equal(100, loaded.SavedListers.Count, "Saved Lister count must remain bounded.");
            CheckRunner.Equal(1, loaded.SavedListers.Count(lister => string.Equals(lister.Name, "Work", StringComparison.OrdinalIgnoreCase)),
                "Saved Lister names must be unique case-insensitively after trimming.");
            var work = loaded.SavedListers.Single(lister => lister.Name == "Work");
            CheckRunner.Equal(800d, work.WindowWidth);
            CheckRunner.Equal(500d, work.WindowHeight);
            CheckRunner.Equal(1, work.ActivePane);
            CheckRunner.Equal(2, work.Panes.Count);
            CheckRunner.Equal("Tiles", work.Panes[0].Tabs.Single().ViewMode);
            CheckRunner.Equal("Type", work.Panes[0].Tabs.Single().SortColumn);
            CheckRunner.True(loaded.SavedListers.All(lister => lister.Name.Length is > 0 and <= 100), "Saved Lister names escaped normalization.");
        });
    }

    private static string Fresh(string sandbox, string name)
    {
        var path = Path.Combine(sandbox, name + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static string JsonEscape(string value) => System.Text.Json.JsonSerializer.Serialize(value);
}
