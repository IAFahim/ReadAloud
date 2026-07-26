# ReadAloud for Windows - one-liner setup:
#
#   irm https://raw.githubusercontent.com/IAFahim/ReadAloud/master/windows/setup.ps1 | iex
#
# Installs the .NET 10 SDK (winget) if missing, clones/updates the repo into $HOME\ReadAloud,
# publishes windows\ReadAloud.Windows.csproj to a single exe, drops a Start Menu + Startup
# shortcut. Every step says what it did. Safe to re-run.
#
# *** HONESTY: this script was written on Linux and has NEVER been executed on Windows. ***
# It is deliberately Windows PowerShell 5.1 compatible (no ternaries, no -AsHashtable), because
# `irm ... | iex` usually lands in 5.1. Read it before you trust it.

$ErrorActionPreference = 'Stop'

$RepoUrl = 'https://github.com/IAFahim/ReadAloud'
$RepoDir = Join-Path $HOME 'ReadAloud'
$Project = Join-Path $RepoDir 'windows\ReadAloud.Windows.csproj'
$OutDir = Join-Path $RepoDir 'windows\publish'
$Exe = Join-Path $OutDir 'ReadAloud.exe'

function Have($name) {
    return [bool](Get-Command $name -ErrorAction SilentlyContinue)
}

function Update-Path {
    $machine = [Environment]::GetEnvironmentVariable('Path', 'Machine')
    $user = [Environment]::GetEnvironmentVariable('Path', 'User')
    $env:Path = "$machine;$user"
}

Write-Host ''
Write-Host 'ReadAloud (Windows) setup' -ForegroundColor Cyan
Write-Host '-------------------------'

# ---- 1. .NET 10 SDK ----------------------------------------------------------------
$hasSdk = $false
if (Have 'dotnet') {
    $hasSdk = [bool]((dotnet --list-sdks) -match '^10\.')
}

if ($hasSdk) {
    Write-Host '[ok]   .NET 10 SDK already installed.'
}
elseif (Have 'winget') {
    Write-Host '[..]   Installing the .NET 10 SDK with winget...'
    winget install --id Microsoft.DotNet.SDK.10 --exact --source winget `
        --accept-package-agreements --accept-source-agreements
    Update-Path
    Write-Host '[ok]   .NET 10 SDK installed.'
}
else {
    Write-Host '[!!]   No .NET 10 SDK and no winget on this machine.' -ForegroundColor Yellow
    Write-Host '       Install it by hand, then re-run this script:'
    Write-Host '       https://dotnet.microsoft.com/download/dotnet/10.0'
    return
}

# ---- 2. the repo -------------------------------------------------------------------
if (-not (Have 'git')) {
    if (Have 'winget') {
        Write-Host '[..]   Installing git with winget...'
        winget install --id Git.Git --exact --source winget `
            --accept-package-agreements --accept-source-agreements
        Update-Path
    }
    else {
        Write-Host '[!!]   git is missing. Install it, then re-run:' -ForegroundColor Yellow
        Write-Host '       https://git-scm.com/download/win'
        return
    }
}

if (Test-Path (Join-Path $RepoDir '.git')) {
    Write-Host "[..]   Updating $RepoDir ..."
    git -C $RepoDir pull --ff-only
    Write-Host '[ok]   Repo updated.'
}
else {
    Write-Host "[..]   Cloning into $RepoDir ..."
    git clone $RepoUrl $RepoDir
    Write-Host '[ok]   Repo cloned.'
}

# ---- 3. build ----------------------------------------------------------------------
Write-Host '[..]   Publishing ReadAloud.exe ...'
dotnet publish $Project -c Release -o $OutDir
if (-not (Test-Path $Exe)) {
    throw "publish finished but $Exe is missing"
}
Write-Host "[ok]   Built $Exe"

# ---- 4. shortcuts ------------------------------------------------------------------
function Set-Shortcut($linkPath, $target) {
    $dir = Split-Path $linkPath
    if (-not (Test-Path $dir)) {
        New-Item -ItemType Directory -Path $dir -Force | Out-Null
    }
    $shell = New-Object -ComObject WScript.Shell
    $link = $shell.CreateShortcut($linkPath)
    $link.TargetPath = $target
    $link.WorkingDirectory = Split-Path $target
    $link.Description = 'ReadAloud - speak the selected text (Ctrl+Win+S)'
    $link.Save()
}

$startMenu = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\ReadAloud.lnk'
$startup = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\Startup\ReadAloud.lnk'
Set-Shortcut $startMenu $Exe
Set-Shortcut $startup $Exe
Write-Host '[ok]   Start Menu shortcut + Startup shortcut (runs at every login).'

# (No Claude Code hook: auto-speaking every agent reply gets chaotic with several
# sessions open. Wiggle or Ctrl+Win+S read on demand — that's the designed flow.
# The exe still supports --claude-hook if someone wants to wire it by hand.)

# ---- 5. run it ---------------------------------------------------------------------
if (Get-Process -Name 'ReadAloud' -ErrorAction SilentlyContinue) {
    Write-Host '[--]   ReadAloud is already running - quit it from the tray icon to load the new build.'
}
else {
    Start-Process $Exe
    Write-Host '[ok]   ReadAloud started (speaker icon in the tray).'
}

Write-Host ''
Write-Host 'Done. Select text anywhere and press Ctrl+Win+S, or shake the mouse left-right.' -ForegroundColor Green
Write-Host "Settings file: $HOME\.config\readaloud\settings.json (the tray menu writes it)"
Write-Host ''
