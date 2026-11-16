#Requires -Version 5.1
<#
.SYNOPSIS
    Builds dist/LxSourceDownloader.exe with the .NET Framework C# compiler.

.DESCRIPTION
    No SDK, MSBuild or NuGet is required: csc.exe ships with .NET Framework 4.x,
    which is part of every Windows 10/11 installation. The resulting exe is a
    single self-contained file (it only needs the preinstalled .NET Framework).

.PARAMETER OutputDir
    Where to put the exe. Defaults to ./dist.

.EXAMPLE
    pwsh ./build.ps1
#>
[CmdletBinding()]
param(
    [string]$OutputDir
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
if (-not $OutputDir) { $OutputDir = Join-Path $root 'dist' }

$cscCandidates = @(
    (Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'),
    (Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe')
)
$csc = $cscCandidates | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $csc) {
    throw 'csc.exe not found. .NET Framework 4.x is required (it ships with Windows 10/11 and with windows-latest CI images).'
}

$srcDir = Join-Path $root 'src'
$icon = Join-Path $root 'assets/icon.ico'
$sourceFiles = @(Get-ChildItem $srcDir -Filter *.cs | Sort-Object Name | ForEach-Object { $_.FullName })
if ($sourceFiles.Count -eq 0) { throw "No .cs files found in $srcDir" }

# csc reads sources as ANSI unless told otherwise; /codepage:65001 makes the
# Chinese literals in this project safe regardless of a BOM.

New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null
$out = Join-Path $OutputDir 'LxSourceDownloader.exe'

$cscArgs = @(
    '/nologo'
    '/target:winexe'
    '/optimize+'
    '/platform:anycpu'
    '/codepage:65001'
    "/win32icon:$icon"
    "/out:$out"
    '/r:System.dll'
    '/r:System.Core.dll'
    '/r:System.Drawing.dll'
    '/r:System.Windows.Forms.dll'
    '/r:System.Web.Extensions.dll'
) + $sourceFiles

Write-Host "Compiler: $csc"
& $csc $cscArgs
if ($LASTEXITCODE -ne 0) { throw "csc failed with exit code $LASTEXITCODE" }

$info = (Get-Item $out).VersionInfo
Write-Host ("Built {0} ({1:N0} bytes, v{2})" -f $out, (Get-Item $out).Length, $info.FileVersion)
