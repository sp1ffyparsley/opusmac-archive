# OpusMac — archived independent file manager

**Source archive, not a production-ready Mac port of Windows Directory Opus.** This .NET/Avalonia experiment implements familiar dual-pane file-management workflows with independently written code. It is unaffiliated with GPSoftware and contains no proprietary Directory Opus executable or recovered source.

Active development was retired on **November 13, 2026**. The source is published for study, reuse and forks under the [MIT license](LICENSE). There is no maintenance commitment and no ready-to-install release of this public snapshot.

## Start here

| Need | Read |
| --- | --- |
| What is implemented and what is missing? | [Status](docs/STATUS.md) |
| Where is each subsystem? | [Architecture](docs/ARCHITECTURE.md) |
| How do I build and test it? | [Build guide](docs/BUILDING.md) |
| What was actually checked? | [Validation](docs/VALIDATION.md) |
| How should an agent work on this fork? | [AGENTS.md](AGENTS.md) |
| What may safely be published? | [Provenance](docs/PROVENANCE.md), [Security](SECURITY.md) |

## Included implementation

- Source/destination panes, tabs, folder trees, saved layouts and keyboard commands.
- Browsing, copy/move/rename/Trash/Undo with dedicated operation and recovery owners.
- ZIP operations, duplicate finding, batch rename, split/join and folder comparison.
- Text/hex/raster/PDF preview; HEIC/HEIF/HIF support through Windows WIC or macOS Image I/O, with bounded preview caching and invalidation.
- A client for the independently implemented [Listary-inspired search archive](https://github.com/sp1ffyparsley/listary-mac-archive). It does not read the commercial Listary database. Ordinary file browsing does not require that companion.
- Source for Windows/macOS packaging, package checks and explicit signing helpers.

## Developer quick start

Install a .NET SDK capable of targeting .NET 8, then run from this checkout:

```sh
dotnet restore src/OpusMac.App/OpusMac.App.csproj --locked-mode
dotnet build src/OpusMac.App/OpusMac.App.csproj -c Release --no-restore
dotnet run --project src/OpusMac.App/OpusMac.App.csproj -c Release -- --state-dir ./artifacts/dev-profile
```

Use an isolated profile and disposable files. The app can modify, move and delete files; this archive is not a safe replacement for a backup. See the build guide for checks and packaging. No original Directory Opus installation is required.

## Boundaries

The source contains a working Windows-tested implementation, not proof of a complete Mac desktop product. Physical Apple Silicon UI, Finder/Trash behavior, permissions, image color/orientation, signing and Gatekeeper still need validation. Office/video integration and exact modern Directory Opus parity are incomplete. Some tests depend on host capabilities and report unverified instead of passing.

Only source and public documentation are distributed here. Private history, personal settings, images, indexes, generated test runs and historical binaries remain excluded. Dependencies retain their own licenses; see [third-party notices](THIRD_PARTY_NOTICES.md).
