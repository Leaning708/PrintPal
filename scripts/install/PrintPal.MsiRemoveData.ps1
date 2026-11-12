#requires -Version 5.1
<#
.SYNOPSIS
    Removes the PrintPal data directory when the user opts in during MSI uninstall.
    Only a directory named PrintPal under ProgramData is allowed.
#>
param(
    [string] $DataDir = ''
)

$ErrorActionPreference = 'Continue'

if ([string]::IsNullOrWhiteSpace($DataDir)) {
    $DataDir = Join-Path $env:ProgramData 'PrintPal'
}

$full = [IO.Path]::GetFullPath($DataDir).TrimEnd('\')
$common = [IO.Path]::GetFullPath($env:ProgramData).TrimEnd('\')

$logDir = Join-Path $env:TEMP 'PrintPal-Uninstall'
New-Item -ItemType Directory -Force -Path $logDir | Out-Null
$log = Join-Path $logDir 'remove-data.log'

function Write-RemoveDataLog([string] $message) {
    try {
        Add-Content -Path $log -Value "[$(Get-Date -Format o)] $message" -Encoding UTF8
    } catch {
        # Logging must never fail the uninstall.
    }
}

try {
    if ((Split-Path $full -Leaf) -ne 'PrintPal') {
        Write-RemoveDataLog "Refusing to delete unexpected directory: $full"
        exit 1
    }

    if (-not $full.StartsWith($common + '\', [StringComparison]::OrdinalIgnoreCase)) {
        Write-RemoveDataLog "Refusing to delete directory outside ProgramData: $full"
        exit 1
    }

    if (-not (Test-Path $full)) {
        Write-RemoveDataLog "Data directory already absent: $full"
        exit 0
    }

    Write-RemoveDataLog "Removing data directory: $full"

    Get-ChildItem $full -Recurse -Force -ErrorAction SilentlyContinue | ForEach-Object {
        try {
            $_.Attributes = [IO.FileAttributes]::Normal
        } catch {
            # Best effort.
        }
    }

    for ($attempt = 0; $attempt -lt 3 -and (Test-Path $full); $attempt++) {
        try {
            Remove-Item $full -Recurse -Force -ErrorAction Stop
        } catch {
            Write-RemoveDataLog "Remove attempt $($attempt + 1) failed: $($_.Exception.Message)"
            Start-Sleep -Milliseconds 800
        }
    }

    # Directory removal can be delayed by file handles or AV scans; wait briefly.
    for ($wait = 0; $wait -lt 10 -and (Test-Path $full); $wait++) {
        Start-Sleep -Milliseconds 300
    }

    if (Test-Path $full) {
        Write-RemoveDataLog "Data directory still present after retries: $full"
        exit 1
    }

    Write-RemoveDataLog 'Data directory removed.'
    exit 0
} catch {
    Write-RemoveDataLog "Unexpected failure: $($_.Exception.Message)"
    exit 1
}
