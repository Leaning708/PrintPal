#requires -Version 5.1
<#
.SYNOPSIS
    Builds PrintPal and runs the unit/integration test suite, then prints a verdict.

.DESCRIPTION
    Normal path (no PrintPal windows/hosts running): builds PrintPal.sln and
    delegates tests to scripts\test\Run-PrintPalTests.ps1, which writes one TRX per
    test project plus an aggregated report under artifacts\.

    Running-process path: PrintPal.App / PrintPal.FakePrinter / PrintPal.AgentHost
    keep their own bin\ DLLs locked, so a solution build fails with MSB3027/MSB3021
    while they run. Since the repository convention is to leave the app running,
    this script then skips the solution build and builds/tests only the test
    projects (they do not reference App/FakePrinter) and reports exit code 2 to
    make "App/Service not compiled" explicit.

    Repository convention (AGENTS.md): do not stop the app, service or FakePrinter
    just because tests finished. If you changed App/Service code, stopping the app
    for the build is the documented exception: restart it with test data right
    after (printpal-dev-env skill).

.PARAMETER RepoRoot
    PrintPal repository root. Defaults to the nearest ancestor of this script that
    contains PrintPal.sln.

.PARAMETER Configuration
    Debug (default) or Release.

.PARAMETER SkipBuild
    Skip the solution build; build and run the test projects only.

.PARAMETER SkipTests
    Build only; skip running the tests.

.PARAMETER IncludeMsi
    Pass -IncludeMsi to the test runner (needs the full path, no running app).

.OUTPUTS
    Exit codes: 0 = full build + tests ok; 1 = build or tests failed;
    2 = tests ok but the solution build was skipped because PrintPal processes
    lock their binaries (App/Service sources were not compiled).

.EXAMPLE
    powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\.agents\skills\printpal-build-test\scripts\Invoke-PrintPalBuildTest.ps1
#>
[CmdletBinding()]
param(
    [string] $RepoRoot = '',
    [ValidateSet('Debug', 'Release')]
    [string] $Configuration = 'Debug',
    [switch] $SkipBuild,
    [switch] $SkipTests,
    [switch] $IncludeMsi
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
    throw 'Cannot locate the PrintPal repository root (no PrintPal.sln found). Pass -RepoRoot <path>.'
}

function Get-LockingProcesses {
    $result = @()
    foreach ($name in @('PrintPal.App', 'PrintPal.FakePrinter', 'PrintPal.AgentHost')) {
        $procs = @(Get-Process -Name $name -ErrorAction SilentlyContinue)
        foreach ($proc in $procs) { $result += $proc }
    }
    return $result
}

function Invoke-TestProjects {
    param([string] $Root, [string] $Config)

    $trxDir = Join-Path $Root 'artifacts\test-results'
    New-Item -ItemType Directory -Force -Path $trxDir | Out-Null
    Get-ChildItem $trxDir -Filter '*.trx' -ErrorAction SilentlyContinue | Remove-Item -Force

    $projects = @(Get-ChildItem (Join-Path $Root 'tests') -Filter '*.csproj' -Recurse -ErrorAction SilentlyContinue |
        Sort-Object FullName)
    $exit = 0
    foreach ($project in $projects) {
        $name = [IO.Path]::GetFileNameWithoutExtension($project.Name)
        Write-Host ("Testing {0} ..." -f $name)
        # Route the native command output to the host so it is not captured as the
        # function's return value (that would pollute $testExit).
        & dotnet test $project.FullName -c $Config --nologo `
            --logger "trx;LogFileName=$name.trx" `
            --results-directory $trxDir 2>&1 | ForEach-Object { Write-Host ([string]$_) }
        if ($LASTEXITCODE -ne 0) { $exit = 1 }
    }
    return $exit
}

function Get-TrxRows {
    param([string] $Root)
    $trxDir = Join-Path $Root 'artifacts\test-results'
    $rows = @()
    foreach ($file in @(Get-ChildItem $trxDir -Filter '*.trx' -ErrorAction SilentlyContinue)) {
        try {
            [xml] $xml = Get-Content -LiteralPath $file.FullName -Raw
            $results = @($xml.TestRun.Results.UnitTestResult)
            $rows += [pscustomobject]@{
                Project = $file.BaseName
                Total   = $results.Count
                Passed  = @($results | Where-Object { $_.outcome -eq 'Passed' }).Count
                Failed  = @($results | Where-Object { $_.outcome -eq 'Failed' }).Count
                Skipped = @($results | Where-Object { $_.outcome -eq 'NotExecuted' -or $_.outcome -eq 'Inconclusive' }).Count
            }
        } catch {
            $rows += [pscustomobject]@{ Project = $file.BaseName; Total = 0; Passed = 0; Failed = 1; Skipped = 0 }
        }
    }
    return $rows
}

function Write-Report {
    param([string] $Root, [object[]] $Rows, [bool] $SolutionBuildSkipped)

    $total = ($Rows | Measure-Object Total -Sum).Sum
    $passed = ($Rows | Measure-Object Passed -Sum).Sum
    $failed = ($Rows | Measure-Object Failed -Sum).Sum
    $skipped = ($Rows | Measure-Object Skipped -Sum).Sum

    $lines = @(
        '# PrintPal Test Report',
        '',
        ('- Time: {0}' -f (Get-Date -Format o)),
        ('- Solution build: {0}' -f $(if ($SolutionBuildSkipped) { 'skipped (PrintPal processes lock bin\)' } else { 'executed' })),
        ('- Result: **{0} passed / {1} failed / {2} skipped / {3} total**' -f $passed, $failed, $skipped, $total),
        ''
    )
    if ($Rows.Count -gt 0) {
        $lines += '| TRX | Total | Passed | Failed | Skipped |'
        $lines += '|---|---:|---:|---:|---:|'
        foreach ($row in $Rows) {
            $lines += ('| {0} | {1} | {2} | {3} | {4} |' -f $row.Project, $row.Total, $row.Passed, $row.Failed, $row.Skipped)
        }
        $lines += ''
    }

    $path = Join-Path $Root 'artifacts\test-report.md'
    $lines | Set-Content -Path $path -Encoding UTF8
    return [pscustomobject]@{
        Total   = [int]$total
        Passed  = [int]$passed
        Failed  = [int]$failed
        Skipped = [int]$skipped
        Path    = $path
    }
}

$root = Resolve-RepoRoot -Explicit $RepoRoot
$solution = Join-Path $root 'PrintPal.sln'
$runner = Join-Path $root 'scripts\test\Run-PrintPalTests.ps1'

Write-Host '=== PrintPal build + test ==='
Write-Host ("Repository    : {0}" -f $root)
Write-Host ("Configuration : {0}" -f $Configuration)
Write-Host ''

$locked = @(Get-LockingProcesses)
$solutionBuildSkipped = $false
$buildExit = 0

# --- Build -----------------------------------------------------------------
if ($SkipBuild) {
    $solutionBuildSkipped = $true
    Write-Host 'Solution build skipped (-SkipBuild).'
} elseif ($locked.Count -gt 0) {
    $solutionBuildSkipped = $true
    Write-Host 'NOTE: these running PrintPal processes lock their own bin\ folders:'
    foreach ($proc in $locked) { Write-Host ("  - {0} pid {1}" -f $proc.ProcessName, $proc.Id) }
    Write-Host 'Skipping the solution build to avoid MSB3027/MSB3021 file-lock errors.'
    Write-Host 'App/Service/FakePrinter sources are NOT compiled by this run.'
    Write-Host 'If you changed them: stop the app (AGENTS.md exception), rebuild, then restart it'
    Write-Host 'with test data and leave it running (printpal-dev-env skill).'
} else {
    Write-Host ("--- dotnet build ({0}) ---" -f $Configuration)
    Push-Location $root
    try {
        & dotnet build $solution -c $Configuration --nologo
        $buildExit = $LASTEXITCODE
    } finally {
        Pop-Location
    }

    if ($buildExit -ne 0) {
        Write-Host ''
        Write-Host ("BUILD FAILED (exit {0}). Fix the build before running tests." -f $buildExit)
        exit 1
    }
}

# --- Tests -----------------------------------------------------------------
$testExit = 0
if (-not $SkipTests) {
    Write-Host ''
    if ($solutionBuildSkipped) {
        Write-Host ("--- tests ({0}, test projects only) ---" -f $Configuration)
        if ($IncludeMsi) {
            Write-Host 'NOTE: -IncludeMsi needs the full runner path (no running app); ignored here.'
        }
        $testExit = Invoke-TestProjects -Root $root -Config $Configuration
        $rows = @(Get-TrxRows -Root $root)
        $summary = Write-Report -Root $root -Rows $rows -SolutionBuildSkipped $true
        Write-Host ''
        Write-Host '=== Test summary ==='
        foreach ($row in $rows) {
            Write-Host ("  {0,-32} total {1,3}  passed {2,3}  failed {3,3}  skipped {4,3}" -f $row.Project, $row.Total, $row.Passed, $row.Failed, $row.Skipped)
        }
        Write-Host ("  Result : {0} passed / {1} failed / {2} skipped / {3} total" -f $summary.Passed, $summary.Failed, $summary.Skipped, $summary.Total)
        Write-Host ("  Report : {0}" -f $summary.Path)
    } else {
        Write-Host ("--- tests ({0}) ---" -f $Configuration)
        if (-not (Test-Path $runner)) { throw "Test runner not found: $runner" }
        $runArgs = @(
            '-NoProfile',
            '-ExecutionPolicy', 'Bypass',
            '-File', $runner,
            '-Root', $root,
            '-Configuration', $Configuration
        )
        if ($IncludeMsi) { $runArgs += '-IncludeMsi' }
        & powershell.exe @runArgs
        $testExit = $LASTEXITCODE
    }
} else {
    Write-Host 'Tests skipped (-SkipTests).'
}

# --- Verdict ---------------------------------------------------------------
Write-Host ''
Write-Host '=== Result ==='
$buildText = if ($solutionBuildSkipped) { 'skipped (running app locks bin\)' } elseif ($buildExit -eq 0) { 'ok' } else { "FAILED (exit $buildExit)" }
$testText = if ($SkipTests) { 'skipped' } elseif ($testExit -eq 0) { 'ok' } else { "FAILED (exit $testExit)" }
Write-Host ("Build  : {0}" -f $buildText)
Write-Host ("Tests  : {0}" -f $testText)

$report = Join-Path $root 'artifacts\test-report.md'
if (Test-Path $report) { Write-Host ("Report : {0}" -f $report) }

Write-Host ''
Write-Host 'Reminder (AGENTS.md): do not close PrintPal.App / PrintPal.Service / FakePrinter after testing.'

if ($testExit -ne 0) { exit 1 }
if ($solutionBuildSkipped -and -not $SkipTests) {
    Write-Host 'NOTE: App/Service were not compiled in this run (exit code 2).'
    exit 2
}
exit 0
