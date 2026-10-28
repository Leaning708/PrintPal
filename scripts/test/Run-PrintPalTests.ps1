#requires -Version 5.1
<#
.SYNOPSIS
    Runs PrintPal unit/integration tests once and writes a single aggregated report.

.DESCRIPTION
    This avoids one elevated PowerShell/UAC prompt per command. Unit and integration
    tests do not need elevation, so the default path never prompts.

    For future installed-MSI regression, use -IncludeMsi. That path relaunches this
    script once with RunAs (one UAC prompt) and then runs the MSI phases in-process.

.PARAMETER IncludeMsi
    Also run MSI install/repair/rollback smoke tests. Requires admin; one UAC prompt.

.PARAMETER Elevated
    Internal flag used by the one-time RunAs relaunch.
#>
[CmdletBinding()]
param(
    [string] $Root = '',
    [ValidateSet('Debug', 'Release')]
    [string] $Configuration = 'Debug',
    [switch] $IncludeMsi,
    [switch] $Elevated,
    [string] $ReportPath = ''
)

$ErrorActionPreference = 'Stop'

if (-not $Root) {
    $Root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
}

$artifacts = Join-Path $Root 'artifacts'
$trxDir = Join-Path $artifacts 'test-results'
$reportBase = if ($ReportPath) { $ReportPath } else { Join-Path $artifacts 'test-report' }
$sln = Join-Path $Root 'PrintPal.sln'

if ($IncludeMsi -and -not $Elevated) {
    $args = @(
        '-NoProfile',
        '-ExecutionPolicy', 'Bypass',
        '-File', $PSCommandPath,
        '-Root', $Root,
        '-Configuration', $Configuration,
        '-IncludeMsi',
        '-Elevated',
        '-ReportPath', $reportBase
    )
    $p = Start-Process -FilePath 'powershell.exe' -Verb RunAs -ArgumentList $args -Wait -PassThru
    exit $p.ExitCode
}

New-Item -ItemType Directory -Force -Path $trxDir | Out-Null
Get-ChildItem $trxDir -Filter '*.trx' -ErrorAction SilentlyContinue | Remove-Item -Force

$started = Get-Date
$testExit = 0

Write-Host "PrintPal test runner"
Write-Host "  Root          : $Root"
Write-Host "  Configuration : $Configuration"
Write-Host "  MSI phase     : $([bool]$IncludeMsi)"
Write-Host ""

Push-Location $Root
try {
    & dotnet test $sln -c $Configuration --nologo `
        --logger "trx;LogFileName=printpal.trx" `
        --results-directory $trxDir
    $testExit = $LASTEXITCODE
} finally {
    Pop-Location
}

$trxFiles = Get-ChildItem $trxDir -Filter '*.trx' -ErrorAction SilentlyContinue
$projects = @()
$allFailures = @()

foreach ($file in $trxFiles) {
    try {
        [xml] $xml = Get-Content $file.FullName -Raw
        $unitResults = @($xml.TestRun.Results.UnitTestResult)
        $definitions = @($xml.TestRun.TestDefinitions.UnitTest)
        $nameMap = @{}
        foreach ($definition in $definitions) {
            $nameMap[$definition.id] = $definition.name
        }

        $passed = @($unitResults | Where-Object { $_.outcome -eq 'Passed' }).Count
        $failed = @($unitResults | Where-Object { $_.outcome -eq 'Failed' }).Count
        $skipped = @($unitResults | Where-Object { $_.outcome -eq 'NotExecuted' -or $_.outcome -eq 'Inconclusive' }).Count

        $projects += [pscustomobject]@{
            trx = $file.Name
            total = $unitResults.Count
            passed = $passed
            failed = $failed
            skipped = $skipped
        }

        foreach ($failure in @($unitResults | Where-Object { $_.outcome -eq 'Failed' })) {
            $name = $nameMap[$failure.testId]
            if (-not $name) { $name = $failure.testName }
            $message = ''
            if ($failure.Output -and $failure.Output.ErrorInfo) {
                $message = [string]$failure.Output.ErrorInfo.Message
            }
            $allFailures += [pscustomobject]@{
                test = $name
                message = $message.Trim()
                trx = $file.Name
            }
        }
    } catch {
        $projects += [pscustomobject]@{
            trx = $file.Name
            total = 0
            passed = 0
            failed = 1
            skipped = 0
            error = $_.Exception.Message
        }
    }
}

$total = ($projects | Measure-Object total -Sum).Sum
$passed = ($projects | Measure-Object passed -Sum).Sum
$failed = ($projects | Measure-Object failed -Sum).Sum
$skipped = ($projects | Measure-Object skipped -Sum).Sum
$finished = Get-Date

$gitCommit = ''
try { $gitCommit = (git -C $Root rev-parse --short HEAD 2>$null) } catch {}

$summary = [pscustomobject]@{
    product = 'PrintPal'
    startedAt = $started.ToString('o')
    finishedAt = $finished.ToString('o')
    durationSeconds = [math]::Round(($finished - $started).TotalSeconds, 1)
    configuration = $Configuration
    gitCommit = $gitCommit
    dotnetTestExitCode = $testExit
    total = [int]$total
    passed = [int]$passed
    failed = [int]$failed
    skipped = [int]$skipped
    projects = $projects
    failures = $allFailures
}

$jsonPath = "$reportBase.json"
$mdPath = "$reportBase.md"
$summary | ConvertTo-Json -Depth 6 | Set-Content $jsonPath -Encoding UTF8

$lines = @(
    '# PrintPal Test Report',
    '',
    "- Time: $($summary.startedAt) → $($summary.finishedAt) ($($summary.durationSeconds)s)",
    "- Configuration: $Configuration",
    "- Git: $gitCommit",
    "- Result: **$passed passed / $failed failed / $skipped skipped / $total total**",
    ''
)

if ($projects.Count -gt 0) {
    $lines += '| TRX | Total | Passed | Failed | Skipped |'
    $lines += '|---|---:|---:|---:|---:|'
    foreach ($project in $projects) {
        $lines += "| $($project.trx) | $($project.total) | $($project.passed) | $($project.failed) | $($project.skipped) |"
    }
    $lines += ''
}

if ($allFailures.Count -gt 0) {
    $lines += '## Failures'
    $lines += ''
    foreach ($failure in $allFailures) {
        $lines += "### $($failure.test)"
        $lines += ''
        $lines += '```text'
        $lines += $failure.message
        $lines += '```'
        $lines += ''
    }
}

$lines | Set-Content $mdPath -Encoding UTF8

Write-Host ''
Write-Host '=== Test Report ==='
Write-Host "  Result : $passed passed / $failed failed / $skipped skipped / $total total"
Write-Host "  JSON   : $jsonPath"
Write-Host "  Markdown: $mdPath"

if ($IncludeMsi) {
    # MSI regression is intentionally a separate, later phase. The runner is the
    # single entry point so there is only one UAC prompt when it is added.
    Write-Host ''
    Write-Host 'MSI phase requested: use scripts/test/Invoke-PrintPalMsiRegression.ps1'
    Write-Host 'The unit report above is still written.'
}

if ($failed -gt 0 -or $testExit -ne 0) { exit 1 }
exit 0
