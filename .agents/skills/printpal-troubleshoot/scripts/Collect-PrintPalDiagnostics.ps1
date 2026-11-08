#requires -Version 5.1
<#
.SYNOPSIS
    Collects a READ-ONLY PrintPal diagnostic evidence bundle.

.DESCRIPTION
    Writes summary.md into artifacts\diagnostics\<timestamp>\ containing:
    environment, processes and services, listening ports and owning PIDs, /health
    responses, queue state and jobs, queue UUID binding, data directories,
    log tails (with JOB / ERROR / WARN extraction) and recent Windows events.

    The script never starts, stops, installs or modifies anything; it only reads
    process/service/queue state, HTTP /health endpoints and log files.

.PARAMETER RepoRoot
    PrintPal repository root. Defaults to the nearest ancestor of this script that
    contains PrintPal.sln; falls back to the current directory.

.PARAMETER Output
    Output directory. Default <repo>\artifacts\diagnostics\<timestamp>.

.PARAMETER Port
    Agent IPP port. Default 8630.

.PARAMETER TailLines
    Lines of each log to include. Default 60.

.PARAMETER SkipWindowsEvents
    Do not query the Windows event logs.

.EXAMPLE
    powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\.agents\skills\printpal-troubleshoot\scripts\Collect-PrintPalDiagnostics.ps1
#>
[CmdletBinding()]
param(
    [string] $RepoRoot = '',
    [string] $Output = '',
    [int] $Port = 8630,
    [int] $TailLines = 60,
    [switch] $SkipWindowsEvents
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

function Test-TcpPort {
    param([int] $PortNumber, [int] $TimeoutMs = 400)
    $client = New-Object System.Net.Sockets.TcpClient
    try {
        $async = $client.BeginConnect('127.0.0.1', $PortNumber, $null, $null)
        if (-not $async.AsyncWaitHandle.WaitOne($TimeoutMs)) { return $false }
        $client.EndConnect($async)
        return $true
    } catch {
        return $false
    } finally {
        $client.Close()
    }
}

function Get-NetstatListeners {
    $map = @{}
    foreach ($line in (& netstat -ano 2>$null)) {
        if ($line -match '^\s*TCP\s+\S+:(\d+)\s+\S+\s+\S+\s+(\d+)\s*$') {
            $localPort = [int]$Matches[1]
            $processId = [int]$Matches[2]
            if (-not $map.ContainsKey($localPort)) {
                $map[$localPort] = New-Object System.Collections.ArrayList
            }
            if (-not $map[$localPort].Contains($processId)) {
                [void]$map[$localPort].Add($processId)
            }
        }
    }
    return $map
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

$root = Resolve-RepoRoot -Explicit $RepoRoot
if (-not $Output) {
    $base = if ($root) { Join-Path $root 'artifacts\diagnostics' } else { Join-Path (Get-Location).Path 'printpal-diagnostics' }
    $Output = Join-Path $base (Get-Date -Format 'yyyyMMdd-HHmmss')
}
New-Item -ItemType Directory -Force -Path $Output | Out-Null

$report = New-Object System.Collections.ArrayList
function Add-Report {
    param([string] $Text)
    [void]$report.Add([string]$Text)
}
function Add-Section {
    param([string] $Title)
    Add-Report ''
    Add-Report ("## {0}" -f $Title)
    Add-Report ''
}

# --- Environment -----------------------------------------------------------
Add-Report '# PrintPal diagnostics'
Add-Report ''
Add-Report ("- Time      : {0}" -f (Get-Date -Format o))
Add-Report ("- OS        : {0} ({1})" -f [System.Environment]::OSVersion.VersionString, $env:PROCESSOR_ARCHITECTURE)
Add-Report ("- PowerShell: {0}" -f $PSVersionTable.PSVersion.ToString())
Add-Report ("- Repo root : {0}" -f $(if ($root) { $root } else { 'not found' }))
if ($root) {
    try { Add-Report ("- Git commit: {0}" -f ((git -C $root rev-parse --short HEAD) -join '')) } catch { Add-Report '- Git commit: unknown' }
}
Add-Report ("- Output    : {0}" -f $Output)

# --- Processes and services ------------------------------------------------
Add-Section 'Processes and services'
foreach ($processName in @('PrintPal.App', 'PrintPal.Service', 'PrintPal.FakePrinter', 'PrintPal.AgentHost')) {
    $procs = @(Get-Process -Name $processName -ErrorAction SilentlyContinue)
    if ($procs.Count -eq 0) {
        Add-Report ("- {0}: not running" -f $processName)
        continue
    }
    foreach ($proc in $procs) {
        $started = try { $proc.StartTime.ToString('o') } catch { 'n/a' }
        Add-Report ("- {0}: pid {1}, started {2}, responding {3}" -f $processName, $proc.Id, $started, $proc.Responding)
    }
}

$service = Get-Service -Name 'PrintPal.Service' -ErrorAction SilentlyContinue
if ($service) {
    Add-Report ("- PrintPal.Service: {0}, start type {1}" -f $service.Status, $service.StartType)
    $cim = Get-CimInstance Win32_Service -Filter "Name='PrintPal.Service'" -ErrorAction SilentlyContinue
    if ($cim) { Add-Report ("- service path: {0}" -f $cim.PathName) }
} else {
    Add-Report '- PrintPal.Service: not installed'
}

# --- Ports -----------------------------------------------------------------
Add-Section 'Listening ports'
$listeners = Get-NetstatListeners
$httpPorts = @($Port, 8631, 8632, 8633)
$rawPorts = @(9101, 9102, 9103)
foreach ($watchPort in ($httpPorts + $rawPorts)) {
    # Only probe HTTP ports with a TCP connect. Connecting to a FakePrinter RAW
    # port is interpreted as an empty print job, so RAW ports are read from
    # netstat only.
    if ($httpPorts -contains $watchPort) {
        $open = Test-TcpPort -PortNumber $watchPort
    } else {
        $open = $listeners.ContainsKey($watchPort)
    }
    $owners = ''
    if ($listeners.ContainsKey($watchPort)) {
        $names = foreach ($processId in $listeners[$watchPort]) {
            $proc = Get-Process -Id $processId -ErrorAction SilentlyContinue
            if ($proc) { "{0}({1})" -f $proc.ProcessName, $processId } else { "pid $processId" }
        }
        $owners = ($names -join ', ')
    }
    Add-Report ("- {0}: {1}{2}" -f $watchPort, $(if ($open) { 'LISTEN' } else { 'closed' }), $(if ($owners) { " -> $owners" } else { '' }))
}

# --- Health ----------------------------------------------------------------
Add-Section 'Agent health'
foreach ($healthPort in @($Port, 8631, 8632, 8633)) {
    try {
        $response = Invoke-RestMethod -Uri ("http://127.0.0.1:{0}/health" -f $healthPort) -TimeoutSec 3 -ErrorAction Stop
        Add-Report ("- {0}: {1}" -f $healthPort, ($response | ConvertTo-Json -Compress))
    } catch {
        Add-Report ("- {0}: FAILED ({1})" -f $healthPort, $_.Exception.Message)
    }
}

# --- Queue -----------------------------------------------------------------
Add-Section 'Queue and jobs'
$queueName = 'PrintPal Virtual Printer'
$queue = Get-Printer -Name $queueName -ErrorAction SilentlyContinue
if ($queue) {
    Add-Report ("- name: {0}" -f $queue.Name)
    Add-Report ("- driver: {0}" -f $queue.DriverName)
    Add-Report ("- port: {0} (WSD: {1})" -f $queue.PortName, ($queue.PortName -like 'WSD-*'))
    Add-Report ("- status: {0}" -f $queue.PrinterStatus)
    $jobs = @(Get-PrintJob -PrinterName $queueName -ErrorAction SilentlyContinue | Select-Object -First 15)
    if ($jobs.Count -eq 0) {
        Add-Report '- jobs: none'
    } else {
        Add-Report '```text'
        foreach ($job in $jobs) {
            Add-Report ("{0}  {1}  {2}  pages={3}" -f $job.Id, $job.JobStatus, $job.DocumentName, $job.TotalPages)
        }
        Add-Report '```'
    }
} else {
    Add-Report '- NOT INSTALLED'
}

# --- Queue binding ---------------------------------------------------------
Add-Section 'Queue UUID binding'
$bindingScript = if ($root) { Join-Path $root 'scripts\Get-PrintPalQueueBinding.ps1' } else { $null }
if ($bindingScript -and (Test-Path $bindingScript)) {
    $bindingOutput = & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $bindingScript 2>&1
    $bindingExit = $LASTEXITCODE
    Add-Report '```text'
    foreach ($line in $bindingOutput) { Add-Report ([string]$line) }
    Add-Report '```'
    $meaning = switch ($bindingExit) {
        0 { 'MATCH' }
        1 { 'MISMATCH' }
        2 { 'UNAVAILABLE' }
        default { "exit $bindingExit" }
    }
    Add-Report ("- result: {0}" -f $meaning)
} else {
    Add-Report '- binding script not found (repo not located)'
}

# --- Data directories ------------------------------------------------------
Add-Section 'Data directories'
$dataDirs = @()
if ($root) { $dataDirs += (Join-Path $root 'artifacts\p3\data') }
$dataDirs += 'C:\ProgramData\PrintPal'
$dataDirs += (Join-Path $env:LOCALAPPDATA 'PrintPal')
foreach ($dataDir in $dataDirs) {
    Add-Report ("### {0}" -f $dataDir)
    Add-Report ''
    if (-not (Test-Path $dataDir)) {
        Add-Report '- not found'
        continue
    }
    $items = @(Get-ChildItem -LiteralPath $dataDir -Force -ErrorAction SilentlyContinue | Select-Object -First 20)
    Add-Report '```text'
    foreach ($item in $items) {
        $size = if ($item.PSIsContainer) { '<dir>' } else { '{0:N0}' -f $item.Length }
        Add-Report ("{0,-40} {1,12}  {2:o}" -f $item.Name, $size, $item.LastWriteTime)
    }
    if ($items.Count -eq 0) { Add-Report '(empty)' }
    Add-Report '```'
    $db = Join-Path $dataDir 'printpal.db'
    if (Test-Path $db) {
        Add-Report ("- db : {0:N0} bytes, {1:o}" -f (Get-Item -LiteralPath $db).Length, (Get-Item -LiteralPath $db).LastWriteTime)
    }
    if (Test-Path "$db-wal") {
        Add-Report ("- wal: {0:N0} bytes" -f (Get-Item -LiteralPath "$db-wal").Length)
    }
}

# --- Log tails -------------------------------------------------------------
Add-Section 'Log tails'
$logFiles = New-Object System.Collections.ArrayList
if ($root) {
    [void]$logFiles.Add(@{ Label = 'app.log (development data)'; Path = (Join-Path $root 'artifacts\p3\data\logs\app.log') })
    [void]$logFiles.Add(@{ Label = 'service.log'; Path = 'C:\ProgramData\PrintPal\logs\service.log' })
    foreach ($name in @('Fake-A4-Color', 'Fake-A4-Mono', 'Fake-A3')) {
        [void]$logFiles.Add(@{ Label = "$name.out.log"; Path = (Join-Path $root "artifacts\p2\fake-printers\$name.out.log") })
        [void]$logFiles.Add(@{ Label = "$name.err.log"; Path = (Join-Path $root "artifacts\p2\fake-printers\$name.err.log") })
    }
}
[void]$logFiles.Add(@{ Label = 'app.log (user profile)'; Path = (Join-Path $env:LOCALAPPDATA 'PrintPal\logs\app.log') })

foreach ($logFile in $logFiles) {
    Add-Report ("### {0}" -f $logFile.Label)
    Add-Report ''
    Add-Report ("- path: {0}" -f $logFile.Path)
    if (-not (Test-Path $logFile.Path)) {
        Add-Report '- NOT FOUND'
        continue
    }
    $item = Get-Item -LiteralPath $logFile.Path
    Add-Report ("- size: {0:N0} bytes, last write: {1:o}" -f $item.Length, $item.LastWriteTime)
    # PrintPal writes logs as UTF-8 without a BOM; read them explicitly so CJK
    # messages are not decoded as ANSI (mojibake) by Windows PowerShell 5.1.
    $tail = @(Get-Content -LiteralPath $logFile.Path -Tail $TailLines -Encoding UTF8 -ErrorAction SilentlyContinue)
    if ($tail.Count -eq 0) {
        Add-Report '- (empty)'
        continue
    }
    Add-Report '```text'
    foreach ($line in $tail) { Add-Report ([string]$line) }
    Add-Report '```'

    $interesting = @($tail | Where-Object { $_ -match 'JOB |ERROR|WARN|[Ee]xception|failed' } | Select-Object -Last 20)
    if ($interesting.Count -gt 0) {
        Add-Report 'Interesting lines:'
        Add-Report '```text'
        foreach ($line in $interesting) { Add-Report ([string]$line) }
        Add-Report '```'
    }
}

# --- Windows events --------------------------------------------------------
if (-not $SkipWindowsEvents) {
    Add-Section 'Windows events: PrintService/Admin (last 10)'
    try {
        $events = @(Get-WinEvent -LogName 'Microsoft-Windows-PrintService/Admin' -MaxEvents 10 -ErrorAction Stop)
        if ($events.Count -eq 0) {
            Add-Report '- none'
        } else {
            Add-Report '```text'
            foreach ($event in $events) {
                $message = ($event.Message -replace "`r?`n", ' ')
                Add-Report ("{0:o} [{1}] {2}" -f $event.TimeCreated, $event.LevelDisplayName, $message)
            }
            Add-Report '```'
        }
    } catch {
        Add-Report ("- unavailable: {0}" -f $_.Exception.Message)
    }

    Add-Section 'Windows events: Service Control Manager (last 2h)'
    try {
        $filter = @{
            LogName      = 'System'
            ProviderName = 'Service Control Manager'
            StartTime    = (Get-Date).AddHours(-2)
            Level        = @(1, 2, 3)
        }
        $events = @(Get-WinEvent -FilterHashtable $filter -MaxEvents 10 -ErrorAction Stop)
        if ($events.Count -eq 0) {
            Add-Report '- none'
        } else {
            Add-Report '```text'
            foreach ($event in $events) {
                $message = ($event.Message -replace "`r?`n", ' ')
                Add-Report ("{0:o} [{1}] {2}" -f $event.TimeCreated, $event.LevelDisplayName, $message)
            }
            Add-Report '```'
        }
    } catch {
        Add-Report '- none or unavailable'
    }
}

# --- Write -----------------------------------------------------------------
$summaryPath = Join-Path $Output 'summary.md'
$report | Set-Content -Path $summaryPath -Encoding UTF8

Write-Host '=== PrintPal diagnostics collected (READ-ONLY) ==='
Write-Host ("Output : {0}" -f $summaryPath)
Write-Host ''
Write-Host 'Next: read summary.md top-down; no JOB line means the request never reached PrintPal.'
Write-Host 'Nothing was started, stopped or modified by this script.'
exit 0
