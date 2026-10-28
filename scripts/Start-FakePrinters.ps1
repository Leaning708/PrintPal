#requires -Version 5.1
<#
.SYNOPSIS
    Starts three PrintPal FakePrinter instances for the P2 demo.
#>
[CmdletBinding()]
param(
    [string] $Root = (Split-Path -Parent $PSScriptRoot),
    [string] $Output = '',
    [string] $Exe = ''
)

$ErrorActionPreference = 'Stop'

if (-not $Output) { $Output = Join-Path $Root 'artifacts\p2\fake-printers' }
if (-not $Exe) { $Exe = Join-Path $Root 'src\PrintPal.FakePrinter\bin\Debug\net10.0\PrintPal.FakePrinter.exe' }

New-Item -ItemType Directory -Force -Path $Output | Out-Null
$received = Join-Path $Output 'received'
New-Item -ItemType Directory -Force -Path $received | Out-Null

if (-not (Test-Path $Exe)) {
    Write-Host 'Building PrintPal.FakePrinter ...'
    & dotnet build (Join-Path $Root 'src\PrintPal.FakePrinter\PrintPal.FakePrinter.csproj') -c Debug --nologo | Out-Null
}

if (-not (Test-Path $Exe)) { throw "FakePrinter executable not found: $Exe" }

$instances = @(
    @{ Name = 'Fake-A4-Color'; Ipp = 8631; Raw = 9101; Args = @('--media', 'iso_a4_210x297mm') },
    @{ Name = 'Fake-A4-Mono';  Ipp = 8632; Raw = 9102; Args = @('--media', 'iso_a4_210x297mm', '--color', 'monochrome') },
    @{ Name = 'Fake-A3';       Ipp = 8633; Raw = 9103; Args = @('--media', 'iso_a3_297x420mm,iso_a4_210x297mm') }
)

$pids = @()

foreach ($instance in $instances) {
    $outLog = Join-Path $Output ($instance.Name + '.out.log')
    $errLog = Join-Path $Output ($instance.Name + '.err.log')

    $arguments = @(
        '--name', $instance.Name,
        '--ipp-port', [string]$instance.Ipp,
        '--raw-port', [string]$instance.Raw,
        '--output', "`"$received`"",
        '--json'
    ) + $instance.Args

    $process = Start-Process -FilePath $Exe -ArgumentList $arguments -WindowStyle Hidden `
        -RedirectStandardOutput $outLog -RedirectStandardError $errLog -PassThru

    $pids += [pscustomobject]@{
        name = $instance.Name
        pid = $process.Id
        ippPort = $instance.Ipp
        rawPort = $instance.Raw
    }

    Write-Host "Started $($instance.Name): pid=$($process.Id) IPP=$($instance.Ipp) RAW=$($instance.Raw)"
}

$pids | ConvertTo-Json -Depth 3 | Set-Content -Path (Join-Path $Output 'pids.json') -Encoding UTF8

Start-Sleep -Seconds 2

Write-Host ''
Write-Host '=== Health ==='
foreach ($instance in $instances) {
    try {
        $health = Invoke-RestMethod -Uri "http://127.0.0.1:$($instance.Ipp)/health" -TimeoutSec 5
        Write-Host ("  {0,-14} ok  {1}" -f $instance.Name, $health.ippUri)
    } catch {
        Write-Host ("  {0,-14} FAILED: {1}" -f $instance.Name, $_.Exception.Message)
    }
}

Write-Host ''
Write-Host "Fake printers started. Output: $received"
Write-Host "Stop with: .\Stop-FakePrinters.ps1 -Root '$Root'"
