#requires -Version 5.1
<#
.SYNOPSIS
    Idempotently starts the PrintPal local test environment and reports its state.

.DESCRIPTION
    Brings up the three FakePrinter instances and the WPF app when they are not
    already running, waits for the agent health endpoint, and prints a summary.

    READ-ONLY with respect to existing processes: this script never stops, kills or
    closes anything. Repository convention (AGENTS.md section 1) is to leave the app
    running after testing so the user can verify manually.

.PARAMETER RepoRoot
    PrintPal repository root. Defaults to the nearest ancestor of this script that
    contains PrintPal.sln.

.PARAMETER Port
    Agent IPP port. Default 8630.

.PARAMETER NoSeedDemo
    Do not pass -SeedDemo to Start-PrintPalApp.ps1.

.PARAMETER SkipFakePrinters
    Do not start the FakePrinter instances.

.PARAMETER SkipApp
    Do not start the WPF app.

.PARAMETER WaitSeconds
    How long to wait for /health after starting things. Default 30.

.EXAMPLE
    powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\.agents\skills\printpal-dev-env\scripts\Start-PrintPalTestEnv.ps1
#>
[CmdletBinding()]
param(
    [string] $RepoRoot = '',
    [int] $Port = 8630,
    [switch] $NoSeedDemo,
    [switch] $SkipFakePrinters,
    [switch] $SkipApp,
    [int] $WaitSeconds = 30
)

$ErrorActionPreference = 'Stop'

function Find-RepoRoot {
    param([string] $Start)
    if (-not $Start) { return $null }
    $dir = $Start
    while ($dir) {
        if (Test-Path (Join-Path $dir 'PrintPal.sln')) { return (Resolve-Path -LiteralPath $dir).Path }
        $parent = Split-Path -Parent $dir
        if (-not $parent -or $parent -eq $dir) { return $null }
        $dir = $parent
    }
    return $null
}

function Resolve-RepoRoot {
    param([string] $Explicit)
    foreach ($candidate in @($Explicit, $PSScriptRoot, (Get-Location).Path)) {
        if (-not $candidate) { continue }
        $found = Find-RepoRoot -Start $candidate
        if ($found) { return $found }
    }
    throw 'Cannot locate the PrintPal repository root (no PrintPal.sln found). Pass -RepoRoot <path>.'
}

function Test-AgentHealth {
    param([int] $HealthPort)
    try {
        $response = Invoke-RestMethod -Uri ("http://127.0.0.1:{0}/health" -f $HealthPort) -TimeoutSec 3
        return ([string]$response.status -eq 'ok')
    } catch {
        return $false
    }
}

function Invoke-RepoScriptDetached {
    param([string] $Path, [string[]] $ScriptArgs = @())

    # 用 Start-Process（不重定向输出）启动仓库脚本：走 ShellExecute，子进程不会继承本脚本的
    # stdout 管道。否则 FakePrinter/App 这些孙进程会继承管道句柄，管道宿主（AI 代理、CI）
    # 一直等不到 EOF 就会“卡住”。子脚本输出不采集，启动结果用 /health 轮询确认。
    $argumentList = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$Path`"") + $ScriptArgs
    Start-Process -FilePath 'powershell.exe' -ArgumentList $argumentList -WindowStyle Hidden | Out-Null
}

$root = Resolve-RepoRoot -Explicit $RepoRoot
$failures = New-Object System.Collections.ArrayList

Write-Host '=== PrintPal test environment ==='
Write-Host ("Repository : {0}" -f $root)
Write-Host ("Port       : {0}" -f $Port)
Write-Host ''

# --- 1) FakePrinters (forwarding targets) ---------------------------------
$fakePorts = @(8631, 8632, 8633)
if (-not $SkipFakePrinters) {
    $missing = @($fakePorts | Where-Object { -not (Test-AgentHealth -HealthPort $_) })
    if ($missing.Count -gt 0) {
        Write-Host ("Starting FakePrinter (not healthy: {0}) ..." -f ($missing -join ', '))
        $script = Join-Path $root 'scripts\Start-FakePrinters.ps1'
        if (Test-Path $script) {
            Invoke-RepoScriptDetached -Path $script -ScriptArgs @('-Root', $root) -TimeoutSeconds 60

            # 等三台模拟打印机就绪（子脚本退出后进程仍在后台运行）。
            $fakeDeadline = (Get-Date).AddSeconds([Math]::Max(5, $WaitSeconds))
            while ((Get-Date) -lt $fakeDeadline) {
                $stillMissing = @($fakePorts | Where-Object { -not (Test-AgentHealth -HealthPort $_) })
                if ($stillMissing.Count -eq 0) { break }
                Start-Sleep -Milliseconds 500
            }

            $stillMissing = @($fakePorts | Where-Object { -not (Test-AgentHealth -HealthPort $_) })
            if ($stillMissing.Count -gt 0) {
                [void]$failures.Add("FakePrinter not ready: " + ($stillMissing -join ', '))
            }
        } else {
            [void]$failures.Add("Missing script: $script")
        }
    } else {
        Write-Host 'FakePrinter : all three ports already healthy, leaving them running.'
    }
} else {
    Write-Host 'FakePrinter : skipped (-SkipFakePrinters).'
}

# --- 2) WPF app ------------------------------------------------------------
$service = Get-Service -Name 'PrintPal.Service' -ErrorAction SilentlyContinue
$app = Get-Process -Name 'PrintPal.App' -ErrorAction SilentlyContinue | Select-Object -First 1

if (-not $SkipApp) {
    if (-not $app) {
        Write-Host 'Starting PrintPal.App ...'
        $script = Join-Path $root 'scripts\Start-PrintPalApp.ps1'
        if (Test-Path $script) {
            $startArgs = @('-Root', $root, '-Port', [string]$Port)
            if (-not $NoSeedDemo) { $startArgs += '-SeedDemo' }
            Invoke-RepoScriptDetached -Path $script -ScriptArgs $startArgs

            # 服务模式下 /health 由服务提供，不能代表 UI 已起来；这里等 App 进程和窗口出现。
            $appDeadline = (Get-Date).AddSeconds([Math]::Max(5, $WaitSeconds))
            while ((Get-Date) -lt $appDeadline) {
                $app = Get-Process -Name 'PrintPal.App' -ErrorAction SilentlyContinue | Select-Object -First 1
                if ($app) { break }
                Start-Sleep -Milliseconds 500
            }

            if ($app) {
                $titleDeadline = (Get-Date).AddSeconds(10)
                while ((Get-Date) -lt $titleDeadline -and -not $app.MainWindowTitle) {
                    Start-Sleep -Milliseconds 500
                    $app.Refresh()
                }
            } else {
                [void]$failures.Add('PrintPal.App did not start')
            }
        } else {
            [void]$failures.Add("Missing script: $script")
        }
    } else {
        Write-Host ("PrintPal.App already running (pid {0}); leaving it untouched." -f $app.Id)
    }
} else {
    Write-Host 'App         : skipped (-SkipApp).'
}

# --- 3) Wait for health ----------------------------------------------------
$healthOk = $false
$deadline = (Get-Date).AddSeconds([Math]::Max(3, $WaitSeconds))
while ((Get-Date) -lt $deadline) {
    if (Test-AgentHealth -HealthPort $Port) { $healthOk = $true; break }
    Start-Sleep -Milliseconds 500
}
if (-not $healthOk) { $healthOk = Test-AgentHealth -HealthPort $Port }

# --- 4) Summary ------------------------------------------------------------
$app = Get-Process -Name 'PrintPal.App' -ErrorAction SilentlyContinue | Select-Object -First 1
$serviceRunning = $service -and $service.Status -eq 'Running'

Write-Host ''
Write-Host '=== Summary ==='
if ($healthOk -and $serviceRunning) {
    Write-Host 'Mode        : service mode (PrintPal.Service owns the IPP agent)'
} elseif ($healthOk) {
    Write-Host 'Mode        : standalone (the app hosts the agent)'
} else {
    Write-Host 'Mode        : unknown (agent health is down)'
}

$appText = if ($app) { "pid $($app.Id), window '$($app.MainWindowTitle)'" } else { 'NOT RUNNING' }
$serviceText = if ($service) { $service.Status } else { 'not installed' }
$healthText = if ($healthOk) { "ok (http://127.0.0.1:$Port/health)" } else { 'FAILED' }
Write-Host ("App         : {0}" -f $appText)
Write-Host ("Service     : {0}" -f $serviceText)
Write-Host ("Health      : {0}" -f $healthText)

foreach ($fakePort in $fakePorts) {
    $state = if (Test-AgentHealth -HealthPort $fakePort) { 'ok' } else { 'down' }
    Write-Host ("FakePrinter : {0} {1}" -f $fakePort, $state)
}

$devData = Join-Path $root 'artifacts\p3\data'
$serviceData = 'C:\ProgramData\PrintPal'
$dataDir = if ($serviceRunning -and $healthOk) { $serviceData } else { $devData }
Write-Host ("Data        : {0}" -f $dataDir)

$db = Join-Path $dataDir 'printpal.db'
if (Test-Path $db) {
    Write-Host ("Database    : {0} ({1:N0} bytes)" -f $db, (Get-Item -LiteralPath $db).Length)
} else {
    Write-Host ("Database    : not found at {0}" -f $db)
}

$queue = Get-Printer -Name 'PrintPal Virtual Printer' -ErrorAction SilentlyContinue
if ($queue) {
    Write-Host ("Queue       : {0}, port {1}" -f $queue.PrinterStatus, $queue.PortName)
} else {
    Write-Host 'Queue       : NOT INSTALLED'
}

Write-Host ''
Write-Host 'REMINDER: repository convention (AGENTS.md) - do NOT close the app after testing.'
Write-Host '          Leave app, service and FakePrinters running for the user to verify.'

if ($failures.Count -gt 0) {
    Write-Host ''
    Write-Host 'Failures:'
    foreach ($failure in $failures) { Write-Host ("  - {0}" -f $failure) }
}

if ($healthOk) { exit 0 }
exit 1
