#requires -RunAsAdministrator
<#
.SYNOPSIS
    P0 cleanup: restart the spooler and remove the probe queue/port.

.DESCRIPTION
    A stuck IPP job can keep the queue in "Error/Printing" and prevent a clean
    re-add. Restarting the spooler clears the job. The queue and its IPP port are
    then removed so the next Add-Printer starts from a known state.

    This script is intentionally separate from Install-P0Queue.ps1 because P0 is
    an experiment: we need to be able to reset the Windows side without touching
    the probe or the future PrintPal code.
#>
[CmdletBinding()]
param(
    [string[]] $Names = @('PrintPal Virtual Printer', 'PrintPal 虚拟打印机'),
    [string] $LogPath = 'C:\Develop\code\AGI\PrintPal\artifacts\p0\reset-queue.log'
)

$ErrorActionPreference = 'Continue'
New-Item -ItemType Directory -Force -Path (Split-Path -Parent $LogPath) | Out-Null
Start-Transcript -Path $LogPath -Force | Out-Null

try {
    Write-Host "=== Restarting Print Spooler ==="
    Restart-Service -Name Spooler -Force -ErrorAction Continue
    Start-Sleep -Seconds 2
    Get-Service Spooler | Select-Object Name, Status | Format-List | Out-String | Write-Host

    foreach ($name in $Names) {
        $queue = Get-Printer -Name $name -ErrorAction SilentlyContinue
        if (-not $queue) {
            Write-Host "Queue '$name' not found."
            continue
        }

        $port = $queue.PortName
        Write-Host "Removing queue '$name' (port '$port') ..."
        Remove-Printer -Name $name -ErrorAction Continue
        Start-Sleep -Seconds 1

        if ($port) {
            $used = Get-Printer -ErrorAction SilentlyContinue | Where-Object { $_.PortName -eq $port }
            if (-not $used) {
                Write-Host "Removing unused port '$port' ..."
                Remove-PrinterPort -Name $port -ErrorAction Continue
            }
        }
    }

    Write-Host ""
    Write-Host "=== Printers ==="
    Get-Printer | Select-Object Name, DriverName, PortName | Format-Table -AutoSize | Out-String | Write-Host

    Write-Host "=== Ports ==="
    Get-PrinterPort | Select-Object Name, Description | Format-Table -AutoSize | Out-String | Write-Host
}
finally {
    Stop-Transcript | Out-Null
}

exit 0
