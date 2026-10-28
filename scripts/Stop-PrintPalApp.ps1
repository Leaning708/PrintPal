#requires -Version 5.1
<#
.SYNOPSIS
    Stops the PrintPal WPF application started by Start-PrintPalApp.ps1.
    Note: this is a development helper; production exit should use the tray menu.
#>
[CmdletBinding()]
param(
    [string] $Root = (Split-Path -Parent $PSScriptRoot),
    [string] $Data = ''
)

$ErrorActionPreference = 'Continue'

if (-not $Data) { $Data = Join-Path $Root 'artifacts\p3\data' }
$infoFile = Join-Path $Data 'app.json'

if (Test-Path $infoFile) {
    $info = Get-Content $infoFile -Raw | ConvertFrom-Json
    try {
        Stop-Process -Id $info.pid -Force -ErrorAction Stop
        Write-Host "Stopped PrintPal.App (pid $($info.pid))"
    } catch {
        Write-Host "Could not stop pid $($info.pid): $($_.Exception.Message)"
    }

    Remove-Item $infoFile -Force -ErrorAction SilentlyContinue
} else {
    Write-Host "No app info file at $infoFile"
}

Get-Process PrintPal.App -ErrorAction SilentlyContinue |
    ForEach-Object {
        Write-Host "Stopping leftover PrintPal.App pid $($_.Id)"
        Stop-Process -Id $_.Id -Force -ErrorAction SilentlyContinue
    }
