# Architecture and source map

The application is a .NET 8 workspace, not a wrapper around the commercial Directory Opus executable.

| Owner | Responsibility |
| --- | --- |
| `src/OpusMac.App/Program.cs` | CLI parsing, application lifecycle, isolated headless checks and HEIF diagnostics |
| `MainWindow*.cs`, `PaneView.cs` | Commands, source/destination displays, selection and layout UI |
| `PreviewPane.cs`, `NativeHeifPreview.cs` | Single-selection previews; native HEIF decoding and bounded process-local cache |
| `src/OpusMac.Core/Browsing` | Directory enumeration and browsing state |
| `Operations` | Mutations, journals, path identity/revalidation, recovery and Undo |
| `Persistence` | Settings, folder columns and saved layouts |
| `Platform` | Native file operations and shell/desktop boundaries |
| `Search` | Authenticated companion-client protocol; no second search database |
| `Tools` | Archives, rename plans, duplicates, file parts and comparisons |
| `Integration` | Single-instance dispatch and Windows clipboard intent |
| `tests/OpusMac.Checks` | Disposable filesystem/core checks |
| `AppDiagnostics.cs` | Headless Avalonia UI/interaction checks; not a real Mac session |
| `tools/packaging` | Self-contained bundles, manifests, archive modes and signing/verification scripts |

UI commands must use the operation owner instead of direct filesystem mutation. Search integration is optional; a missing companion must not become fake results or a UI stall. The published companion archive is also retired and is not installed automatically.

HEIF cache entries contain compressed previews, not reusable disposable UI objects. The cache is bounded to 48 MiB / 24 entries, keyed by path, size, timestamps and requested dimension. A larger cached preview may generate a tile without another native decode. This is a performance cache, never an integrity authority for file operations. Identical metadata after deliberate content replacement can still require explicit invalidation.

The public package identifier is `io.github.sp1ffyparsley.opusmac`. This publication changes the prior owner-specific identifier consistently in builder and verifier; it is not an upgrade path for any private historical installation.
