<#
.SYNOPSIS
    Creates the GitHub repository (if needed) and pushes main + tags.

.DESCRIPTION
    Handles the flaky-network reality of pushing to GitHub from some regions:
    retries, optional proxy, and a fallback to an explicit remote URL when the
    gh CLI is not authenticated.

    Prerequisites: git. gh is only required when -RemoteUrl is not given.

.PARAMETER RepoName
    Repository name to create with gh (default: lx-source-downloader).

.PARAMETER RemoteUrl
    Existing repository URL, e.g. https://github.com/<user>/lx-source-downloader.git
    When supplied, gh is not used.

.PARAMETER Proxy
    Optional HTTP(S) proxy for git, e.g. http://127.0.0.1:7890

.PARAMETER Retries
    Attempts per push operation (default 3).

.EXAMPLE
    pwsh ./publish.ps1

.EXAMPLE
    pwsh ./publish.ps1 -RemoteUrl https://github.com/me/lx-source-downloader.git -Proxy http://127.0.0.1:7890
#>
[CmdletBinding()]
param(
    [string]$RepoName = 'lx-source-downloader',
    [string]$RemoteUrl,
    [string]$Proxy,
    [int]$Retries = 3
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
Push-Location $root
try {
    if (-not (Test-Path (Join-Path $root '.git'))) { throw "Not a git repository: $root" }

    $branch = (git rev-parse --abbrev-ref HEAD).Trim()
    if ($branch -ne 'main') { throw "Current branch is '$branch', expected 'main'." }

    $gitArgs = @()
    if ($Proxy) { $gitArgs += @('-c', "http.proxy=$Proxy") }

    function Invoke-Push([string[]]$arguments) {
        for ($i = 1; $i -le $Retries; $i++) {
            Write-Host "push attempt $i/$Retries : git $($arguments -join ' ')"
            & git @gitArgs @arguments
            if ($LASTEXITCODE -eq 0) { return $true }
            if ($i -lt $Retries) { Start-Sleep -Seconds (3 * $i) }
        }
        return $false
    }

    if (-not $RemoteUrl) {
        $gh = Get-Command gh -ErrorAction SilentlyContinue
        if (-not $gh) { throw 'gh CLI not found. Install it or pass -RemoteUrl.' }
        & gh auth status *> $null
        if ($LASTEXITCODE -ne 0) { throw 'gh is not authenticated. Run "gh auth login" or pass -RemoteUrl with a repo you created.' }

        $existing = (git remote 2>$null) -contains 'origin'
        if (-not $existing) {
            Write-Host "Creating GitHub repository '$RepoName'..."
            & gh repo create $RepoName --public --source . --remote origin --push `
                --description 'Single-file Windows GUI that downloads/updates LX Music custom sources and syncs them into your local LX Music installation.'
            if ($LASTEXITCODE -ne 0) { throw 'gh repo create failed.' }
        }
        else {
            $RemoteUrl = (git remote get-url origin).Trim()
        }
    }

    if ($RemoteUrl) {
        $hasOrigin = (git remote 2>$null) -contains 'origin'
        if ($hasOrigin) { & git remote set-url origin $RemoteUrl } else { & git remote add origin $RemoteUrl }
    }

    if (-not (Invoke-Push @('push', '-u', 'origin', 'main'))) { throw 'Failed to push main after retries.' }
    if (-not (Invoke-Push @('push', '--tags', 'origin'))) { throw 'Failed to push tags after retries.' }

    Write-Host ''
    Write-Host 'Done. Remaining manual steps in the GitHub web UI:'
    Write-Host '  1. Settings -> Topics: lx-music, winforms, csharp, windows, music'
    Write-Host '  2. Actions tab: make sure the build workflow is enabled'
    Write-Host '  3. Releases: the v* tag should have produced a release zip automatically'
}
finally {
    Pop-Location
}
