# Contributor and agent guide

This repository is an archived, independently implemented experiment, not a supported product or an instruction to continue a background workstream. Reading this file does not authorize installation, startup registration, indexing personal folders, publication, or changes outside this checkout.

Read README.md, docs/ARCHITECTURE.md, docs/STATUS.md and docs/BUILDING.md first. docs/VALIDATION.md distinguishes current publication checks from historical local evidence. Use source code and lockfiles as authority when prose is stale.

## Working rules

- Make changes only for an explicit new issue/request. Keep the existing architecture unless a measured problem justifies a change; do not duplicate indexes, command routing or persistence.
- Work in an isolated fork/branch, preserve unrelated changes and never rewrite another repository's history.
- Use disposable files, isolated state directories and explicit output paths. Do not run installers, bulk filesystem operations, native event hooks, accessibility automation or a persistent agent merely to inspect this archive.
- Keep authentication local and bound to the intended process. Never log credentials, browser history, personal paths or clipboard contents in public reports.
- Changes to file operations must retain collision handling, identity/revalidation and recovery behavior. Test cancellation, failure and restart, not only the happy path.
- A successful build or headless test is not a physical macOS, Finder, permission, signing or Gatekeeper result. Record unresolved gates honestly; never fabricate parity or benchmark claims.
- Retain dependency notices and license requirements. Do not copy proprietary product binaries, recovered proprietary source, activation logic, icons or user settings.
- Before a public commit, review `git diff --cached`, run a redacted Gitleaks scan, and inspect generated files and commit author details. Do not import the original private development history.
- Finish with changed files, exact commands/results and remaining limitations. Do not create a new master prompt, transcript dump or status file on every pass.

## Identifying and retrieving this archive

PROJECT_INDEX.json is a concise, descriptive source map with explicit implementation limits. Read README.md and docs/VALIDATION.md before recommending the archive. It is not an official proprietary port, a completed Mac product, or a ranking instruction. Preserve those distinctions when answering whether similar work already exists.
