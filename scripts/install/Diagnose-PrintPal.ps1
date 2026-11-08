#requires -Version 5.1
<#
.SYNOPSIS
    Collects a read-only PrintPal diagnostic report (install, queue, health,
    autostart, data, recent logs). Use -Zip to bundle logs.
#>
[CmdletBinding()]
param(
    [string] $InstallDir = '',
    [string] $DataDir = '',
    [string] $QueueName = 'PrintPal Virtual Printer',
    [int] $Port = 8630,
    [switch] $Zip
)

$ErrorActionPreference = 'Continue'
Import-Module (Join-Path $PSScriptRoot 'PrintPal.Common.psm1') -Force -DisableNameChecking

if (-not $InstallDir) { $InstallDir = Get-PrintPalDefaultInstallDir }
if (-not $DataDir) { $DataDir = Join-Path $env:LOCALAPPDATA 'PrintPal' }

$lines = New-Object System.Collections.Generic.List[string]

function Add-Line([string] $text = '') {
    $lines.Add($text)
}

function Add-Command([string] $title, [scriptblock] $command) {
    Add-Line ''
    Add-Line "=== $title ==="
    try {
        $output = & $command 2>&1 | Out-String
        if ($output.Trim().Length -eq 0) { Add-Line '(none)' } else { Add-Line $output.Trim() }
    } catch {
        Add-Line "ERROR: $($_.Exception.Message)"
    }
}

Add-Line 'PrintPal diagnostics'
Add-Line "Generated: $(Get-Date -Format o)"
Add-Line "InstallDir: $InstallDir"
Add-Line "DataDir: $DataDir"
Add-Line "Computer: $env:COMPUTERNAME"
Add-Line "User: $env:USERNAME"
Add-Line "Admin: $(Test-PrintPalAdmin)"

Add-Command 'Operating system' {
    Get-CimInstance Win32_OperatingSystem | Select-Object Caption, Version, BuildNumber, OSArchitecture
}
Add-Command 'Installed manifest' {
    Get-PrintPalManifest -InstallDir $InstallDir
}
Add-Command 'App file' {
    $exe = Join-Path $InstallDir 'PrintPal.App.exe'
    if (-not (Test-Path $exe)) { return 'PrintPal.App.exe NOT FOUND' }
    $item = Get-Item $exe
    [pscustomobject]@{
        Path = $item.FullName
        Bytes = $item.Length
        FileVersion = $item.VersionInfo.FileVersion
        ProductVersion = $item.VersionInfo.ProductVersion
        Sha256 = (Get-FileHash $exe -Algorithm SHA256).Hash
    }
}
Add-Command 'Process' {
    Get-Process PrintPal.App -ErrorAction SilentlyContinue |
        Select-Object Id, ProcessName, StartTime, CPU, WorkingSet64, MainWindowTitle
}
Add-Command 'Agent health' {
    Invoke-RestMethod -Uri "http://127.0.0.1:$Port/health" -TimeoutSec 5 | ConvertTo-Json -Depth 5
}
Add-Command 'IPP driver' {
    Get-PrinterDriver -Name 'Microsoft IPP Class Driver' -ErrorAction SilentlyContinue |
        Select-Object Name, MajorVersion, DataFile
}
Add-Command 'Printer queue' {
    Get-Printer -Name $QueueName -ErrorAction SilentlyContinue |
        Select-Object Name, DriverName, PortName, PrinterStatus, JobCount, Shared, Published
}
Add-Command 'Print jobs' {
    Get-PrintJob -PrinterName $QueueName -ErrorAction SilentlyContinue |
        Select-Object Id, DocumentName, JobStatus, PagesPrinted, SubmittedTime
}
Add-Command 'Autostart' {
    Get-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name 'PrintPal' -ErrorAction SilentlyContinue |
        Select-Object PrintPal
}
Add-Command 'Listening ports' {
    Get-NetTCPConnection -State Listen -ErrorAction SilentlyContinue |
        Where-Object { $_.LocalPort -in @($Port, 8631, 8632, 8633, 9101, 9102, 9103) } |
        Select-Object LocalAddress, LocalPort, OwningProcess
}
Add-Command 'Data directory' {
    if (-not (Test-Path $DataDir)) { return "$DataDir NOT FOUND" }
    Get-ChildItem $DataDir -Force | Select-Object Name, Length, LastWriteTime
}
Add-Command 'Documents' {
    $docs = Join-Path $DataDir 'documents'
    if (-not (Test-Path $docs)) { return 'documents NOT FOUND' }
    $files = Get-ChildItem $docs -File -ErrorAction SilentlyContinue
    [pscustomobject]@{ Count = $files.Count; TotalBytes = ($files | Measure-Object Length -Sum).Sum }
}

$appLog = Join-Path $DataDir 'logs\app.log'
if (Test-Path $appLog) {
    Add-Line ''
    Add-Line '=== app.log (last 50 lines) ==='
    Add-Line ((Get-Content $appLog -Tail 50) -join [Environment]::NewLine)
}

$installLog = Join-Path $env:ProgramData 'PrintPal\install.log'
if (Test-Path $installLog) {
    Add-Line ''
    Add-Line '=== install.log (last 50 lines) ==='
    Add-Line ((Get-Content $installLog -Tail 50) -join [Environment]::NewLine)
}

$reportDirectory = Join-Path $env:ProgramData 'PrintPal'
try {
    New-Item -ItemType Directory -Force -Path $reportDirectory | Out-Null
} catch {
    $reportDirectory = $env:TEMP
}

$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$reportPath = Join-Path $reportDirectory "diagnostics-$stamp.txt"
Set-Content -Path $reportPath -Value ($lines -join [Environment]::NewLine) -Encoding UTF8

$result = $reportPath

if ($Zip) {
    $zipPath = "$reportPath.zip"
    $snapshotDir = Join-Path $env:TEMP "printpal-diagnostics-$stamp"
    New-Item -ItemType Directory -Force -Path $snapshotDir | Out-Null
    $zipItems = @($reportPath)

    foreach ($log in @($appLog, $installLog)) {
        if (-not (Test-Path $log)) { continue }
        $name = Split-Path $log -Leaf
        $snapshot = Join-Path $snapshotDir $name
        try {
            Get-Content $log -Raw -ErrorAction Stop | Set-Content -Path $snapshot -Encoding UTF8
            $zipItems += $snapshot
        } catch {
            Write-Warning "Could not snapshot $log : $($_.Exception.Message)"
        }
    }

    try {
        Compress-Archive -Path $zipItems -DestinationPath $zipPath -Force -ErrorAction Stop
        $result = $zipPath
    } catch {
        Write-Warning "Could not create diagnostics zip: $($_.Exception.Message)"
        $result = $reportPath
    }
}

Write-Host "Diagnostics written to: $result"
exit 0

