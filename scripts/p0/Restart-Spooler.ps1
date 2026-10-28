#requires -RunAsAdministrator
<#
.SYNOPSIS
    Restarts the Print Spooler and reports queue/job state.

.DESCRIPTION
    Used during P0 when an IPP probe job is stuck in "Error, Printing, Retained"
    and the queue cannot be cleaned without recycling the spooler. This does not
    remove any printer queues.
#>
[CmdletBinding()]
param(
    [string] $LogPath = 'C:\Develop\code\AGI\PrintPal\artifacts\p0\restart-spooler.log'
)

$ErrorActionPreference = 'Continue'
New-Item -ItemType Directory -Force -Path (Split-Path -Parent $LogPath) | Out-Null
Start-Transcript -Path $LogPath -Force | Out-Null

try {
    Write-Host "Restarting Print Spooler at $(Get-Date -Format o) ..."
    Restart-Service -Name Spooler -Force -ErrorAction Continue
    Start-Sleep -Seconds 2
    Get-Service Spooler | Select-Object Name, Status | Format-List | Out-String | Write-Host
    Write-Host "=== Printers ==="
    Get-Printer | Select-Object Name, DriverName, PortName, PrinterStatus, JobCount | Format-Table -AutoSize | Out-String | Write-Host
    Write-Host "=== Jobs ==="
    Get-PrintJob -ErrorAction SilentlyContinue | Select-Object Id, PrinterName, DocumentName, JobStatus, PagesPrinted, TotalPages, Size | Format-Table -AutoSize | Out-String | Write-Host
}
finally {
    Stop-Transcript | Out-Null
}

exit 0
