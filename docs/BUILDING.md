# Building and verifying OpusMac

## Prerequisites

- A .NET SDK capable of targeting .NET 8; project language version is C# 12. The publication host had .NET SDK 10.0.401.
- NuGet restore access (or a complete trusted package cache). Versions are pinned in project/lock files; keep locked restore enabled.
- macOS 13 or newer is the package's declared minimum, not proof of acceptance on every supported OS.
- Windows HEIF previews need an installed compatible WIC codec. macOS uses Image I/O; no codec is installed by this repository.
- Native PDF/image/desktop dependencies are supplied by the pinned packages. External video playback may require FFplay; that is not an integrated player.

## Build and isolated checks

Run from the repository root; use new empty disposable output folders.

```sh
dotnet restore src/OpusMac.App/OpusMac.App.csproj --locked-mode
dotnet build src/OpusMac.App/OpusMac.App.csproj -c Release --no-restore
dotnet run --project tests/OpusMac.Checks/OpusMac.Checks.csproj -c Release -- ./artifacts/core-checks
dotnet run --project src/OpusMac.App/OpusMac.App.csproj -c Release -- --self-test --state-dir ./artifacts/ui-state --render-check ./artifacts/ui-checks
dotnet run --project tools/packaging/OpusMac.Packaging.csproj -c Release -- self-test ./artifacts/packaging-checks
```

The core runner emits a JSON receipt with passed, failed and unverified counts. An exit code of zero does not turn unverified checks into passes. Headless UI checks do not qualify native focus, Finder, permissions or physical Mac execution.

For your own non-sensitive HEIF sample:

```sh
dotnet run --project src/OpusMac.App/OpusMac.App.csproj -c Release -- --preview-check /path/to/sample.heif
dotnet run --project src/OpusMac.App/OpusMac.App.csproj -c Release -- --preview-cache-regression /path/to/sample.heif
```

The mutation regression uses a temporary copy, not the original image. Cache benchmarks are workload/host dependent; do not infer commercial-product parity from them.

## Package a deliberate candidate

```sh
dotnet run --project tools/packaging/OpusMac.Packaging.csproj -c Release -- publish . all ./artifacts/releases
```

Supported selectors are `all`, `win-x64` and `osx-arm64`. Keep output under ignored build/artifact directories. The tool creates self-contained packages and validates available host checks. Inspect its receipt and the exact package before distributing it. It does not publish to GitHub.

On an actual Mac follow [the package acceptance runbook](../tools/packaging/MAC-ACCEPTANCE.md). Signing scripts require deliberate invocation. Never bypass TCC, Gatekeeper, quarantine or SIP; do not present ad-hoc signing as notarization. No signing key or certificate is supplied.
