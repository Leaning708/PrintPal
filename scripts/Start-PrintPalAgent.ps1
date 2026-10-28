#requires -Version 5.1
<#
.SYNOPSIS
    Starts the PrintPal Agent (IPP server + capture pipeline + SQLite).
#>
[CmdletBinding()]
param(
    [string] $Root = (Split-Path -Parent $PSScriptRoot),
    [int] $Port = 8630,
    [string] $Data = '',
    [string] $Bind = '127.0.0.1',
    [string] $HostName = '127.0.0.1',
    [switch] $NoAutoForward,
    [switch] $NoSeedDemo,
    [string] $Exe = ''
)

$ErrorActionPreference = 'Stop'

if (-not $Data) { $Data = Join-Path $Root 'artifacts\p2\data' }
if (-not $Exe) { $Exe = Join-Path $Root 'src\PrintPal.AgentHost\bin\Debug\net10.0\PrintPal.AgentHost.exe' }

New-Item -ItemType Directory -Force -Path $Data | Out-Null

if (-not (Test-Path $Exe)) {
    Write-Host 'Building PrintPal.AgentHost ...'
    & dotnet build (Join-Path $Root 'src\PrintPal.AgentHost\PrintPal.AgentHost.csproj') -c Debug --nologo | Out-Null
}

if (-not (Test-Path $Exe)) { throw "Agent host executable not found: $Exe" }

$outLog = Join-Path $Data 'agent.out.log'
$errLog = Join-Path $Data 'agent.err.log'

$arguments = @(
    '--port', [string]$Port,
    '--data', "`"$Data`"",
    '--bind', $Bind,
    '--host', $HostName
)
if ($NoAutoForward) { $arguments += '--no-auto-forward' }
if (-not $NoSeedDemo) { $arguments += '--seed-demo' }

$process = Start-Process -FilePath $Exe -ArgumentList $arguments -WindowStyle Hidden `
    -RedirectStandardOutput $outLog -RedirectStandardError $errLog -PassThru

$info = [pscustomobject]@{
    pid = $process.Id
    port = $Port
    data = $Data
    startedAt = (Get-Date).ToString('o')
    outLog = $outLog
    errLog = $errLog
}
$info | ConvertTo-Json | Set-Content -Path (Join-Path $Data 'agent.json') -Encoding UTF8

Start-Sleep -Seconds 2

try {
    $health = Invoke-RestMethod -Uri "http://127.0.0.1:$Port/health" -TimeoutSec 5
    Write-Host '=== PrintPal Agent ==='
    Write-Host "  pid    : $($process.Id)"
    Write-Host "  IPP    : $($health.ippUri)"
    Write-Host "  HTTP   : $($health.httpUri)"
    Write-Host "  data   : $Data"
    Write-Host "  log    : $outLog"
} catch {
    Write-Host "Agent did not answer /health: $($_.Exception.Message)"
    Write-Host "Check $errLog and $outLog"
}
