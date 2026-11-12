#requires -Version 5.1
<#
.SYNOPSIS
    Uninstalls PrintPal: stops the app, removes the queue, autostart, shortcuts,
    uninstall entry and program files. User data is kept unless -RemoveData.
#>
[CmdletBinding()]
param(
    [string] $InstallDir = '',
    [string] $QueueName = 'PrintPal Virtual Printer',
    [switch] $RemoveData,
    [switch] $Quiet
)

$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'PrintPal.Common.psm1') -Force -DisableNameChecking

if (-not (Test-PrintPalAdmin)) {
    Write-Host 'Requesting administrator privileges ...'
    $argList = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$PSCommandPath`"")
    foreach ($key in $PSBoundParameters.Keys) {
        $value = $PSBoundParameters[$key]
        if ($value -is [switch]) {
            if ($value.IsPresent) { $argList += "-$key" }
        } elseif ($null -ne $value -and "$value" -ne '') {
            $argList += "-$key"
            $argList += "`"$value`""
        }
    }

    try {
        Start-Process -FilePath 'powershell.exe' -Verb RunAs -ArgumentList $argList | Out-Null
        exit 0
    } catch {
        Write-Error "Elevation failed: $($_.Exception.Message)"
        exit 1
    }
}

if (-not $InstallDir) { $InstallDir = Get-PrintPalDefaultInstallDir }
$InstallDir = Assert-PrintPalInstallDir -InstallDir $InstallDir

function Write-Info([string] $message) {
    if (-not $Quiet) { Write-Host $message }
    Write-PrintPalInstallLog $message
}

Write-Info "PrintPal uninstall started. InstallDir=$InstallDir"

$exitCode = 0

# 1) 如果是 MSI 安装：先走 MSI 自己的卸载（它会停服务、删服务、删队列、删程序文件）。
#    过去这里直接 Remove-Item 目录，但服务还在跑 → 文件被占用，只剩快捷键被删。
try {
    $msiCount = Uninstall-PrintPalMsiProducts -Quiet:$Quiet -RemoveData:$RemoveData
    if ($msiCount -gt 0) {
        Write-Info "Uninstalled $msiCount MSI product(s)."
    } else {
        Write-Info 'No MSI product found; performing portable cleanup.'
    }
} catch {
    Write-Warning "MSI uninstall failed: $($_.Exception.Message)"
    $exitCode = 1
}

# 2) 兜底清理：无论 MSI 与否，都停 App、停并删除服务、删队列。
Write-Info 'Stopping PrintPal.App ...'
Stop-PrintPalApp

Write-Info 'Stopping and deleting PrintPal.Service ...'
Stop-PrintPalService -Delete

Write-Info "Removing queue '$QueueName' ..."
try {
    Remove-PrintPalQueue -Name $QueueName
} catch {
    Write-Warning "Queue removal failed: $($_.Exception.Message)"
    $exitCode = 1
}

Write-Info 'Removing autostart and shortcuts ...'
Set-PrintPalAutostart -InstallDir $InstallDir -Disable
Remove-PrintPalRunEntries
Remove-PrintPalShortcuts
Remove-PrintPalUninstallRegistry

# 3) 删除程序目录（MSI 卸载后通常已不存在；便携安装或 MSI 卸载残留时兜底）。
if (Test-Path $InstallDir) {
    Write-Info "Removing program files from $InstallDir ..."
    if (-not (Remove-PrintPalInstallDirectory -InstallDir $InstallDir)) {
        $exitCode = 1
    }
}

if ($RemoveData) {
    Write-Info 'Removing data directories (ProgramData + LocalAppData) ...'
    Remove-PrintPalDataDirectories
}

if ($exitCode -eq 0) {
    Write-Info 'Uninstall completed.'
} else {
    Write-Info 'Uninstall completed with warnings (see above / install.log).'
}
if (-not $Quiet) {
    Write-Host ''
    Write-Host '=== PrintPal uninstalled ==='
    if (-not $RemoveData) {
        Write-Host "User data kept at: $env:LOCALAPPDATA\PrintPal"
        Write-Host 'Pass -RemoveData to delete it.'
    }
}

exit 0

