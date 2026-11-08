#requires -RunAsAdministrator
<#
.SYNOPSIS
    Removes the PrintPal queue and its IPP port. Does not remove the IPP driver.
#>
[CmdletBinding()]
param(
    [string] $Name = 'PrintPal Virtual Printer',
    [string] $LogPath = 'C:\Develop\code\AGI\PrintPal\artifacts\p2\uninstall-queue.log'
)

$ErrorActionPreference = 'Continue'
New-Item -ItemType Directory -Force -Path (Split-Path -Parent $LogPath) | Out-Null
Start-Transcript -Path $LogPath -Force | Out-Null

try {
    foreach ($existingName in @($Name, 'PrintPal Virtual Printer')) {
        $queue = Get-Printer -Name $existingName -ErrorAction SilentlyContinue
        if (-not $queue) {
            Write-Host "Queue '$existingName' not found."
            continue
        }

        $port = $queue.PortName
        Write-Host "Removing queue '$existingName' (port '$port') ..."
        Remove-Printer -Name $existingName -ErrorAction Continue
        Start-Sleep -Milliseconds 500

        if ($port -and $port -like 'http*') {
            $used = Get-Printer -ErrorAction SilentlyContinue | Where-Object { $_.PortName -eq $port }
            if (-not $used) {
                Write-Host "Removing IPP port '$port' ..."
                Remove-PrinterPort -Name $port -ErrorAction SilentlyContinue
            }
        }
    }

    Write-Host ''
    Get-Printer | Select-Object Name, DriverName, PortName | Format-Table -AutoSize | Out-String | Write-Host
} finally {
    Stop-Transcript | Out-Null
}

exit 0
