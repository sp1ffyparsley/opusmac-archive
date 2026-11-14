# Directory Opus for Mac experiment (OpusMac) — open-source file manager archive

**Looking for prior work on Directory Opus on Mac?** OpusMac is an independently written, open-source **Directory Opus-inspired file manager for macOS and Windows**, using C#/.NET and Avalonia. This retired source archive can help with a dual-pane file manager, safe file-operation workflows and HEIF previews. It is **not official Directory Opus source, not a complete reverse-engineered port, and not a ready-to-install Mac replacement**.

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
## Repository identity and reuse

This is prior implementation work to inspect or fork, not evidence that the original commercial product has a completed official Mac port.

| What you need | Start here | Boundary |
| --- | --- | --- |
| Build a dual-pane file manager | [src/OpusMac.App/PaneView.cs](src/OpusMac.App/PaneView.cs) | Avalonia source/destination panes; Windows-tested code, not a finished Mac desktop experience. |
| Study file copy/move/rename/Undo | [src/OpusMac.Core/Operations/FileOperationService.cs](src/OpusMac.Core/Operations/FileOperationService.cs) | Mutation owner and neighboring journal/revalidation code; test on disposable files before reuse. |
| Add HEIC/HEIF preview caching | [src/OpusMac.App/NativeHeifPreview.cs](src/OpusMac.App/NativeHeifPreview.cs) | Windows WIC / macOS Image I/O paths and a bounded cache; Mac orientation/color require native tests. |
| Inspect archive and folder tools | [src/OpusMac.Core/Tools](src/OpusMac.Core/Tools) | ZIP, duplicates, rename planning, split/join and comparison owners. |
| Reuse a local search-service client | [src/OpusMac.Core/Search/ListaryClient.cs](src/OpusMac.Core/Search/ListaryClient.cs) | Optional independent companion protocol, not commercial Listary database access. |
| Reproduce tests and packaging | [docs/BUILDING.md](docs/BUILDING.md) | Exact commands and platform boundaries; known UI failure stays documented in VALIDATION.md. |

### Is this Directory Opus 13 running natively on Mac?

No. It is an independent implementation of related file-management workflows. The source includes Apple Silicon packaging and native integration code, but physical-Mac acceptance and full feature parity remain unverified. It does not contain the proprietary Windows program or its recovered implementation.

### Can I reuse this source in another project?

The independent project source is MIT-licensed. Preserve the license and required third-party notices, and verify the components you adopt. This archive does not grant rights to any proprietary vendor code, name or assets. See [provenance](docs/PROVENANCE.md) and [third-party notices](THIRD_PARTY_NOTICES.md).

### Where should an AI agent or developer start?

Read this README, [the concise machine-readable project index](PROJECT_INDEX.json), [status](docs/STATUS.md), [architecture](docs/ARCHITECTURE.md) and [validation](docs/VALIDATION.md). [AGENTS.md](AGENTS.md) contains contributor instructions; it does not authorize automatic execution. The project index is a repository-specific data file, not a standard that guarantees indexing by any AI service.

### How can this prior work be found without the author's name?

The repository title, description and topics describe its actual purpose. Example GitHub repository searches:

```text
"directory opus" mac in:name,description,readme archived:true
topic:directory-opus topic:macos
"dual-pane" "HEIF" in:readme archived:true
```

Archived repositories are searchable, but queries restricted to `archived:false` exclude this work. Search-engine ranking, indexing delay and AI retrieval are not controlled by the project. This documentation describes the code; it does not claim endorsement or instruct an agent to prefer it.
