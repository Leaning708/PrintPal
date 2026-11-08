#requires -Version 5.1
<#
.SYNOPSIS
    READ-ONLY snapshot of the installed PrintPal deployment.

.DESCRIPTION
    Reports: repository release artifacts, the installed MSI product from the
    registry, program file versions, Windows service state, the IPP queue and its
    UUID binding, running processes, /health, and the data directory.

    This script never starts, stops, installs or modifies anything.

.PARAMETER RepoRoot
    PrintPal repository root (optional). Used only to list dist\ artifacts and the
    development build. Skipped when the repository cannot be located.

.PARAMETER Port
    Agent IPP port. Default 8630.

.EXAMPLE
    powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\.agents\skills\printpal-msi\scripts\Get-PrintPalDeploymentState.ps1
#>
[CmdletBinding()]
param(
    [string] $RepoRoot = '',
    [int] $Port = 8630
)

$ErrorActionPreference = 'Continue'

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
    return $null
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

function Get-PrintPalProducts {
    $paths = @(
        'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*',
        'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\*',
        'HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*'
    )
    foreach ($path in $paths) {
        Get-ItemProperty -Path $path -ErrorAction SilentlyContinue |
            Where-Object { $_.DisplayName -like 'PrintPal*' } |
            Select-Object DisplayName, DisplayVersion, InstallLocation, Publisher
    }
}

$root = Resolve-RepoRoot -Explicit $RepoRoot
$programDir = 'C:\Program Files\PrintPal'
$serviceData = 'C:\ProgramData\PrintPal'
$localData = Join-Path $env:LOCALAPPDATA 'PrintPal'
$queueName = 'PrintPal Virtual Printer'

Write-Host '=== PrintPal deployment state (READ-ONLY) ==='
Write-Host ("Time  : {0}" -f (Get-Date -Format o))
Write-Host ("OS    : {0} ({1})" -f [System.Environment]::OSVersion.VersionString, $env:PROCESSOR_ARCHITECTURE)
Write-Host ("Repo  : {0}" -f $(if ($root) { $root } else { 'not found (repo sections skipped)' }))
Write-Host ''

# --- Release artifacts -----------------------------------------------------
Write-Host '--- Release artifacts (dist) ---'
if ($root) {
    $dist = Join-Path $root 'dist'
    if (Test-Path $dist) {
        $msis = @(Get-ChildItem -Path $dist -Recurse -Filter '*.msi' -ErrorAction SilentlyContinue |
            Sort-Object LastWriteTime -Descending | Select-Object -First 3)
        if ($msis.Count -gt 0) {
            $msis | ForEach-Object {
                Write-Host ("  MSI  {0}  {1:N0} bytes  {2:o}" -f $_.Name, $_.Length, $_.LastWriteTime)
            }
        } else {
            Write-Host '  no MSI under dist\'
        }

        $zips = @(Get-ChildItem -Path $dist -Filter '*.zip' -ErrorAction SilentlyContinue |
            Sort-Object LastWriteTime -Descending | Select-Object -First 3)
        foreach ($zip in $zips) {
            Write-Host ("  ZIP  {0}  {1:N0} bytes  {2:o}" -f $zip.Name, $zip.Length, $zip.LastWriteTime)
        }
    } else {
        Write-Host '  dist\ not found'
    }

    $devExe = Join-Path $root 'src\PrintPal.App\bin\Debug\net10.0-windows\PrintPal.App.exe'
    if (Test-Path $devExe) {
        Write-Host ("  dev  {0} version {1}" -f $devExe, (Get-Item -LiteralPath $devExe).VersionInfo.FileVersion)
    }
} else {
    Write-Host '  skipped'
}
Write-Host ''

# --- Installed product -----------------------------------------------------
Write-Host '--- Installed MSI product (registry) ---'
$products = @(Get-PrintPalProducts)
if ($products.Count -gt 0) {
    foreach ($product in $products) {
        Write-Host ("  {0} {1}" -f $product.DisplayName, $product.DisplayVersion)
        Write-Host ("    location: {0}" -f $product.InstallLocation)
    }
} else {
    Write-Host '  not installed (no PrintPal* product in the uninstall registry)'
}
Write-Host ''

# --- Program files ---------------------------------------------------------
Write-Host '--- Program files ---'
if (Test-Path $programDir) {
    Write-Host ("  dir: {0}" -f $programDir)
    foreach ($name in @('PrintPal.App.exe', 'PrintPal.Service.exe')) {
        $file = Join-Path $programDir $name
        if (Test-Path $file) {
            $item = Get-Item -LiteralPath $file
            Write-Host ("  {0}  version {1}  {2:N0} bytes  {3:o}" -f $name, $item.VersionInfo.FileVersion, $item.Length, $item.LastWriteTime)
        } else {
            Write-Host ("  {0}  MISSING" -f $name)
        }
    }
} else {
    Write-Host ("  not installed at {0}" -f $programDir)
}
Write-Host ''

# --- Service ---------------------------------------------------------------
Write-Host '--- Windows service ---'
$service = Get-Service -Name 'PrintPal.Service' -ErrorAction SilentlyContinue
if ($service) {
    Write-Host ("  PrintPal.Service: {0} (start type: {1})" -f $service.Status, $service.StartType)
    $cim = Get-CimInstance Win32_Service -Filter "Name='PrintPal.Service'" -ErrorAction SilentlyContinue
    if ($cim) { Write-Host ("  path: {0}" -f $cim.PathName) }
} else {
    Write-Host '  PrintPal.Service: not installed'
}
Write-Host ''

# --- Queue -----------------------------------------------------------------
Write-Host '--- IPP queue ---'
$queue = Get-Printer -Name $queueName -ErrorAction SilentlyContinue
if ($queue) {
    Write-Host ("  name   : {0}" -f $queue.Name)
    Write-Host ("  driver : {0}" -f $queue.DriverName)
    Write-Host ("  port   : {0}  (WSD: {1})" -f $queue.PortName, ($queue.PortName -like 'WSD-*'))
    Write-Host ("  status : {0}" -f $queue.PrinterStatus)
    $jobs = @(Get-PrintJob -PrinterName $queueName -ErrorAction SilentlyContinue | Select-Object -First 15)
    if ($jobs.Count -gt 0) {
        Write-Host '  jobs:'
        foreach ($job in $jobs) {
            Write-Host ("    {0}  {1}  {2}  pages={3}" -f $job.Id, $job.JobStatus, $job.DocumentName, $job.TotalPages)
        }
    } else {
        Write-Host '  jobs: none'
    }
} else {
    Write-Host '  NOT INSTALLED'
}
Write-Host ''

# --- Queue binding ---------------------------------------------------------
Write-Host '--- Queue UUID binding ---'
$bindingScript = if ($root) { Join-Path $root 'scripts\Get-PrintPalQueueBinding.ps1' } else { $null }
$bindingResult = 'unknown'
if ($bindingScript -and (Test-Path $bindingScript)) {
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $bindingScript
    $bindingExit = $LASTEXITCODE
    switch ($bindingExit) {
        0 { $bindingResult = 'MATCH' }
        1 { $bindingResult = 'MISMATCH' }
        2 { $bindingResult = 'UNAVAILABLE' }
        default { $bindingResult = "exit $bindingExit" }
    }
    Write-Host ("  result: {0}" -f $bindingResult)
} else {
    Write-Host '  binding script not found (repo not located)'
}
Write-Host ''

# --- Processes and health --------------------------------------------------
Write-Host '--- Processes ---'
foreach ($processName in @('PrintPal.App', 'PrintPal.Service', 'PrintPal.FakePrinter', 'PrintPal.AgentHost')) {
    $procs = @(Get-Process -Name $processName -ErrorAction SilentlyContinue)
    if ($procs.Count -gt 0) {
        foreach ($proc in $procs) {
            $title = if ($proc.MainWindowTitle) { " window='$($proc.MainWindowTitle)'" } else { '' }
            Write-Host ("  {0}: pid {1} started {2:o}{3}" -f $processName, $proc.Id, $proc.StartTime, $title)
        }
    }
}

$health = Test-AgentHealth -HealthPort $Port
Write-Host ("  /health on {0}: {1}" -f $Port, $(if ($health) { 'ok' } else { 'FAILED' }))
Write-Host ''

# --- Data ------------------------------------------------------------------
Write-Host '--- Data directories ---'
foreach ($dataDir in @($serviceData, $localData)) {
    if (Test-Path $dataDir) {
        $db = Join-Path $dataDir 'printpal.db'
        $dbText = if (Test-Path $db) { "{0:N0} bytes, {1:o}" -f (Get-Item -LiteralPath $db).Length, (Get-Item -LiteralPath $db).LastWriteTime } else { 'no printpal.db' }
        Write-Host ("  {0}" -f $dataDir)
        Write-Host ("    db: {0}" -f $dbText)
        $wal = "$db-wal"
        if (Test-Path $wal) { Write-Host ("    wal: {0:N0} bytes" -f (Get-Item -LiteralPath $wal).Length) }
    } else {
        Write-Host ("  {0}: not found" -f $dataDir)
    }
}
Write-Host ''

# --- Hints -----------------------------------------------------------------
Write-Host '--- Hints ---'
$hints = New-Object System.Collections.ArrayList
if (-not $service) {
    [void]$hints.Add('PrintPal.Service is not installed: install the MSI (see printpal-msi skill).')
} elseif ($service.Status -ne 'Running') {
    [void]$hints.Add('PrintPal.Service is installed but not running: start it, then re-check /health.')
}
if (-not $queue) {
    [void]$hints.Add('IPP queue is missing: run scripts\Install-PrintPalQueue.ps1 as admin, or repair the MSI.')
} elseif ($queue.PortName -notlike 'WSD-*') {
    [void]$hints.Add('Queue port is not WSD: copies/sides/color may be lost. Rebuild the queue.')
}
if ($bindingResult -eq 'MISMATCH') {
    [void]$hints.Add('A1: queue UUID != agent printerUuid. Rebuild the queue or point the agent at the original data directory.')
}
if ($health -eq $false) {
    [void]$hints.Add('Agent /health is down: check port 8630 owner (netstat -ano) and C:\ProgramData\PrintPal\logs\service.log.')
}
$app = Get-Process -Name 'PrintPal.App' -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $app) {
    [void]$hints.Add('App is not running: start it (printpal-dev-env skill) and leave it running for manual verification.')
}
if ($hints.Count -eq 0) {
    Write-Host '  no obvious problems detected'
} else {
    foreach ($hint in $hints) { Write-Host ("  - {0}" -f $hint) }
}

Write-Host ''
Write-Host 'Nothing was started, stopped or modified by this script.'
exit 0
