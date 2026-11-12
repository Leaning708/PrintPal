#requires -Version 5.1
<#
.SYNOPSIS
    MSI 卸载兜底清理：停/删服务、删队列、清 HKCU Run 自启。
    由 MSI 的 deferred custom action 以 SYSTEM 运行，失败不阻塞卸载。
#>
$ErrorActionPreference = 'Continue'

$module = Join-Path $PSScriptRoot 'PrintPal.Common.psm1'
if (Test-Path $module) {
    Import-Module $module -Force -DisableNameChecking
}

function Write-UninstallLog([string] $Message) {
    try {
        $dir = Join-Path $env:ProgramData 'PrintPal'
        New-Item -ItemType Directory -Force -Path $dir | Out-Null
        Add-Content -Path (Join-Path $dir 'msi-uninstall.log') `
            -Value "[$(Get-Date -Format o)] $Message" -Encoding UTF8
    } catch {
        # Logging must never fail the uninstall.
    }
}

Write-UninstallLog 'MSI uninstall cleanup started.'

try {
    Stop-PrintPalService -Delete
    Write-UninstallLog 'PrintPal.Service stopped/deleted.'
} catch {
    Write-UninstallLog "Stop/delete service failed: $($_.Exception.Message)"
}

try {
    Remove-PrintPalQueue -Name 'PrintPal Virtual Printer'
    Write-UninstallLog 'Queue removal attempted.'
} catch {
    Write-UninstallLog "Queue removal failed: $($_.Exception.Message)"
}

try {
    Remove-PrintPalRunEntries
    Write-UninstallLog 'HKCU Run entries removed.'
} catch {
    Write-UninstallLog "Run entry cleanup failed: $($_.Exception.Message)"
}

Write-UninstallLog 'MSI uninstall cleanup finished.'
exit 0
