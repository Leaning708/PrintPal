#requires -RunAsAdministrator
<#
.SYNOPSIS
    Creates the "PrintPal Virtual Printer" queue pointing at the local agent.

.DESCRIPTION
    Windows 10/11 both support the Microsoft IPP Class Driver, but Windows 10 hides
    it from the Add Printer model list. This script installs the driver explicitly
    (Add-PrinterDriver), then creates an IPP queue and verifies it.

    The agent must already be running. Start it with Start-PrintPalAgent.ps1.

    Run from an elevated PowerShell:
      powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\Install-PrintPalQueue.ps1
#>
[CmdletBinding()]
param(
    [string] $Url = 'http://127.0.0.1:8630/ipp/print',
    [string] $Name = 'PrintPal Virtual Printer',
    [string] $LogPath = 'C:\Develop\code\AGI\PrintPal\artifacts\p2\install-queue.log'
)

$ErrorActionPreference = 'Stop'
New-Item -ItemType Directory -Force -Path (Split-Path -Parent $LogPath) | Out-Null
Start-Transcript -Path $LogPath -Force | Out-Null

try {
    Write-Host "=== PrintPal queue install ==="
    Write-Host "URL  : $Url"
    Write-Host "Name : $Name"
    Write-Host "Time : $(Get-Date -Format o)"

    $healthUrl = $Url -replace '/ipp/print$', '/health'
    Write-Host "Probe health: $healthUrl"
    $health = Invoke-RestMethod -Uri $healthUrl -TimeoutSec 10
    Write-Host ($health | ConvertTo-Json -Compress)

    Write-Host ''
    Write-Host '=== IPP Class Driver ==='
    $driver = Get-PrinterDriver -Name 'Microsoft IPP Class Driver' -ErrorAction SilentlyContinue
    if (-not $driver) {
        Write-Host 'Installing Microsoft IPP Class Driver from the inbox INF ...'
        Add-PrinterDriver -Name 'Microsoft IPP Class Driver' -ErrorAction Stop
        $driver = Get-PrinterDriver -Name 'Microsoft IPP Class Driver' -ErrorAction Stop
    }
    $driver | Select-Object Name, MajorVersion, DataFile | Format-List | Out-String | Write-Host

    Write-Host '=== Existing queue cleanup ==='
    foreach ($existingName in @($Name, 'PrintPal Virtual Printer')) {
        $existing = Get-Printer -Name $existingName -ErrorAction SilentlyContinue
        if ($existing) {
            Write-Host "Removing queue '$existingName' ..."
            Remove-Printer -Name $existingName -ErrorAction Continue
            Start-Sleep -Milliseconds 500
        }
    }

    Write-Host '=== Adding queue ==='
    $created = $null
    $hasIppUrl = (Get-Command Add-Printer).Parameters.ContainsKey('IppURL')

    if ($hasIppUrl) {
        try {
            Write-Host 'Trying Add-Printer -IppURL ...'
            $created = Add-Printer -IppURL $Url -ErrorAction Stop
            Write-Host "  returned: $($created | Out-String)"
        } catch {
            Write-Warning "Add-Printer -IppURL failed: $($_.Exception.Message)"
        }
    }

    if (-not $created) {
        try {
            Write-Host 'Trying Add-Printer -Name ... -PortName http://...'
            $created = Add-Printer -Name $Name -DriverName 'Microsoft IPP Class Driver' -PortName $Url -ErrorAction Stop
        } catch {
            Write-Warning "Legacy Add-Printer failed: $($_.Exception.Message)"
        }
    }

    if (-not $created) {
        Write-Host 'Add-Printer returned no object; polling by name ...'
        for ($i = 0; $i -lt 30 -and -not $created; $i++) {
            Start-Sleep -Milliseconds 500
            $created = Get-Printer -Name 'PrintPal Virtual Printer' -ErrorAction SilentlyContinue
            if (-not $created) {
                $created = Get-Printer -Name $Name -ErrorAction SilentlyContinue
            }
        }
    }

    if (-not $created) {
        throw 'Queue was not created.'
    }

    if ($created.Name -ne $Name) {
        Write-Host "Renaming '$($created.Name)' -> '$Name' ..."
        Rename-Printer -Name $created.Name -NewName $Name -ErrorAction Stop
    }

    Start-Sleep -Milliseconds 500

    $queue = Get-Printer -Name $Name -ErrorAction Stop
    Write-Host ''
    Write-Host '=== Queue installed ==='
    $queue | Select-Object Name, DriverName, PortName, PrinterStatus, JobCount |
        Format-List | Out-String | Write-Host

    if ($queue.PrinterStatus -ne 'Normal') {
        Write-Host "WARNING: printer status is $($queue.PrinterStatus)" -ForegroundColor Yellow
    }

    Write-Host "SUCCESS: queue '$Name' exists."
} catch {
    Write-Host "FAILED: $($_.Exception.Message)" -ForegroundColor Red
    Write-Host $_.ScriptStackTrace
    Get-Printer -ErrorAction SilentlyContinue |
        Select-Object Name, DriverName, PortName | Format-Table -AutoSize | Out-String | Write-Host
} finally {
    Stop-Transcript | Out-Null
}

exit 0
