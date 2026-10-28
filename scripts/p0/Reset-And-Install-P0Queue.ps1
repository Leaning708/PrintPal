#requires -RunAsAdministrator
<#
.SYNOPSIS
    P0 iteration helper: restart the spooler, remove the probe queue, and add it again.

.DESCRIPTION
    The IPP Class Driver caches printer capabilities and can leave a queue in an
    Error state after a failed job. During P0 we need a repeatable reset + add
    cycle while the probe is running.

    The probe must already be listening at $Url/health. This script does NOT start
    the probe.
#>
[CmdletBinding()]
param(
    [string] $Url = 'http://127.0.0.1:8630/ipp/print',
    [string] $Name = 'PrintPal 虚拟打印机',
    [string] $LogPath = 'C:\Develop\code\AGI\PrintPal\artifacts\p0\reset-install.log'
)

$ErrorActionPreference = 'Continue'
New-Item -ItemType Directory -Force -Path (Split-Path -Parent $LogPath) | Out-Null
Start-Transcript -Path $LogPath -Force | Out-Null

try {
    Write-Host "=== P0 reset + install $(Get-Date -Format o) ==="

    Write-Host "Restarting spooler ..."
    Restart-Service -Name Spooler -Force -ErrorAction Continue
    Start-Sleep -Seconds 2
    Get-Service Spooler | Select-Object Name, Status | Format-List | Out-String | Write-Host

    foreach ($n in @('PrintPal Virtual Printer', $Name)) {
        $existing = Get-Printer -Name $n -ErrorAction SilentlyContinue
        if ($existing) {
            Write-Host "Removing queue '$n' ..."
            Remove-Printer -Name $n -ErrorAction Continue
            Start-Sleep -Milliseconds 500
        }
    }

    $healthUrl = $Url -replace '/ipp/print$', '/health'
    Write-Host "Probe health: $healthUrl"
    $health = Invoke-RestMethod -Uri $healthUrl -TimeoutSec 10
    Write-Host ($health | ConvertTo-Json -Compress)

    Write-Host "Adding queue via Add-Printer -IppURL ..."
    $created = $null
    try {
        $created = Add-Printer -IppURL $Url -ErrorAction Stop
        Write-Host "  returned: $($created | Out-String)"
    } catch {
        Write-Warning "Add-Printer -IppURL failed: $($_.Exception.Message)"
        Write-Host "Trying legacy Add-Printer -PortName ..."
        $created = Add-Printer -Name $Name -DriverName 'Microsoft IPP Class Driver' -PortName $Url -ErrorAction Stop
    }

    if (-not $created) {
        Write-Host "Add-Printer returned no object; polling by name ..."
        for ($i = 0; $i -lt 20 -and -not $created; $i++) {
            Start-Sleep -Milliseconds 500
            $created = Get-Printer -Name 'PrintPal Virtual Printer' -ErrorAction SilentlyContinue
            if (-not $created) {
                $created = Get-Printer -Name $Name -ErrorAction SilentlyContinue
            }
        }
    }

    if (-not $created) {
        throw 'No queue appeared after Add-Printer.'
    }

    if ($created.Name -ne $Name) {
        Write-Host "Renaming '$($created.Name)' -> '$Name' ..."
        Rename-Printer -Name $created.Name -NewName $Name -ErrorAction Stop
    }

    Start-Sleep -Milliseconds 500
    Write-Host "=== Result ==="
    Get-Printer -Name $Name -ErrorAction Stop |
        Select-Object Name, DriverName, PortName, PrinterStatus, JobCount |
        Format-List | Out-String | Write-Host
}
catch {
    Write-Host "FAILED: $($_.Exception.Message)" -ForegroundColor Red
    Write-Host $_.ScriptStackTrace
    Get-Printer -ErrorAction SilentlyContinue | Select-Object Name, DriverName, PortName | Format-Table -AutoSize | Out-String | Write-Host
}
finally {
    Stop-Transcript | Out-Null
}

exit 0
