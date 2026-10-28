#requires -RunAsAdministrator
<#
.SYNOPSIS
    P0 probe: create a Windows IPP printer queue that points at PrintPal.IppProbe.

.DESCRIPTION
    Windows 11 24H2+ supports `Add-Printer -IppURL`, which also works with
    Windows protected print mode. Windows 10 uses the older
    `Add-Printer -Name ... -DriverName "Microsoft IPP Class Driver" -PortName <url>`
    form. This script tries the modern form first and falls back to the legacy form.

    The probe server must already be running and reachable at the given URL.
    This script writes a transcript next to the P0 artifacts so the (non-elevated)
    caller can read exactly what happened.
#>
[CmdletBinding()]
param(
    [string] $Url = 'http://127.0.0.1:8630/ipp/print',
    [string] $Name = 'PrintPal 虚拟打印机',
    [string] $LogPath = 'C:\Develop\code\AGI\PrintPal\artifacts\p0\install-queue.log'
)

$ErrorActionPreference = 'Stop'
New-Item -ItemType Directory -Force -Path (Split-Path -Parent $LogPath) | Out-Null
Start-Transcript -Path $LogPath -Force | Out-Null

$exitCode = 1

try {
    Write-Host "=== P0 IPP queue install ==="
    Write-Host "URL  : $Url"
    Write-Host "Name : $Name"
    Write-Host "Time : $(Get-Date -Format o)"
    Write-Host "User : $([Security.Principal.WindowsIdentity]::GetCurrent().Name)"
    Write-Host "OS   : $((Get-CimInstance Win32_OperatingSystem).Caption) build $((Get-CimInstance Win32_OperatingSystem).BuildNumber)"
    Write-Host ""

    $health = Invoke-RestMethod -Uri ($Url -replace '/ipp/print$', '/health') -TimeoutSec 10
    Write-Host "Probe health: $($health | ConvertTo-Json -Compress)"
    Write-Host ""

    $existing = Get-Printer -Name $Name -ErrorAction SilentlyContinue
    if ($existing) {
        Write-Host "Removing existing queue '$Name' ..."
        Remove-Printer -Name $Name -ErrorAction Continue
        Start-Sleep -Seconds 1
    }

    $created = $null

    # Preferred: Windows 11 24H2+ / protected print mode aware.
    try {
        Write-Host "Trying Add-Printer -IppURL ..."
        $created = Add-Printer -IppURL $Url -ErrorAction Stop
        Write-Host "  Add-Printer -IppURL returned: $($created | Out-String)"
    } catch {
        Write-Warning "Add-Printer -IppURL failed: $($_.Exception.Message)"

        Write-Host "Trying legacy Add-Printer -PortName ..."
        $created = Add-Printer -Name $Name -DriverName 'Microsoft IPP Class Driver' -PortName $Url -ErrorAction Stop
        Write-Host "  Add-Printer legacy returned: $($created | Out-String)"
    }

    # Add-Printer -IppURL on Windows 11 can create the queue successfully but
    # return no object (observed on build 26300). Poll for the queue by name
    # before declaring failure.
    if (-not $created) {
        Write-Host "Add-Printer returned no object; polling for the queue by name ..."
        for ($i = 0; $i -lt 20 -and -not $created; $i++) {
            Start-Sleep -Milliseconds 500
            $created = Get-Printer -Name 'PrintPal Virtual Printer' -ErrorAction SilentlyContinue
            if (-not $created) {
                $created = Get-Printer -Name $Name -ErrorAction SilentlyContinue
            }
        }
    }

    if (-not $created) {
        throw 'Add-Printer returned no printer object and no queue appeared.'
    }

    $createdName = $created.Name
    Write-Host "Created queue name: $createdName"

    if ($createdName -ne $Name) {
        Write-Host "Renaming '$createdName' -> '$Name' ..."
        Rename-Printer -Name $createdName -NewName $Name -ErrorAction Stop
    }

    Start-Sleep -Seconds 1

    $queue = Get-Printer -Name $Name -ErrorAction Stop
    Write-Host ""
    Write-Host "=== Queue installed ==="
    $queue | Select-Object Name, DriverName, PortName, Shared, Published | Format-List | Out-String | Write-Host

    Write-Host "=== Driver ==="
    Get-PrinterDriver -Name $queue.DriverName -ErrorAction SilentlyContinue |
        Select-Object Name, MajorVersion, ConfigFile, DataFile | Format-List | Out-String | Write-Host

    Write-Host "=== Port ==="
    Get-PrinterPort -Name $queue.PortName -ErrorAction SilentlyContinue |
        Select-Object Name, Description, PrinterHostAddress, PortNumber | Format-List | Out-String | Write-Host

    Write-Host "SUCCESS: queue '$Name' exists."
    $exitCode = 0
}
catch {
    Write-Host ""
    Write-Host "FAILED: $($_.Exception.Message)" -ForegroundColor Red
    Write-Host $_.ScriptStackTrace

    Write-Host ""
    Write-Host "=== Printers currently installed ==="
    Get-Printer -ErrorAction SilentlyContinue | Select-Object Name, DriverName, PortName | Format-Table -AutoSize | Out-String | Write-Host

    Write-Host "=== IPP driver ==="
    Get-PrinterDriver -Name 'Microsoft IPP Class Driver' -ErrorAction SilentlyContinue |
        Format-List | Out-String | Write-Host
}
finally {
    Stop-Transcript | Out-Null
}

exit $exitCode
