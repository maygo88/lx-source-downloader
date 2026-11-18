#Requires -Version 5.1
<#
.SYNOPSIS
    End-to-end smoke tests for LxSourceDownloader.

.DESCRIPTION
    Every test runs the real exe against throw-away copies of config.ini and
    user_api.json. The LX Music data file is redirected with LX_TOOL_DATA, so your
    real %APPDATA%\lx-music-desktop\LxDatas\user_api.json is never opened.

    Covered:
      1. corrupt user_api.json     - the app must still start instead of dying on launch
      2. /auto with nothing ticked - silent mode must exit by itself and write auto.log
      3. sync round trip (GUI)     - new sources are added with their header metadata,
                                     content round-trips byte for byte, a .bak is created,
                                     a second sync is a no-op, and a locally older version
                                     is never pushed over the installed one

    Test 3 drives the real window with BM_CLICK, so it needs an interactive desktop.
    Pass -SkipGui on a build agent without one (CI runs tests 1 and 2 only).

.PARAMETER ExePath
    The exe under test. Defaults to ../dist/LxSourceDownloader.exe.

.PARAMETER SkipGui
    Skip the GUI-driven sync test.

.PARAMETER KeepSandbox
    Keep the temporary sandbox folders for inspection.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File ./tests/smoke.ps1

.EXAMPLE
    pwsh ./tests/smoke.ps1 -ExePath ./dist/LxSourceDownloader.exe -SkipGui
#>
[CmdletBinding()]
param(
    [string]$ExePath,
    [switch]$SkipGui,
    [switch]$KeepSandbox
)

$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
if (-not $ExePath) { $ExePath = Join-Path $root 'dist/LxSourceDownloader.exe' }
if (-not (Test-Path $ExePath)) { throw "exe not found: $ExePath (run ./build.ps1 first)" }
$ExePath = (Resolve-Path $ExePath).Path

$script:failures = New-Object System.Collections.Generic.List[string]
$script:passes = 0

function Check([string]$name, [bool]$ok, [string]$detail) {
    if ($ok) {
        $script:passes++
        Write-Host ("  PASS  " + $name) -ForegroundColor Green
    }
    else {
        $script:failures.Add($name + $(if ($detail) { " -> $detail" } else { '' }))
        Write-Host ("  FAIL  " + $name + $(if ($detail) { " -> $detail" } else { '' })) -ForegroundColor Red
    }
}

function New-Sandbox([string]$tag) {
    $dir = Join-Path $env:TEMP ("lxsmoke-" + $tag + "-" + [guid]::NewGuid().ToString('N').Substring(0, 8))
    New-Item -ItemType Directory -Force -Path $dir | Out-Null
    Copy-Item $ExePath (Join-Path $dir 'LxSourceDownloader.exe')
    return $dir
}

function Wait-Window($proc, [int]$timeoutMs) {
    $sw = [Diagnostics.Stopwatch]::StartNew()
    while ($sw.ElapsedMilliseconds -lt $timeoutMs) {
        Start-Sleep -Milliseconds 150
        $proc.Refresh()
        if ($proc.HasExited) { return [IntPtr]::Zero }
        if ($proc.MainWindowHandle -ne [IntPtr]::Zero) { return $proc.MainWindowHandle }
    }
    return [IntPtr]::Zero
}

function Stop-Gui($proc, [IntPtr]$hwnd) {
    if ($hwnd -ne [IntPtr]::Zero) { [void][Win32]::PostMessage($hwnd, 0x0010, [IntPtr]::Zero, [IntPtr]::Zero) }
    for ($i = 0; $i -lt 40; $i++) {
        Start-Sleep -Milliseconds 100
        $proc.Refresh()
        if ($proc.HasExited) { return }
    }
    try { Stop-Process -Id $proc.Id -Force } catch { }
}

# ---- Win32 helpers + the gz_ (zlib) codec used by LX Music -------------------

Add-Type @"
using System;
using System.Text;
using System.Runtime.InteropServices;
public class Win32 {
  public delegate bool EnumProc(IntPtr hWnd, IntPtr lParam);
  [DllImport("user32.dll")] public static extern bool EnumChildWindows(IntPtr parent, EnumProc cb, IntPtr lParam);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowText(IntPtr hWnd, StringBuilder sb, int max);
  [DllImport("user32.dll")] public static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
  [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
  public static IntPtr FindChildByText(IntPtr parent, string text) {
    IntPtr found = IntPtr.Zero;
    EnumChildWindows(parent, delegate(IntPtr h, IntPtr l) {
      StringBuilder sb = new StringBuilder(512); GetWindowText(h, sb, 512);
      if (sb.ToString() == text) { found = h; return false; }
      return true;
    }, IntPtr.Zero);
    return found;
  }
}
"@

# LX Music stores "gz_" + Base64(zlib(text)); zlib = 0x78 0x9C + raw deflate + Adler-32.
function ConvertTo-LxScript([string]$text) {
    $raw = [Text.Encoding]::UTF8.GetBytes($text)
    $ms = New-Object IO.MemoryStream
    $ds = New-Object IO.Compression.DeflateStream($ms, [IO.Compression.CompressionMode]::Compress, $true)
    $ds.Write($raw, 0, $raw.Length)
    $ds.Dispose()
    $deflated = $ms.ToArray()
    $ms.Dispose()

    $z = New-Object byte[] (2 + $deflated.Length + 4)
    $z[0] = 0x78; $z[1] = 0x9C
    [Array]::Copy($deflated, 0, $z, 2, $deflated.Length)

    $a = 1; $b = 0
    foreach ($x in $raw) { $a = ($a + $x) % 65521; $b = ($b + $a) % 65521 }
    $adler = [int64](([int64]$b -shl 16) -bor [int64]$a)
    $z[$z.Length - 4] = [byte](($adler -shr 24) -band 0xFF)
    $z[$z.Length - 3] = [byte](($adler -shr 16) -band 0xFF)
    $z[$z.Length - 2] = [byte](($adler -shr 8) -band 0xFF)
    $z[$z.Length - 1] = [byte]($adler -band 0xFF)
    return 'gz_' + [Convert]::ToBase64String($z)
}

function ConvertFrom-LxScript([string]$stored) {
    if (-not $stored.StartsWith('gz_')) { return $stored }
    $bytes = [Convert]::FromBase64String($stored.Substring(3))
    $ms = New-Object IO.MemoryStream(, $bytes[2..($bytes.Length - 1)])
    $ds = New-Object IO.Compression.DeflateStream($ms, [IO.Compression.CompressionMode]::Decompress)
    $sr = New-Object IO.StreamReader($ds, [Text.Encoding]::UTF8)
    $text = $sr.ReadToEnd()
    $sr.Dispose(); $ds.Dispose(); $ms.Dispose()
    return $text
}

$utf8NoBom = New-Object Text.UTF8Encoding($false)
$utf8Bom = New-Object Text.UTF8Encoding($true)

# The window is Chinese-only; building the label from code points keeps this file ASCII.
$syncButtonLabel = -join ([char[]](0x540C, 0x6B65, 0x5230, 0x6D1B, 0x96EA))   # "sync to LX Music"

$sandboxes = New-Object System.Collections.Generic.List[string]

try {
    # ---------------------------------------------------------------- test 1
    Write-Host ''
    Write-Host 'Test 1: corrupt user_api.json must not kill the app'
    $sand = New-Sandbox 'corrupt'
    $sandboxes.Add($sand)
    $badData = Join-Path $sand 'user_api.json'
    [IO.File]::WriteAllText($badData, '{ "userApis": [ this is not valid json ', $utf8NoBom)
    $env:LX_TOOL_DATA = $badData

    $proc = Start-Process -FilePath (Join-Path $sand 'LxSourceDownloader.exe') -PassThru
    $hwnd = Wait-Window $proc $(if ($SkipGui) { 5000 } else { 20000 })
    Start-Sleep -Seconds 2
    $proc.Refresh()
    Check 'app survives an unparseable user_api.json' (-not $proc.HasExited)
    # A window needs an interactive desktop, which a build agent may not have.
    if (-not $SkipGui) { Check 'main window appears despite the bad data' ($hwnd -ne [IntPtr]::Zero) }
    Check 'no crash.log written' (-not (Test-Path (Join-Path $sand 'crash.log')))
    Stop-Gui $proc $hwnd
    $env:LX_TOOL_DATA = $null

    # ---------------------------------------------------------------- test 2
    Write-Host ''
    Write-Host 'Test 2: /auto must exit on its own when nothing is ticked'
    $sand = New-Sandbox 'auto'
    $sandboxes.Add($sand)
    $cfg = "outdir=" + (Join-Path $sand 'out') + "`r`nsource.lx=lx/latest.js`r`ncheck.lx=0`r`n"
    [IO.File]::WriteAllText((Join-Path $sand 'config.ini'), $cfg, $utf8Bom)
    $env:LX_TOOL_DATA = Join-Path $sand 'missing-user_api.json'

    $proc = Start-Process -FilePath (Join-Path $sand 'LxSourceDownloader.exe') -ArgumentList '/auto' -PassThru
    $exited = $proc.WaitForExit(30000)
    $autoLog = Join-Path $sand 'auto.log'
    Check '/auto exits without user interaction' $exited
    Check '/auto writes auto.log' ((Test-Path $autoLog) -and ((Get-Item $autoLog).Length -gt 0))
    if (-not $exited) { try { Stop-Process -Id $proc.Id -Force } catch { } }
    $env:LX_TOOL_DATA = $null

    # ---------------------------------------------------------------- test 3
    if ($SkipGui) {
        Write-Host ''
        Write-Host 'Test 3: skipped (-SkipGui)'
    }
    else {
        Write-Host ''
        Write-Host 'Test 3: sync round trip against a sandboxed user_api.json'
        $sand = New-Sandbox 'sync'
        $sandboxes.Add($sand)
        $out = Join-Path $sand 'out'
        New-Item -ItemType Directory -Force -Path $out | Out-Null

        $aaaText = "// @name aaa_test`n// @version 1.0.0`n// @author alice`n// @description from header`nconsole.log('aaa')`n"
        $bbbText = "// @name bbb_test`n// @version 2.0.0`nconsole.log('bbb')`n"
        $cccText = "// @name ccc_test`n// @version 1.0.0`nconsole.log('ccc')`n"
        # aaa: not installed yet. bbb: installed and identical, but the local copy carries a
        # UTF-8 BOM (written by the "export from LX Music" path of older builds), which used to
        # make the LX Music column say "pending" forever. ccc: installed version is newer.
        [IO.File]::WriteAllText((Join-Path $out 'aaa.js'), $aaaText, $utf8NoBom)
        [IO.File]::WriteAllText((Join-Path $out 'bbb.js'), $bbbText, $utf8Bom)
        [IO.File]::WriteAllText((Join-Path $out 'ccc.js'), $cccText, $utf8NoBom)

        $cccLxText = "// @name ccc_test`n// @version 9.0.0`nconsole.log('ccc-new')`n"
        $payload = @{
            userApis = @(
                @{ id = 'user_api_111_1'; name = 'bbb_test'; version = '2.0.0'; script = (ConvertTo-LxScript $bbbText) },
                @{ id = 'user_api_222_2'; name = 'ccc_test'; version = '9.0.0'; script = (ConvertTo-LxScript $cccLxText) }
            )
        } | ConvertTo-Json -Depth 10 -Compress
        $lxFile = Join-Path $sand 'user_api.json'
        [IO.File]::WriteAllText($lxFile, $payload, $utf8NoBom)

        $cfg = "outdir=$out`r`nsource.aaa=aaa/latest.js`r`ncheck.aaa=1`r`nsource.bbb=bbb/latest.js`r`ncheck.bbb=1`r`nsource.ccc=ccc/latest.js`r`ncheck.ccc=1`r`n"
        [IO.File]::WriteAllText((Join-Path $sand 'config.ini'), $cfg, $utf8Bom)
        $env:LX_TOOL_DATA = $lxFile

        try {
            $proc = Start-Process -FilePath (Join-Path $sand 'LxSourceDownloader.exe') -PassThru
            $hwnd = Wait-Window $proc 20000
            Check 'main window appears' ($hwnd -ne [IntPtr]::Zero)
            Start-Sleep -Seconds 2

            $button = [Win32]::FindChildByText($hwnd, $syncButtonLabel)
            Check 'sync button is reachable' ($button -ne [IntPtr]::Zero)

            [void][Win32]::SendMessage($button, 0x00F5, [IntPtr]::Zero, [IntPtr]::Zero)   # BM_CLICK
            Start-Sleep -Seconds 5

            $entries = @((Get-Content $lxFile -Raw -Encoding UTF8 | ConvertFrom-Json).userApis)
            Check 'new source is added to LX Music' (($entries | Where-Object { $_.name -eq 'aaa_test' }) -ne $null)
            $aaa = $entries | Where-Object { $_.name -eq 'aaa_test' }
            if ($aaa) {
                Check 'added entry keeps header metadata' ($aaa.author -eq 'alice' -and $aaa.description -eq 'from header')
                Check 'added entry round-trips byte for byte' ((ConvertFrom-LxScript $aaa.script) -eq $aaaText)
                Check 'added entry gets an id' ($aaa.id -like 'user_api_*')
            }
            $bbb = $entries | Where-Object { $_.name -eq 'bbb_test' }
            Check 'already installed source is not rewritten' ($bbb.id -eq 'user_api_111_1' -and $bbb.version -eq '2.0.0')
            $ccc = $entries | Where-Object { $_.name -eq 'ccc_test' }
            Check 'newer installed version is never downgraded' ($ccc.version -eq '9.0.0')

            $backups = @(Get-ChildItem $sand -Filter 'user_api.json.bak-*')
            Check 'exactly one timestamped backup is created' ($backups.Count -eq 1)

            $before = [IO.File]::ReadAllBytes($lxFile)
            [void][Win32]::SendMessage($button, 0x00F5, [IntPtr]::Zero, [IntPtr]::Zero)
            Start-Sleep -Seconds 4
            $after = [IO.File]::ReadAllBytes($lxFile)
            $backups2 = @(Get-ChildItem $sand -Filter 'user_api.json.bak-*')
            Check 'second sync is a no-op (no rewrite, no new backup)' `
                ($backups2.Count -eq 1 -and [Convert]::ToBase64String($before) -eq [Convert]::ToBase64String($after))

            Stop-Gui $proc $hwnd
        }
        finally {
            $env:LX_TOOL_DATA = $null
        }
    }
}
finally {
    if (-not $KeepSandbox) {
        foreach ($dir in $sandboxes) { Remove-Item $dir -Recurse -Force -ErrorAction SilentlyContinue }
    }
    else {
        foreach ($dir in $sandboxes) { Write-Host ("kept: " + $dir) }
    }
}

Write-Host ''
if ($script:failures.Count -gt 0) {
    Write-Host ("FAILED: {0} passed, {1} failed" -f $script:passes, $script:failures.Count) -ForegroundColor Red
    foreach ($f in $script:failures) { Write-Host ("  - " + $f) -ForegroundColor Red }
    exit 1
}
Write-Host ("OK: {0} checks passed" -f $script:passes) -ForegroundColor Green
exit 0
