# Contributing

Thanks for taking the time to improve this project. Everything here — code, docs, translations, bug reports —
is welcome.

## Ways to help

- **Report bugs** with the built-in log and `crash.log` (see below).
- **Translate the UI** — the app is Simplified Chinese only today; extracting strings is a great first PR.
- **Maintain the mirror list** — GitHub mirrors die often; PRs that add working ones are valuable.
- **Improve robustness** — retries, timeouts, partial downloads, proxy handling.
- **Improve the build/CI** — smaller binaries, better release automation.

## Development setup

Requirements: Windows 10/11 with .NET Framework 4.x (preinstalled). No Visual Studio, SDK, or NuGet needed.

```powershell
git clone https://github.com/maygo88/lx-source-downloader.git
cd lx-source-downloader
pwsh ./build.ps1        # produces dist/LxSourceDownloader.exe
./dist/LxSourceDownloader.exe
```

## Project constraints

These keep the "single portable exe" promise — please respect them:

1. **C# 5 only** (`csc.exe` 4.0.30319). No string interpolation, no `?.`, no expression-bodied members,
   no tuple syntax, no `nameof`.
2. **.NET Framework 4.8 BCL only.** No NuGet packages, no external binaries, no P/Invoke beyond what is
   already there.
3. **One exe, no installer.** Anything that would need a second file at runtime is a no-go.
4. **Never write to the user's LX Music data without a backup and a downgrade check.**
5. **Keep the app usable offline-ish.** It must start and show the UI even if the network (or LX Music) is absent.

## Code map

`src/LxSourceDownloader.cs` is divided into sections:

| Section | What lives there |
| --- | --- |
| `Program` | entry point, TLS defaults, crash logging, `/auto` detection, DLL-injection mitigation |
| `SourceItem` / `ItemUpdate` / `WorkResult` | small data carriers |
| `MainForm.BuildUi` / `LayoutPanels` | manual layout (no designer file) |
| list/refresh code | `LoadList`, `RefreshLocalState`, `UpdateLxCell` |
| `StartUpdate` / `RunWorker` / `DoUpdate` | BackgroundWorker pipeline, download loop |
| `DoDiscover` | upstream repo listing + LX-only discovery |
| `EnsureProxy` / `FetchWithFallback` / `Fetch` | mirror probing and HTTP |
| `LoadLx` / `SyncToLx` / `EncodeScript` / `DecodeScript` | LX Music `user_api.json` read/write (gz_ = zlib) |
| `LoadConfig` / `SaveConfig` | `config.ini` |

## Style

- 4 spaces, Allman braces, one type per file only where it already is that way.
- UI strings stay Simplified Chinese; put user-facing text in `string.Format`/literals so it is easy to
  extract later.
- Comments may be English or Chinese — English is preferred for new code.
- Keep `AssemblyInfo.cs` as the single source of truth for the version.

## Testing checklist

Before opening a PR, please verify:

- [ ] `pwsh ./build.ps1` finishes with exit code 0 on a clean clone.
- [ ] The app starts, lists sources, and `检查并更新` reports no crash.
- [ ] Running `LxSourceDownloader.exe /auto` writes `auto.log` with a sensible summary.
- [ ] With LX Music installed, the 洛雪 column shows versions and `同步到洛雪` creates a
  `user_api.json.bak-*` file. **Test against a copy of `user_api.json`, not your real one.**
- [ ] No new `crash.log` is generated.

## Reporting bugs

Please include:

1. Windows version and the app version (title bar).
2. What you did, what you expected, what happened.
3. The text from the log pane (or `auto.log`).
4. `crash.log` if it exists.
5. Whether LX Music was running, and whether you use game overlay/booster software.

Trim personal paths before pasting logs if you care about them.

## Pull requests

- One logical change per PR; describe the *why*, not just the *what*.
- Update `CHANGELOG.md` under an `Unreleased` heading for user-visible changes.
- If you touch LX Music I/O, say in the PR how you tested the round-trip (a copy of the real file is fine).
- By contributing you agree your changes are licensed under the MIT license.
