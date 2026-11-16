# Legacy scripts

These are the original PowerShell/.cmd scripts this project started from. They are kept for reference only —
the C# application in `src/` supersedes them (same download logic, plus LX Music integration, silent mode,
and no dependency on the execution policy).

`run.cmd` sets `POWERSHELL_UPDATECHECK=Off` before starting PowerShell, which worked around a PowerShell
7.6.x startup crash in Chinese locales (`totalWidth ('-17')` from the update banner). That bug is fixed
upstream in 7.6.8+, where the banner was removed entirely.