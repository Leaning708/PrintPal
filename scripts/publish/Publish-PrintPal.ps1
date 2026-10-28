#requires -Version 5.1
<#
.SYNOPSIS
    Publishes a self-contained PrintPal package (win-x64) for P5/P6.
#>
[CmdletBinding()]
param(
    [string] $Root = '',
    [string] $Configuration = 'Release',
    [string] $Runtime = 'win-x64',
    [string] $Output = '',
    [string] $Version = '',
    [switch] $Zip,
    [switch] $NoFakePrinter,
    [switch] $NoMsi
)

$ErrorActionPreference = 'Stop'

if (-not $Root) {
    $Root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
}

$appProject = Join-Path $Root 'src\PrintPal.App\PrintPal.App.csproj'
$serviceProject = Join-Path $Root 'src\PrintPal.Service\PrintPal.Service.csproj'
$fakeProject = Join-Path $Root 'src\PrintPal.FakePrinter\PrintPal.FakePrinter.csproj'
$msiProject = Join-Path $Root 'packaging\PrintPal.Msi\PrintPal.Msi.wixproj'

if (-not $Version) {
    [xml] $versionProps = Get-Content (Join-Path $Root 'Directory.Build.props') -Raw
    $versionNode = $versionProps.Project.PropertyGroup |
        Where-Object { $_.PrintPalVersion } |
        Select-Object -First 1
    if ($versionNode) {
        $Version = $versionNode.PrintPalVersion.InnerText
    }
}
if (-not $Version) { $Version = '1.0.1' }
if ($Version -notmatch '^\d+\.\d+\.\d+$') {
    throw "Version must be major.minor.build, got '$Version'."
}
$version = $Version

if (-not $Output) {
    $Output = Join-Path $Root "dist\PrintPal-$version-$Runtime"
}

if (Test-Path $Output) {
    if (-not $Output.StartsWith((Join-Path $Root 'dist'), [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to delete an output directory outside dist: $Output"
    }

    Remove-Item $Output -Recurse -Force
}

$appOutput = Join-Path $Output 'app'
$fakeOutput = Join-Path $Output 'fake-printer'
New-Item -ItemType Directory -Force -Path $appOutput | Out-Null

Write-Host "Publishing PrintPal $version ($Runtime) to $Output ..."

$commonArgs = @(
    '-c', $Configuration,
    '-r', $Runtime,
    '--self-contained', 'true',
    '-p:PublishSingleFile=false',
    '-p:PublishReadyToRun=false',
    '-p:DebugType=None',
    '-p:DebugSymbols=false',
    "-p:PrintPalVersion=$version",
    '--nologo'
)

& dotnet publish $appProject @commonArgs -o $appOutput
if ($LASTEXITCODE -ne 0) { throw 'PrintPal.App publish failed.' }

if (-not (Test-Path (Join-Path $appOutput 'PrintPal.App.exe'))) {
    throw "PrintPal.App.exe not found in $appOutput"
}

Write-Host 'Publishing PrintPal.Service into the shared app folder ...'
& dotnet publish $serviceProject @commonArgs -o $appOutput
if ($LASTEXITCODE -ne 0) { throw 'PrintPal.Service publish failed.' }

if (-not (Test-Path (Join-Path $appOutput 'PrintPal.Service.exe'))) {
    throw "PrintPal.Service.exe not found in $appOutput"
}

if (-not $NoFakePrinter) {
    New-Item -ItemType Directory -Force -Path $fakeOutput | Out-Null
    & dotnet publish $fakeProject @commonArgs -o $fakeOutput
    if ($LASTEXITCODE -ne 0) { throw 'PrintPal.FakePrinter publish failed.' }
}

$installerSource = Join-Path $Root 'scripts\install'
$installerOutput = Join-Path $Output 'installer'
if (Test-Path $installerSource) {
    New-Item -ItemType Directory -Force -Path $installerOutput | Out-Null
    Copy-Item (Join-Path $installerSource '*') $installerOutput -Recurse -Force
}

$docsOutput = Join-Path $Output 'docs'
New-Item -ItemType Directory -Force -Path $docsOutput | Out-Null
foreach ($doc in @('README.md', 'docs\architecture.md', 'docs\installer.md', 'docs\p7-service-msi.md', 'docs\runbook.md', 'docs\ui.md', 'docs\test-plan.md', 'docs\acceptance.md', 'docs\CHANGELOG.md', 'docs\manual-test-1.0.1.md', 'docs\STAGE-SUMMARY-2026-10-28.md')) {
    $source = Join-Path $Root $doc
    if (Test-Path $source) {
        Copy-Item $source $docsOutput -Force
    }
}

if (-not $NoMsi) {
    if (-not (Test-Path $msiProject)) {
        throw "MSI project not found: $msiProject"
    }

    Write-Host 'Building MSI with WiX 5 ...'
    & dotnet build $msiProject -c $Configuration -p:PrintPalVersion=$version --nologo
    if ($LASTEXITCODE -ne 0) { throw 'PrintPal MSI build failed.' }

    $msiPath = Join-Path $Output "PrintPal-$version-x64.msi"
    if (-not (Test-Path $msiPath)) {
        throw "MSI not found at $msiPath"
    }
    Write-Host "  MSI     : $msiPath"
}

$appExe = Join-Path $appOutput 'PrintPal.App.exe'
$appInfo = Get-Item $appExe
$files = Get-ChildItem $Output -Recurse -File
$sha256 = (Get-FileHash $appExe -Algorithm SHA256).Hash

$info = [pscustomobject]@{
    product = 'PrintPal'
    version = $version
    runtime = $Runtime
    selfContained = $true
    publishedAt = (Get-Date).ToString('o')
    appExe = 'app\PrintPal.App.exe'
    appExeBytes = $appInfo.Length
    appExeSha256 = $sha256
    fileCount = $files.Count
    totalBytes = ($files | Measure-Object Length -Sum).Sum
}
$info | ConvertTo-Json -Depth 5 | Set-Content -Path (Join-Path $Output 'publish-info.json') -Encoding UTF8
Set-Content -Path (Join-Path $Output 'VERSION.txt') -Value $version -Encoding ASCII

$cmd = @'
@echo off
setlocal
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0installer\Install-PrintPal.ps1" %*
if errorlevel 1 pause
'@
Set-Content -Path (Join-Path $Output 'Install-PrintPal.cmd') -Value $cmd -Encoding ASCII

$cmdUninstall = @'
@echo off
setlocal
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0installer\Uninstall-PrintPal.ps1" %*
if errorlevel 1 pause
'@
Set-Content -Path (Join-Path $Output 'Uninstall-PrintPal.cmd') -Value $cmdUninstall -Encoding ASCII

Write-Host ''
Write-Host '=== Publish ==='
Write-Host "  Version : $version"
Write-Host "  Runtime : $Runtime"
Write-Host "  Output  : $Output"
Write-Host "  App exe : $($appInfo.Length) bytes, SHA-256 $sha256"
Write-Host "  Files   : $($files.Count), $([math]::Round($info.totalBytes / 1MB, 1)) MB"

if ($Zip) {
    $zipPath = "$Output.zip"
    if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
    Write-Host "Zipping to $zipPath ..."
    Compress-Archive -Path (Join-Path $Output '*') -DestinationPath $zipPath -CompressionLevel Optimal
    $zipInfo = Get-Item $zipPath
    Write-Host "  Zip     : $($zipInfo.Length) bytes"
}


