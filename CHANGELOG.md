# Changelog

All notable changes to this project are documented here. The format loosely follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and the project uses
[Semantic Versioning](https://semver.org/).

## [1.2.1] — 2026-11-16

### Fixed

- **Sync to LX Music now adds missing sources instead of silently skipping them.** If a source
  exists locally but not in LX Music (for example after deleting all custom sources), syncing
  creates a new `userApis` entry with a generated `id` and `name` / `version` / `author` /
  `description` / `homepage` taken from the script header. Previously every such source was
  counted as "skipped" and the summary read like nothing was wrong.

### Changed

- The sync summary now separates **added**, **updated**, **already in sync**, **local file
  missing** and **skipped to avoid a downgrade**.

## [Unreleased]

### Documentation

- Expanded `README.zh-CN.md` into a full Chinese guide: problem statement, per-control UI reference,
  download/hash pipeline, mirror scheduling, the five LX Music states, sync safeguards, `user_api.json`
  format (`gz_` is zlib), `config.ini` reference, CLI, build and FAQ.
- Documented that the codebase is generated and iterated by DeepSeek Harness under maintainer review
  (README, CONTRIBUTING and CHANGELOG).

## [1.2.0] — 2026-11-16

First public release.

### Added

- One-file WinForms application built with the .NET Framework C# 5 compiler (no SDK required).
- SHA-256 based update logic: unchanged sources are skipped, changed ones replace a single file.
- Multi-mirror download with automatic failover and working-mirror caching.
- LX Music integration:
  - reads `%APPDATA%\lx-music-desktop\LxDatas\user_api.json` (gz_ + Base64 + zlib),
  - "洛雪" column showing installed version / pending sync / installed-is-newer / not installed / LX-only,
  - **Sync to LX Music** with timestamped backup, unknown-field preservation and downgrade protection,
  - discovery of sources that exist only in LX Music, exportable to local `.js` files.
- Discovery of new source directories from the upstream repository.
- Silent mode (`/auto`) writing `auto.log`, suitable for Task Scheduler.
- `config.ini` persisting output folder, per-source paths, checkbox state, hashes and timestamps.
- `LX_TOOL_DATA` override for the LX Music data file and `LX_TOOL_NO_MITIGATION` to disable the
  DLL-injection mitigation.
- Startup hardening: `PROCESS_MITIGATION_BINARY_SIGNATURE_POLICY` to block unsigned DLL injection
  (works around startup crashes caused by game overlay injectors), plus `SetErrorMode` to suppress
  the resulting loader dialog.
- GitHub Actions workflow: builds every push, publishes a release zip on `v*` tags.

### Known limitations

- UI is Simplified Chinese only; i18n is on the roadmap.
- Windows-only (WinForms + .NET Framework).
- Default upstream repository is `pdone/lx-music-source`; mirrors need occasional maintenance.

[1.2.0]: https://github.com/maygo88/lx-source-downloader/releases/tag/v1.2.0
