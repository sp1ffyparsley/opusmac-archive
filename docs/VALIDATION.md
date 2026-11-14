# Publication validation

Checked on Windows on November 13, 2026 with .NET SDK 10.0.401, targeting .NET 8. These results apply to the sanitized public source, not the commercial product or a physical Mac.

| Check | Result |
| --- | --- |
| Locked NuGet restore | Passed |
| Release application build | Passed, zero warnings/errors |
| Core/filesystem suite | 178 passed, zero failed, five unverified |
| Packaging self-test | 10 passed, zero failed |
| Headless Avalonia UI suite | 80 passed, one failed, two skipped |
| Gitleaks 8.30.1 source scan | No findings after public sanitization; inline allow comments were ignored by the scanner |
| Personal-context and source-only review | No private account/path markers or bundled runtime data found in the reviewed publication set |
| Physical macOS/Apple Silicon execution | Not performed |

## Known failing check

`Split File creates numbered pieces that Join Files restores` failed its rendered-frame assertion: **The rendered image is unexpectedly empty.** The failure occurred at screenshot validation in the headless UI harness. It was not removed, marked passed or bypassed for this publication. The precise cause has not been established; do not assume it proves a data-operation defect or that it is harmless. Other checks, including the dedicated core suite, do not erase this failure.

Use the commands in BUILDING.md to reproduce from an isolated clone. Preserve the failure and inspect render readiness, lifecycle and the actual Split/Join interaction. Do not weaken the assertion just to obtain a green result.

## Evidence boundaries

Raw local receipts, logs and screenshots remain private because they can contain host paths. This page summarizes the outcomes without publishing those paths. Historical 81-pass UI claims refer to an earlier package, not this run. No new application binaries are released here, and no developer certificate/notarization or full Mac workflow acceptance is claimed.

Before publication, the fresh Git history is also scanned and the remote source tree is compared against the reviewed local snapshot. The owner's public handle and GitHub no-reply email are used for new commits; original private author history is not imported.

## Discoverability documentation update — November 14, 2026

This later change modifies repository metadata and documentation only: search-oriented title/description/topics, README reuse/FAQ sections, PROJECT_INDEX.json, the agent entrypoint note, and the source manifest. Application source and dependency versions are unchanged; the application tests above were not rerun or upgraded to new acceptance claims. Documentation links, JSON, source-manifest consistency and secret scans were checked before the update. Search ranking is not a guaranteed outcome.
