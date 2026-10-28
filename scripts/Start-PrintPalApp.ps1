#requires -Version 5.1
<#
.SYNOPSIS
    Starts the PrintPal WPF application (which hosts the IPP agent).
#>
[CmdletBinding()]
param(
    [string] $Root = (Split-Path -Parent $PSScriptRoot),
    [string] $Data = '',
    [int] $Port = 8630,
    [switch] $SeedDemo,
    [switch] $NoAutoForward,
    [switch] $Background,
    [string] $Exe = ''
)

$ErrorActionPreference = 'Stop'

if (-not $Data) { $Data = Join-Path $Root 'artifacts\p3\data' }
if (-not $Exe) { $Exe = Join-Path $Root 'src\PrintPal.App\bin\Debug\net10.0-windows\PrintPal.App.exe' }

New-Item -ItemType Directory -Force -Path $Data | Out-Null

$existing = Get-Process PrintPal.App -ErrorAction SilentlyContinue | Select-Object -First 1
if ($existing) {
    Write-Host "PrintPal.App is already running (pid $($existing.Id))."
    exit 0
}

if (-not (Test-Path $Exe)) {
    Write-Host 'Building PrintPal.App ...'
    & dotnet build (Join-Path $Root 'src\PrintPal.App\PrintPal.App.csproj') -c Debug --nologo | Out-Null
}

if (-not (Test-Path $Exe)) { throw "PrintPal.App executable not found: $Exe" }

$arguments = @('--port', [string]$Port, '--data', "`"$Data`"")
if ($SeedDemo) { $arguments += '--seed-demo' }
if ($NoAutoForward) { $arguments += '--no-auto-forward' }
if ($Background) { $arguments += '--background' }

$process = Start-Process -FilePath $Exe -ArgumentList $arguments -PassThru

$info = [pscustomobject]@{
    pid = $process.Id
    port = $Port
    data = $Data
    startedAt = (Get-Date).ToString('o')
    exe = $Exe
}
$info | ConvertTo-Json | Set-Content -Path (Join-Path $Data 'app.json') -Encoding UTF8

Start-Sleep -Seconds 3

try {
    $health = Invoke-RestMethod -Uri "http://127.0.0.1:$Port/health" -TimeoutSec 5
    Write-Host '=== PrintPal App ==='
    Write-Host "  pid  : $($process.Id)"
    Write-Host "  IPP  : $($health.ippUri)"
    Write-Host "  data : $Data"
} catch {
    Write-Host "PrintPal.App started but /health did not answer: $($_.Exception.Message)"
    Write-Host "Check $(Join-Path $Data 'logs\app.log')"
}
