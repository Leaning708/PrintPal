#requires -Version 5.1
<#
.SYNOPSIS
    Repairs a PrintPal installation: restores the queue, autostart, shortcuts and
    uninstall entry. Pass -Source to re-copy program files from a package.
#>
[CmdletBinding()]
param(
    [string] $InstallDir = '',
    [string] $Source = '',
    [string] $QueueName = 'PrintPal Virtual Printer',
    [int] $Port = 8630,
    [switch] $SkipAutostart,
    [switch] $NoShortcuts,
    [switch] $NoStart,
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

$appExe = Join-Path $InstallDir 'PrintPal.App.exe'
if (-not (Test-Path $appExe) -and -not $Source) {
    throw "PrintPal.App.exe is missing from $InstallDir. Re-run with -Source <package>\app."
}

$installScript = Join-Path $PSScriptRoot 'Install-PrintPal.ps1'
if (-not (Test-Path $installScript)) {
    throw "Installer script not found: $installScript"
}

$installArgs = @{
    InstallDir = $InstallDir
    QueueName = $QueueName
    Port = $Port
    Quiet = $Quiet
}

if ($Source) {
    $installArgs.Source = $Source
} else {
    $installArgs.SkipFileCopy = $true
}

if ($SkipAutostart) { $installArgs.SkipAutostart = $true }
if ($NoShortcuts) { $installArgs.NoShortcuts = $true }
if ($NoStart) { $installArgs.NoStart = $true }

Write-Host 'Repairing PrintPal ...'
& $installScript @installArgs
exit $LASTEXITCODE

