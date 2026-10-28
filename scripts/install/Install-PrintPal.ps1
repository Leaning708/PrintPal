#requires -Version 5.1
<#
.SYNOPSIS
    Installs PrintPal (self-contained WPF app) and its local IPP queue.

.DESCRIPTION
    Default: copy the package's app folder to Program Files, install the IPP
    queue, create shortcuts, enable per-user autostart and register an uninstall
    entry. Safe to run from an elevated PowerShell. Self-elevates when needed.

    Examples:
      powershell -ExecutionPolicy Bypass -File .\Install-PrintPal.ps1
      powershell -ExecutionPolicy Bypass -File .\Install-PrintPal.ps1 -NoStart -SkipQueue
      powershell -ExecutionPolicy Bypass -File .\Install-PrintPal.ps1 -InstallDir C:\Temp\PrintPal -SkipAutostart -NoShortcuts
#>
[CmdletBinding()]
param(
    [string] $InstallDir = '',
    [string] $Source = '',
    [string] $QueueName = 'PrintPal Virtual Printer',
    [int] $Port = 8630,
    [switch] $NoStart,
    [switch] $SkipQueue,
    [switch] $SkipAutostart,
    [switch] $NoShortcuts,
    [switch] $SkipFileCopy,
    [switch] $Quiet
)

$ErrorActionPreference = 'Stop'
$modulePath = Join-Path $PSScriptRoot 'PrintPal.Common.psm1'
Import-Module $modulePath -Force -DisableNameChecking

if (-not (Test-PrintPalAdmin)) {
    Write-Host 'Requesting administrator privileges ...'
    $argList = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$PSCommandPath`"")
    foreach ($key in $PSBoundParameters.Keys) {
        $value = $PSBoundParameters[$key]
        if ($value -is [switch]) {
            if ($value.IsPresent) { $argList += "-$key" }
        } elseif ($null -ne $value -and "$value" -ne '') {
            $argList += "-$key"
            $argList += "`"$value`""
        }
    }

    try {
        Start-Process -FilePath 'powershell.exe' -Verb RunAs -ArgumentList $argList | Out-Null
        exit 0
    } catch {
        Write-Error "Elevation failed: $($_.Exception.Message)"
        exit 1
    }
}

if (-not $InstallDir) { $InstallDir = Get-PrintPalDefaultInstallDir }
$InstallDir = Assert-PrintPalInstallDir -InstallDir $InstallDir

$packageRoot = Split-Path -Parent $PSScriptRoot
$versionFile = Join-Path $packageRoot 'VERSION.txt'
$version = if (Test-Path $versionFile) { (Get-Content $versionFile -Raw).Trim() } else { '0.5.0' }

if (-not $Source) {
    $candidate = Join-Path $packageRoot 'app'
    if (Test-Path (Join-Path $candidate 'PrintPal.App.exe')) {
        $Source = $candidate
    }
}

function Write-Info([string] $message) {
    if (-not $Quiet) { Write-Host $message }
    Write-PrintPalInstallLog $message
}

Write-Info "PrintPal $version install started. InstallDir=$InstallDir Source=$Source"

New-Item -ItemType Directory -Force -Path $InstallDir | Out-Null

if (-not $SkipFileCopy) {
    if (-not $Source -or -not (Test-Path (Join-Path $Source 'PrintPal.App.exe'))) {
        throw "Package source not found. Expected app\PrintPal.App.exe under the package root, or pass -Source."
    }

    Write-Info 'Stopping running PrintPal.App ...'
    Stop-PrintPalApp

    Write-Info "Copying app files from $Source ..."
    Copy-Item (Join-Path $Source '*') $InstallDir -Recurse -Force

    $installerDir = Join-Path $InstallDir 'installer'
    New-Item -ItemType Directory -Force -Path $installerDir | Out-Null
    Copy-Item (Join-Path $PSScriptRoot '*') $installerDir -Recurse -Force
}

$appExe = Join-Path $InstallDir 'PrintPal.App.exe'
if (-not (Test-Path $appExe)) {
    throw "PrintPal.App.exe not found in $InstallDir"
}

if (-not $SkipQueue) {
    if (-not (Test-PrintPalHealth -Port $Port)) {
        if ($NoStart) {
            throw "The PrintPal agent is not running on port $Port. Start PrintPal.App.exe first, or omit -NoStart."
        }

        Write-Info 'Starting PrintPal.App in the background ...'
        if (-not (Start-PrintPalApp -InstallDir $InstallDir -Port $Port)) {
            throw "PrintPal agent did not become healthy on port $Port."
        }
    }

    Write-Info "Installing printer queue '$QueueName' -> http://127.0.0.1:$Port/ipp/print ..."
    $queue = Install-PrintPalQueue -Url "http://127.0.0.1:$Port/ipp/print" -Name $QueueName
    Write-Info "Queue installed: $($queue.Name), driver=$($queue.DriverName), port=$($queue.PortName), status=$($queue.PrinterStatus)"
}

if (-not $SkipAutostart) {
    Write-Info 'Enabling per-user autostart ...'
    Set-PrintPalAutostart -InstallDir $InstallDir
}

if (-not $NoShortcuts) {
    Write-Info 'Creating shortcuts ...'
    New-PrintPalShortcuts -InstallDir $InstallDir
}

Write-Info 'Writing uninstall registry entry ...'
Write-PrintPalUninstallRegistry -InstallDir $InstallDir -Version $version
Write-PrintPalManifest -InstallDir $InstallDir -Version $version -QueueName $QueueName -Port $Port

if (-not $NoStart -and -not (Test-PrintPalHealth -Port $Port)) {
    Write-Info 'Starting PrintPal.App in the background ...'
    if (-not (Start-PrintPalApp -InstallDir $InstallDir -Port $Port)) {
        Write-Warning "PrintPal was installed but the agent is not healthy on port $Port."
    }
}

Write-Info 'Install completed.'
if (-not $Quiet) {
    Write-Host ''
    Write-Host '=== PrintPal installed ==='
    Write-Host "  Version : $version"
    Write-Host "  Install : $InstallDir"
    Write-Host "  Data    : $env:LOCALAPPDATA\PrintPal"
    Write-Host "  Queue   : $QueueName"
    Write-Host "  App     : $appExe"
    Write-Host ''
    Write-Host 'Open PrintPal from the Start Menu. Uninstall from Apps & Features or:'
    Write-Host "  powershell -ExecutionPolicy Bypass -File `"$InstallDir\installer\Uninstall-PrintPal.ps1`""
}

exit 0

