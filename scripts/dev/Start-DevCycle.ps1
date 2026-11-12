#requires -Version 5.1
<#
.SYNOPSIS
    Starts a development cycle: bumps the patch version by 1 and enables
    AllowSameVersionUpgrades in Package.wxs.

.DESCRIPTION
    The repository policy is:
      - committed/archived state has NO AllowSameVersionUpgrades;
      - development state enables AllowSameVersionUpgrades so the same x.y.z
        MSI can be reinstalled/overwritten while debugging;
      - starting a new development cycle bumps the third version number by 1.

    Running this script when the flag is already enabled is a no-op.
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

if ($props -notmatch '<PrintPalVersion[^>]*>(\d+\.\d+\.\d+)</PrintPalVersion>') {
    throw "Cannot find PrintPalVersion in $propsPath"
}
$current = $Matches[1]

if ($package -match 'AllowSameVersionUpgrades\s*=\s*"yes"') {
    Write-Host "Development cycle already active (AllowSameVersionUpgrades=yes), version $current."
    exit 0
}

$parts = $current.Split('.')
$next = "$($parts[0]).$($parts[1]).$([int]$parts[2] + 1)"

$props = [regex]::Replace(
    $props,
    '(<PrintPalVersion[^>]*>)\d+\.\d+\.\d+(</PrintPalVersion>)',
    "`${1}$next`${2}")

if ($package -match 'AllowSameVersionUpgrades\s*=\s*"no"') {
    $package = [regex]::Replace(
        $package,
        'AllowSameVersionUpgrades\s*=\s*"no"',
        'AllowSameVersionUpgrades="yes"')
} elseif ($package -notmatch 'AllowSameVersionUpgrades') {
    $package = $package.Replace(
        '<MajorUpgrade ',
        '<MajorUpgrade AllowSameVersionUpgrades="yes" ')
}

[System.IO.File]::WriteAllText($propsPath, $props, (New-Object System.Text.UTF8Encoding($false)))
[System.IO.File]::WriteAllText($packagePath, $package, (New-Object System.Text.UTF8Encoding($false)))

Write-Host "Started development cycle: $current -> $next (AllowSameVersionUpgrades=yes)."
