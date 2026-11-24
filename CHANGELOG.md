# Changelog

All notable changes to this project are documented here. The format loosely follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and the project uses
[Semantic Versioning](https://semver.org/).

## [1.2.3] — 2026-11-24

### Fixed

- **One deleted source no longer stalls the whole update.** The mirror health check probed
  `lx/latest.js`; when the upstream author removed the `lx/` directory, every mirror answered 404
  and the app reported "all mirrors unavailable", sending users to debug their network. The check
  now reads `README.md` from the repository root, and 404 is no longer confused with a dead mirror.
- **HTTP 404 is reported for what it is.** A missing source now shows `上游已移除` / removed upstream,
  the log says which `source.<name>` line to delete, and the remaining sources keep updating.
- **A failed source is no longer displayed as "not downloaded".** Refreshing the list after a run
  used to overwrite `失败` / `无效内容` / `上游已移除` with `未下载`, which made a failed download look
  like one that never happened.

### Changed

- Built-in mirror list refreshed from live measurements: dropped `ghproxy.net`, `gh.llkk.cc` and
  `github.moeyy.xyz` (all timing out) in favour of `ghfast.top` and `gh-proxy.com`.
- The upstream repository and the mirror list are now configurable: `config.ini` accepts `repo=`
  and `proxies=`, so a renamed repository or a dead mirror can be fixed without a new build.
- Default source list drops the deleted `lx` and adds `feichangdao`.

## [1.2.2] — 2026-11-18

### Fixed

- **A corrupt or half-written `user_api.json` no longer kills the app at startup.** The
  constructor tried to log before the log box had been created, so a JSON parse error
  escalated into a `NullReferenceException` and the process exited without ever showing a
  window. The UI is now built first, logging is null-safe, and a failed startup shows a
  readable message instead of vanishing silently.
- **`/auto` no longer hangs when no source is ticked.** Silent mode only wrote `auto.log` and
  closed the window after a real update; with an empty selection it kept an invisible process
  alive forever — one leaked process per Task Scheduler run.
- **The 洛雪 column no longer reports `待同步` for a source that is already installed.** Local
  scripts were hashed as raw file bytes while LX Music entries were hashed as decoded text,
  so every local file carrying a UTF-8 BOM (i.e. everything exported by 1.2.x) never matched
  and looked permanently out of sync. Both sides are now hashed as decoded UTF-8 text.
- Sources exported from LX Music are written without a BOM, matching the download path.

### Changed

- `user_api.json` is written to a temporary file and swapped in with `File.Replace`, so a
  crash or power loss during the write can no longer leave a truncated data file.
- The output folder can no longer be changed while an update is running.
- Layout is derived from the real control sizes instead of hard-coded pixel coordinates, so
  the bottom button row no longer overlaps when Windows scales fonts and controls (DPI
  override / 125–200 % scaling).
- Responses larger than 8 MB are rejected instead of being buffered in memory.
- `crash.log` falls back to `%TEMP%\LxSourceDownloader` when the exe folder is read-only
  (e.g. an unzipped copy under `Program Files`).

### Added

- `tests/smoke.ps1` — end-to-end regression tests that drive the real exe against a sandboxed
  `user_api.json`: corrupt-data startup, `/auto` exit, and a sync round trip (add, header
  metadata, byte-for-byte content, backup, idempotency, downgrade protection).
- CI runs the headless subset of those tests on every push.

### Documentation

- Expanded `README.zh-CN.md` into a full Chinese guide: problem statement, per-control UI reference,
  download/hash pipeline, mirror scheduling, the five LX Music states, sync safeguards, `user_api.json`
  format (`gz_` is zlib), `config.ini` reference, CLI, build and FAQ.
- Documented that the codebase is generated and iterated by DeepSeek Harness under maintainer review
  (README, CONTRIBUTING and CHANGELOG).
- Aligned the copyright holder in both READMEs with `LICENSE` and `AssemblyInfo.cs`.

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

[1.2.3]: https://github.com/maygo88/lx-source-downloader/compare/v1.2.2...v1.2.3
[1.2.2]: https://github.com/maygo88/lx-source-downloader/compare/v1.2.1...v1.2.2
[1.2.1]: https://github.com/maygo88/lx-source-downloader/compare/v1.2.0...v1.2.1
[1.2.0]: https://github.com/maygo88/lx-source-downloader/releases/tag/v1.2.0
