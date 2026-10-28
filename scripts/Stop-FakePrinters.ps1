#requires -Version 5.1
<#
.SYNOPSIS
    Stops the three FakePrinter instances started by Start-FakePrinters.ps1.
#>
[CmdletBinding()]
param(
    [string] $Root = (Split-Path -Parent $PSScriptRoot),
    [string] $Output = ''
)

$ErrorActionPreference = 'Continue'

if (-not $Output) { $Output = Join-Path $Root 'artifacts\p2\fake-printers' }
$pidFile = Join-Path $Output 'pids.json'

if (Test-Path $pidFile) {
    foreach ($entry in (Get-Content $pidFile -Raw | ConvertFrom-Json)) {
        try {
            Stop-Process -Id $entry.pid -Force -ErrorAction Stop
            Write-Host "Stopped $($entry.name) (pid $($entry.pid))"
        } catch {
            Write-Host "Could not stop $($entry.name) (pid $($entry.pid)): $($_.Exception.Message)"
        }
    }

    Remove-Item $pidFile -Force -ErrorAction SilentlyContinue
} else {
    Write-Host "No pid file at $pidFile"
}

Get-Process PrintPal.FakePrinter -ErrorAction SilentlyContinue |
    ForEach-Object {
        Write-Host "Stopping leftover PrintPal.FakePrinter pid $($_.Id)"
        Stop-Process -Id $_.Id -Force -ErrorAction SilentlyContinue
    }
