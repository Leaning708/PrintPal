#requires -Version 5.1
<#
.SYNOPSIS
    Read-only PrintPal diagnostic: driver, queue, agent health, data and logs.
#>
[CmdletBinding()]
param(
    [string] $Root = (Split-Path -Parent $PSScriptRoot),
    [string] $Data = '',
    [int] $Port = 8630,
    [string] $QueueName = 'PrintPal Virtual Printer'
)

$ErrorActionPreference = 'Continue'

if (-not $Data) { $Data = Join-Path $Root 'artifacts\p3\data' }

Write-Host '=== PrintPal diagnostics ==='
Write-Host "Time : $(Get-Date -Format o)"
Write-Host "OS   : $((Get-CimInstance Win32_OperatingSystem).Caption) build $((Get-CimInstance Win32_OperatingSystem).BuildNumber)"
Write-Host "Data : $Data"
Write-Host ''

Write-Host '=== IPP Class Driver ==='
$driver = Get-PrinterDriver -Name 'Microsoft IPP Class Driver' -ErrorAction SilentlyContinue
if ($driver) {
    $driver | Select-Object Name, MajorVersion, DataFile | Format-List | Out-String | Write-Host
} else {
    Write-Host 'NOT INSTALLED'
}

Write-Host '=== Queue ==='
Get-Printer -Name $QueueName -ErrorAction SilentlyContinue |
    Select-Object Name, DriverName, PortName, PrinterStatus, JobCount |
    Format-List | Out-String | Write-Host

Write-Host '=== Agent health ==='
try {
    Invoke-RestMethod -Uri "http://127.0.0.1:$Port/health" -TimeoutSec 5 |
        ConvertTo-Json -Depth 5 | Out-String | Write-Host
} catch {
    Write-Host "FAILED: $($_.Exception.Message)"
}

Write-Host '=== Agent process ==='
Get-Process PrintPal.AgentHost -ErrorAction SilentlyContinue |
    Select-Object Id, ProcessName, StartTime, CPU, WorkingSet |
    Format-Table -AutoSize | Out-String | Write-Host

Write-Host '=== WPF app process ==='
Get-Process PrintPal.App -ErrorAction SilentlyContinue |
    Select-Object Id, ProcessName, MainWindowTitle, StartTime, CPU, WorkingSet |
    Format-Table -AutoSize | Out-String | Write-Host

Write-Host '=== Data files ==='
if (Test-Path $Data) {
    Get-ChildItem $Data -Force | Select-Object Name, Length, LastWriteTime | Format-Table -AutoSize | Out-String | Write-Host
    $db = Join-Path $Data 'printpal.db'
    if (Test-Path $db) {
        Write-Host "Database: $db ($((Get-Item $db).Length) bytes)"
    }
} else {
    Write-Host "Data directory not found: $Data"
}

Write-Host '=== Recent agent log ==='
$outLog = Join-Path $Data 'agent.out.log'
if (Test-Path $outLog) {
    Get-Content $outLog -Tail 40
} else {
    Write-Host "No log at $outLog"
}

Write-Host ''
Write-Host '=== Print jobs (latest 20) ==='
Get-PrintJob -PrinterName $QueueName -ErrorAction SilentlyContinue |
    Select-Object Id, DocumentName, JobStatus, PagesPrinted |
    Format-Table -AutoSize | Out-String | Write-Host
