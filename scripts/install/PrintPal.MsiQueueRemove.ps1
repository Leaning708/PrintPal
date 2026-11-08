#requires -Version 5.1
param(
    [string] $Name = 'PrintPal Virtual Printer'
)

$ErrorActionPreference = 'Continue'
Import-Module (Join-Path $PSScriptRoot 'PrintPal.Common.psm1') -Force -DisableNameChecking

try {
    Remove-PrintPalQueue -Name $Name

    $startFolder = Join-Path $env:ProgramData 'Microsoft\Windows\Start Menu\Programs\PrintPal'
    Remove-Item (Join-Path $startFolder 'PrintPal.lnk') -Force -ErrorAction SilentlyContinue
    Remove-Item $startFolder -Force -ErrorAction SilentlyContinue

    $publicDesktop = Join-Path $env:PUBLIC 'Desktop\PrintPal.lnk'
    Remove-Item $publicDesktop -Force -ErrorAction SilentlyContinue

    $userDesktop = Join-Path $env:USERPROFILE 'Desktop\PrintPal.lnk'
    Remove-Item $userDesktop -Force -ErrorAction SilentlyContinue

    exit 0
} catch {
    Write-Host "Queue removal warning: $($_.Exception.Message)"
    exit 0
}
