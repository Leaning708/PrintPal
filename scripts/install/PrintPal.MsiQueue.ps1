#requires -Version 5.1
param(
    [string] $Url = 'http://127.0.0.1:8630/ipp/print',
    [string] $Name = 'PrintPal Virtual Printer',
    [string] $InstallDir = '',
    [string] $LogPath = ''
)

$ErrorActionPreference = 'Stop'
$modulePath = Join-Path $PSScriptRoot 'PrintPal.Common.psm1'
Import-Module $modulePath -Force -DisableNameChecking

if (-not $LogPath) {
    $LogPath = Join-Path $env:ProgramData 'PrintPal\msi-queue.log'
}
New-Item -ItemType Directory -Force -Path (Split-Path -Parent $LogPath) | Out-Null

function Write-Log([string] $message) {
    Add-Content -Path $LogPath -Value "[$(Get-Date -Format o)] $message" -Encoding UTF8
    Write-Host $message
}

function New-PrintPalShortcut([string] $shortcutPath, [string] $targetPath, [string] $workingDirectory) {
    $folder = Split-Path -Parent $shortcutPath
    if (-not $folder) { return }
    New-Item -ItemType Directory -Force -Path $folder | Out-Null

    $shell = New-Object -ComObject WScript.Shell
    $shortcut = $shell.CreateShortcut($shortcutPath)
    $shortcut.TargetPath = $targetPath
    $shortcut.WorkingDirectory = $workingDirectory
    $shortcut.Description = 'PrintPal'
    $shortcut.Save()
}

try {
    Write-Log "Waiting for PrintPal service at $Url ..."
    $deadline = (Get-Date).AddSeconds(45)
    while ((Get-Date) -lt $deadline) {
        if (Test-PrintPalHealth -Port 8630) { break }
        Start-Sleep -Seconds 1
    }

    if (-not (Test-PrintPalHealth -Port 8630)) {
        throw 'PrintPal service did not become healthy on port 8630.'
    }

    Write-Log "Installing queue '$Name' ..."
    $existingQueue = Get-Printer -Name $Name -ErrorAction SilentlyContinue
    if ($existingQueue -and $existingQueue.PortName -like 'WSD*') {
        $queue = $existingQueue
        Write-Log "Queue already uses WSD port $($queue.PortName); skipping reinstall (repair mode)."
    } else {
        $queue = Install-PrintPalQueue -Url $Url -Name $Name
    }
    Write-Log "Queue installed: $($queue.Name), driver=$($queue.DriverName), port=$($queue.PortName), status=$($queue.PrinterStatus)"

    try {
        if (-not $InstallDir) {
            try {
                $InstallDir = (Get-ItemProperty 'HKLM:\Software\PrintPal' -ErrorAction Stop).InstallDir
            } catch {
                $InstallDir = ''
            }
        }
        if (-not $InstallDir) {
            $InstallDir = Join-Path $env:ProgramFiles 'PrintPal'
        }

        $appExe = Join-Path $InstallDir 'PrintPal.App.exe'
        if (-not (Test-Path $appExe)) {
            Write-Log "Shortcut warning: app not found at $appExe"
        } else {
            $startFolder = Join-Path $env:ProgramData 'Microsoft\Windows\Start Menu\Programs\PrintPal'
            New-PrintPalShortcut (Join-Path $startFolder 'PrintPal.lnk') $appExe $InstallDir

            # Create both the all-users shortcut and the installing user's shortcut.
            # Some desktops/redirected profiles do not merge the Public desktop.
            $publicDesktop = Join-Path $env:PUBLIC 'Desktop\PrintPal.lnk'
            New-PrintPalShortcut $publicDesktop $appExe $InstallDir

            $userDesktop = Join-Path $env:USERPROFILE 'Desktop\PrintPal.lnk'
            New-PrintPalShortcut $userDesktop $appExe $InstallDir

            Write-Log "Shortcuts created: $startFolder, $publicDesktop, $userDesktop"
        }
    } catch {
        Write-Log "Shortcut warning: $($_.Exception.Message)"
    }
} catch {
    Write-Log "FAILED: $($_.Exception.Message)"
    exit 1
}

exit 0
