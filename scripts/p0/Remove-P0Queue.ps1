#requires -RunAsAdministrator
<#
.SYNOPSIS
    Removes the P0 probe queue and its port (if the port was created by us).
#>
[CmdletBinding()]
param(
    [string] $Name = 'PrintPal 虚拟打印机',
    [string] $LogPath = 'C:\Develop\code\AGI\PrintPal\artifacts\p0\remove-queue.log'
)

$ErrorActionPreference = 'Continue'
New-Item -ItemType Directory -Force -Path (Split-Path -Parent $LogPath) | Out-Null
Start-Transcript -Path $LogPath -Force | Out-Null

try {
    $queue = Get-Printer -Name $Name -ErrorAction SilentlyContinue
    if ($queue) {
        $port = $queue.PortName
        Write-Host "Removing queue '$Name' (port '$port') ..."
        Remove-Printer -Name $Name
        Start-Sleep -Seconds 1

        if ($port -like 'http*') {
            $portStillUsed = Get-Printer | Where-Object PortName -eq $port
            if (-not $portStillUsed) {
                Write-Host "Removing IPP port '$port' ..."
                Remove-PrinterPort -Name $port -ErrorAction SilentlyContinue
            }
        }
    } else {
        Write-Host "Queue '$Name' not found."
    }

    Write-Host ""
    Get-Printer | Select-Object Name, DriverName, PortName | Format-Table -AutoSize | Out-String | Write-Host
}
finally {
    Stop-Transcript | Out-Null
}

exit 0
