#requires -Version 5.1
<#
.SYNOPSIS
    Stops the PrintPal WPF application started by Start-PrintPalApp.ps1.
    Note: this is a development helper; production exit should use the tray menu.

.DESCRIPTION
    NOTE: 测试/验证结束后不要运行本脚本 —— App 要留在运行状态供用户手工验证，
    除非用户明确要求关闭，或构建/安装升级必须重启（重启后立即带测试数据启动）。
    约定见仓库根目录 AGENTS.md 与 docs\runbook.md 第 0 节。
#>
[CmdletBinding()]
param(
    [string] $Root = '',
    [string] $Data = ''
)

$ErrorActionPreference = 'Continue'

# Resolve in the body: with [CmdletBinding()] and `-File`, $PSScriptRoot can be
# empty while parameter defaults are evaluated.
if (-not $Root) {
    $scriptDirectory = if ($PSScriptRoot) { $PSScriptRoot } elseif ($PSCommandPath) { Split-Path -Parent $PSCommandPath } else { (Get-Location).Path }
    $Root = (Resolve-Path -LiteralPath (Join-Path $scriptDirectory '..')).Path
}

if (-not $Data) { $Data = Join-Path $Root 'artifacts\p3\data' }
$infoFile = Join-Path $Data 'app.json'

if (Test-Path $infoFile) {
    $info = Get-Content $infoFile -Raw | ConvertFrom-Json
    try {
        Stop-Process -Id $info.pid -Force -ErrorAction Stop
        Write-Host "Stopped PrintPal.App (pid $($info.pid))"
    } catch {
        Write-Host "Could not stop pid $($info.pid): $($_.Exception.Message)"
    }

    Remove-Item $infoFile -Force -ErrorAction SilentlyContinue
} else {
    Write-Host "No app info file at $infoFile"
}

Get-Process PrintPal.App -ErrorAction SilentlyContinue |
    ForEach-Object {
        Write-Host "Stopping leftover PrintPal.App pid $($_.Id)"
        Stop-Process -Id $_.Id -Force -ErrorAction SilentlyContinue
    }
