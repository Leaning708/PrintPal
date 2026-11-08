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
    # 服务刚被 MSI 装上，冷启动 + 杀软扫描 200 MB 自包含运行时可能很慢，
    # 这里给足 180 秒；期间每 10 秒记一次服务状态，失败时日志能看出卡在哪。
    $serviceName = 'PrintPal.Service'
    $svc = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
    Write-Log "Service state: $(if ($svc) { $svc.Status } else { 'not installed' })"

    Write-Log "Waiting for PrintPal service at $Url (up to 180s) ..."
    $deadline = (Get-Date).AddSeconds(180)
    $nextStartAttempt = Get-Date
    $nextProgressLog = Get-Date
    while ((Get-Date) -lt $deadline) {
        if (Test-PrintPalHealth -Port 8630) { break }

        $svc = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
        if ($svc -and $svc.Status -ne 'Running' -and (Get-Date) -ge $nextStartAttempt) {
            Write-Log "Service is $($svc.Status); trying Start-Service ..."
            try { Start-Service -Name $serviceName -ErrorAction Stop; Write-Log 'Start-Service requested.' }
            catch { Write-Log "Start-Service failed: $($_.Exception.Message)" }
            $nextStartAttempt = (Get-Date).AddSeconds(20)
        }

        if ((Get-Date) -ge $nextProgressLog) {
            Write-Log "  ... still waiting; service=$(if ($svc) { $svc.Status } else { 'n/a' })"
            $nextProgressLog = (Get-Date).AddSeconds(10)
        }

        Start-Sleep -Seconds 1
    }

    if (-not (Test-PrintPalHealth -Port 8630)) {
        $svc = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
        $state = if ($svc) { $svc.Status } else { 'not installed' }
        throw "PrintPal service did not become healthy on port 8630 within 180s (service state: $state)."
    }

    Write-Log 'Service is healthy on port 8630.'

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

            # CA 以 LocalSystem 运行时（MSI 里 Impersonate="no"），$env:USERPROFILE 指向
            # 系统账户而不是安装用户，不能往那里建“当前用户桌面”快捷方式。
            $currentUser = ([Security.Principal.WindowsIdentity]::GetCurrent()).Name
            if ($currentUser -eq 'NT AUTHORITY\SYSTEM') {
                Write-Log 'Running as SYSTEM; skipping per-user desktop shortcut (public desktop is used).'
            } else {
                $userDesktop = Join-Path $env:USERPROFILE 'Desktop\PrintPal.lnk'
                New-PrintPalShortcut $userDesktop $appExe $InstallDir
            }

            Write-Log "Shortcuts created: $startFolder, $publicDesktop"
        }
    } catch {
        Write-Log "Shortcut warning: $($_.Exception.Message)"
    }
} catch {
    Write-Log "FAILED: $($_.Exception.Message)"
    Write-Log "FAILED (type): $($_.Exception.GetType().FullName)"
    if ($_.InvocationInfo -and $_.InvocationInfo.PositionMessage) {
        Write-Log "FAILED (at): $($_.InvocationInfo.PositionMessage)"
    }
    $svc = Get-Service -Name 'PrintPal.Service' -ErrorAction SilentlyContinue
    Write-Log "FAILED (service state): $(if ($svc) { $svc.Status } else { 'not installed' })"
    exit 1
}

exit 0
