#requires -Version 5.1
<#
.SYNOPSIS
    Stops the PrintPal Agent started by Start-PrintPalAgent.ps1.
#>
[CmdletBinding()]
param(
    [string] $Root = '',
    [string] $Data = ''
)

$ErrorActionPreference = 'Continue'

# Resolve in the body: with [CmdletBinding()] and `-File`, $PSScriptRoot can be
# empty while parameter defaults are evaluated.
if (-not $Root) {
    $scriptDirectory = if ($PSScriptRoot) { $PSScriptRoot } elseif ($PSCommandPath) { Split-Path -Parent $PSCommandPath } else { (Get-Location).Path }
    $Root = (Resolve-Path -LiteralPath (Join-Path $scriptDirectory '..')).Path
}

if (-not $Data) { $Data = Join-Path $Root 'artifacts\p2\data' }
$infoFile = Join-Path $Data 'agent.json'

if (Test-Path $infoFile) {
    $info = Get-Content $infoFile -Raw | ConvertFrom-Json
    try {
        Stop-Process -Id $info.pid -Force -ErrorAction Stop
        Write-Host "Stopped PrintPal Agent (pid $($info.pid))"
    } catch {
        Write-Host "Could not stop agent pid $($info.pid): $($_.Exception.Message)"
    }

    Remove-Item $infoFile -Force -ErrorAction SilentlyContinue
} else {
    Write-Host "No agent info file at $infoFile"
}

Get-Process PrintPal.AgentHost -ErrorAction SilentlyContinue |
    ForEach-Object {
        Write-Host "Stopping leftover PrintPal.AgentHost pid $($_.Id)"
        Stop-Process -Id $_.Id -Force -ErrorAction SilentlyContinue
    }
