#requires -Version 5.1
<#
.SYNOPSIS
    Uninstalls PrintPal: stops the app, removes the queue, autostart, shortcuts,
    uninstall entry and program files. User data is kept unless -RemoveData.
#>
[CmdletBinding()]
param(
    [string] $InstallDir = '',
    [string] $QueueName = 'PrintPal Virtual Printer',
    [switch] $RemoveData,
    [switch] $Quiet
)

$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'PrintPal.Common.psm1') -Force -DisableNameChecking

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

function Write-Info([string] $message) {
    if (-not $Quiet) { Write-Host $message }
    Write-PrintPalInstallLog $message
}

Write-Info "PrintPal uninstall started. InstallDir=$InstallDir"

Write-Info 'Stopping PrintPal.App ...'
Stop-PrintPalApp

Write-Info "Removing queue '$QueueName' ..."
Remove-PrintPalQueue -Name $QueueName

Write-Info 'Removing autostart and shortcuts ...'
Set-PrintPalAutostart -InstallDir $InstallDir -Disable
Remove-PrintPalShortcuts
Remove-PrintPalUninstallRegistry

if (Test-Path $InstallDir) {
    Write-Info "Removing program files from $InstallDir ..."
    Remove-Item $InstallDir -Recurse -Force
}

if ($RemoveData) {
    $dataDir = Join-Path $env:LOCALAPPDATA 'PrintPal'
    $fullData = [IO.Path]::GetFullPath($dataDir).TrimEnd('\')
    if ($fullData -match '^[A-Za-z]:\\?$' -or (Split-Path $fullData -Leaf) -ne 'PrintPal') {
        throw "Refusing to delete unexpected data directory: $fullData"
    }

    if (Test-Path $fullData) {
        Write-Info "Removing user data at $fullData ..."
        Remove-Item $fullData -Recurse -Force
    }
}

Write-Info 'Uninstall completed.'
if (-not $Quiet) {
    Write-Host ''
    Write-Host '=== PrintPal uninstalled ==='
    if (-not $RemoveData) {
        Write-Host "User data kept at: $env:LOCALAPPDATA\PrintPal"
        Write-Host 'Pass -RemoveData to delete it.'
    }
}

exit 0

