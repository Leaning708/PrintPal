#requires -Version 5.1
<#
.SYNOPSIS
    Archives a development cycle: removes AllowSameVersionUpgrades from
    Package.wxs so the committed MSI is in release/archive mode.

.DESCRIPTION
    Run this before committing the development batch. The version in
    Directory.Build.props is not changed here; the next Start-DevCycle.ps1
    run bumps it by 1.
#>
[CmdletBinding()]
param(
    [string] $Root = ''
)

$ErrorActionPreference = 'Stop'

if (-not $Root) {
    $Root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
}

$propsPath = Join-Path $Root 'Directory.Build.props'
$packagePath = Join-Path $Root 'packaging\PrintPal.Msi\Package.wxs'

foreach ($path in @($propsPath, $packagePath)) {
    if (-not (Test-Path $path)) { throw "File not found: $path" }
}

$props = [System.IO.File]::ReadAllText($propsPath, [System.Text.Encoding]::UTF8)
$package = [System.IO.File]::ReadAllText($packagePath, [System.Text.Encoding]::UTF8)

if ($props -match '<PrintPalVersion[^>]*>(\d+\.\d+\.\d+)</PrintPalVersion>') {
    $current = $Matches[1]
} else {
    $current = '(unknown)'
}

$package = [regex]::Replace(
    $package,
    '\s*AllowSameVersionUpgrades\s*=\s*"(yes|no)"',
    '')

[System.IO.File]::WriteAllText($packagePath, $package, (New-Object System.Text.UTF8Encoding($false)))

Write-Host "Archived development cycle for version $current (AllowSameVersionUpgrades removed)."
