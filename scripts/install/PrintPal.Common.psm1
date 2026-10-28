Set-StrictMode -Version 2.0

function Test-PrintPalAdmin {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($identity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Get-PrintPalDefaultInstallDir {
    return (Join-Path $env:ProgramFiles 'PrintPal')
}

function Assert-PrintPalInstallDir {
    param(
        [Parameter(Mandatory = $true)][string] $InstallDir,
        [switch] $RequireApp
    )

    $full = [IO.Path]::GetFullPath($InstallDir).TrimEnd('\')
    if ($full -match '^[A-Za-z]:\\?$') {
        throw "Refusing to operate on a drive root: $full"
    }

    $leaf = Split-Path $full -Leaf
    if ($leaf -notlike '*PrintPal*') {
        throw "Refusing to operate on a directory whose name does not contain 'PrintPal': $full"
    }

    if ($RequireApp) {
        $app = Join-Path $full 'PrintPal.App.exe'
        $manifest = Join-Path $full 'install.json'
        if (-not (Test-Path $app) -and -not (Test-Path $manifest)) {
            throw "Refusing to operate: neither PrintPal.App.exe nor install.json exists in $full"
        }
    }

    return $full
}

function Test-PrintPalHealth {
    param([int] $Port = 8630)

    try {
        $health = Invoke-RestMethod -Uri "http://127.0.0.1:$Port/health" -TimeoutSec 3
        return ($health.status -eq 'ok')
    } catch {
        return $false
    }
}

function Wait-PrintPalHealth {
    param(
        [int] $Port = 8630,
        [int] $TimeoutSeconds = 30
    )

    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        if (Test-PrintPalHealth -Port $Port) { return $true }
        Start-Sleep -Milliseconds 500
    }

    return $false
}

function Stop-PrintPalApp {
    Get-Process PrintPal.App -ErrorAction SilentlyContinue |
        ForEach-Object {
            Stop-Process -Id $_.Id -Force -ErrorAction SilentlyContinue
        }

    Start-Sleep -Milliseconds 800
}

function Start-PrintPalApp {
    param(
        [Parameter(Mandatory = $true)][string] $InstallDir,
        [int] $Port = 8630,
        [int] $TimeoutSeconds = 30
    )

    $exe = Join-Path $InstallDir 'PrintPal.App.exe'
    if (-not (Test-Path $exe)) {
        throw "PrintPal.App.exe not found in $InstallDir"
    }

    Start-Process -FilePath $exe -ArgumentList @('--port', [string]$Port, '--background') `
        -WorkingDirectory $InstallDir | Out-Null

    return (Wait-PrintPalHealth -Port $Port -TimeoutSeconds $TimeoutSeconds)
}

function Ensure-PrintPalIppDriver {
    $driver = Get-PrinterDriver -Name 'Microsoft IPP Class Driver' -ErrorAction SilentlyContinue
    if ($driver) { return }

    Write-Host 'Installing Microsoft IPP Class Driver from the inbox INF ...'
    Add-PrinterDriver -Name 'Microsoft IPP Class Driver' -ErrorAction Stop
}

function Install-PrintPalQueue {
    param(
        [string] $Url = 'http://127.0.0.1:8630/ipp/print',
        [string] $Name = 'PrintPal Virtual Printer'
    )

    Ensure-PrintPalIppDriver

    # Remove the target queue and any Windows-generated "(copy)" queues left by
    # earlier installs. A stale copy can make Add-Printer pick the HTTP port,
    # which loses IPP job attributes (copies/sides/color).
    $existingQueues = Get-Printer -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -eq $Name -or $_.Name -like 'PrintPal Virtual Printer*' }

    foreach ($existing in $existingQueues) {
        Write-Host "Removing existing queue '$($existing.Name)' ..."
        try {
            Remove-Printer -Name $existing.Name -ErrorAction Stop
        } catch {
            Write-Warning "Remove-Printer failed for '$($existing.Name)': $($_.Exception.Message)"
            & rundll32.exe printui.dll,PrintUIEntry /dl /n "$($existing.Name)" /q
        }
        Start-Sleep -Milliseconds 500
    }

    # Drop a stale Internet Port for this URL; the WSD IPP port must be used.
    Get-PrinterPort -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -eq $Url } |
        ForEach-Object {
            try { Remove-PrinterPort -Name $_.Name -ErrorAction Stop }
            catch { Write-Warning "Could not remove old HTTP port: $($_.Exception.Message)" }
        }

    $created = $null
    $hasIppUrl = (Get-Command Add-Printer).Parameters.ContainsKey('IppURL')

    if ($hasIppUrl) {
        try {
            Write-Host 'Adding queue with Add-Printer -IppURL ...'
            $created = Add-Printer -IppURL $Url -ErrorAction Stop
        } catch {
            Write-Warning "Add-Printer -IppURL failed: $($_.Exception.Message)"
        }
    }

    # Add-Printer -IppURL can create a WSD queue but return no object. Poll for it
    # before falling back to the legacy HTTP port (which loses IPP job attributes).
    if (-not $created) {
        for ($i = 0; $i -lt 30 -and -not $created; $i++) {
            Start-Sleep -Milliseconds 500
            $created = Get-Printer -Name 'PrintPal Virtual Printer' -ErrorAction SilentlyContinue
            if (-not $created) {
                $created = Get-Printer -Name $Name -ErrorAction SilentlyContinue
            }
        }
    }

    if (-not $created) {
        try {
            Write-Host 'Trying legacy Add-Printer -Name ... -PortName http://...'
            $created = Add-Printer -Name $Name -DriverName 'Microsoft IPP Class Driver' -PortName $Url -ErrorAction Stop
        } catch {
            Write-Warning "Legacy Add-Printer failed: $($_.Exception.Message)"
        }
    }

    if (-not $created) {
        for ($i = 0; $i -lt 30 -and -not $created; $i++) {
            Start-Sleep -Milliseconds 500
            $created = Get-Printer -Name 'PrintPal Virtual Printer' -ErrorAction SilentlyContinue
            if (-not $created) {
                $created = Get-Printer -Name $Name -ErrorAction SilentlyContinue
            }
        }
    }

    if (-not (Get-Printer -Name $Name -ErrorAction SilentlyContinue)) {
        throw 'Printer queue was not created.'
    }

    # Windows sometimes creates the target on the stale Internet Port (HTTP)
    # and puts the real WSD IPP port on a "(copy)" queue. Prefer/rename the WSD
    # queue; HTTP loses copies/sides/color.
    $queue = Get-Printer -Name $Name -ErrorAction SilentlyContinue
    if ($queue -and $queue.PortName -notlike 'WSD*') {
        Write-Host 'Queue used HTTP; locating/creating a WSD IPP queue ...'

        $wsdQueue = Get-Printer -ErrorAction SilentlyContinue |
            Where-Object { $_.Name -like 'PrintPal Virtual Printer*' -and $_.PortName -like 'WSD*' } |
            Select-Object -First 1

        if (-not $wsdQueue -and $hasIppUrl) {
            try {
                Write-Host 'Adding a second IPP queue to obtain the WSD port ...'
                Add-Printer -IppURL $Url -ErrorAction SilentlyContinue | Out-Null
            } catch {
                Write-Warning "Second Add-Printer -IppURL failed: $($_.Exception.Message)"
            }

            for ($i = 0; $i -lt 20 -and -not $wsdQueue; $i++) {
                Start-Sleep -Milliseconds 500
                $wsdQueue = Get-Printer -ErrorAction SilentlyContinue |
                    Where-Object { $_.Name -like 'PrintPal Virtual Printer*' -and $_.PortName -like 'WSD*' } |
                    Select-Object -First 1
            }
        }

        if (-not $wsdQueue) {
            throw "Queue '$Name' used HTTP port '$($queue.PortName)' and no WSD IPP queue could be created."
        }

        # Remove all HTTP queues for this printer (Windows may leave the target
        # and a "(copy)" WSD queue).
        foreach ($httpQueue in @(Get-Printer -ErrorAction SilentlyContinue |
                     Where-Object { $_.Name -like 'PrintPal Virtual Printer*' -and $_.PortName -notlike 'WSD*' })) {
            Write-Host "Removing HTTP queue '$($httpQueue.Name)' ..."
            try { Remove-Printer -Name $httpQueue.Name -ErrorAction Stop }
            catch {
                & rundll32.exe printui.dll,PrintUIEntry /dl /n "$($httpQueue.Name)" /q
            }
            Start-Sleep -Milliseconds 500
        }

        $wsdQueue = Get-Printer -ErrorAction SilentlyContinue |
            Where-Object { $_.Name -like 'PrintPal Virtual Printer*' -and $_.PortName -like 'WSD*' } |
            Select-Object -First 1

        if ($wsdQueue -and $wsdQueue.Name -ne $Name) {
            if (Get-Printer -Name $Name -ErrorAction SilentlyContinue) {
                & rundll32.exe printui.dll,PrintUIEntry /dl /n "$Name" /q
                Start-Sleep -Milliseconds 500
            }
            Write-Host "Renaming '$($wsdQueue.Name)' -> '$Name' ..."
            Rename-Printer -Name $wsdQueue.Name -NewName $Name -ErrorAction Stop
        }
    }

    Start-Sleep -Milliseconds 500
    $queue = Get-Printer -Name $Name -ErrorAction Stop
    if ($queue.PortName -notlike 'WSD*') {
        throw "Queue '$Name' was created on port '$($queue.PortName)'; expected a WSD IPP port so copies/sides/color are preserved."
    }

    if ($queue.PrinterStatus -ne 'Normal') {
        Write-Warning "Queue '$Name' status is $($queue.PrinterStatus)."
    }

    return $queue
}

function Remove-PrintPalQueue {
    param([string] $Name = 'PrintPal Virtual Printer')

    foreach ($existingName in @($Name, 'PrintPal Virtual Printer')) {
        $queue = Get-Printer -Name $existingName -ErrorAction SilentlyContinue
        if (-not $queue) { continue }

        $port = $queue.PortName
        Write-Host "Removing queue '$existingName' ..."
        Remove-Printer -Name $existingName -ErrorAction Continue
        Start-Sleep -Milliseconds 500

        if ($port) {
            $used = Get-Printer -ErrorAction SilentlyContinue | Where-Object { $_.PortName -eq $port }
            if (-not $used) {
                Write-Host "Removing port '$port' ..."
                Remove-PrinterPort -Name $port -ErrorAction SilentlyContinue
            }
        }
    }
}

function Set-PrintPalAutostart {
    param(
        [Parameter(Mandatory = $true)][string] $InstallDir,
        [switch] $Disable
    )

    $runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
    if (-not (Test-Path $runKey)) {
        New-Item -Path $runKey -Force | Out-Null
    }

    if ($Disable) {
        Remove-ItemProperty -Path $runKey -Name 'PrintPal' -ErrorAction SilentlyContinue
        return
    }

    $exe = Join-Path $InstallDir 'PrintPal.App.exe'
    Set-ItemProperty -Path $runKey -Name 'PrintPal' -Value "`"$exe`" --background"
}

function New-PrintPalShortcuts {
    param(
        [Parameter(Mandatory = $true)][string] $InstallDir,
        [switch] $NoDesktop
    )

    $exe = Join-Path $InstallDir 'PrintPal.App.exe'
    $shell = New-Object -ComObject WScript.Shell

    $startMenu = [Environment]::GetFolderPath('Programs')
    $folder = Join-Path $startMenu 'PrintPal'
    New-Item -ItemType Directory -Force -Path $folder | Out-Null

    $shortcut = $shell.CreateShortcut((Join-Path $folder 'PrintPal.lnk'))
    $shortcut.TargetPath = $exe
    $shortcut.WorkingDirectory = $InstallDir
    $shortcut.IconLocation = "$exe,0"
    $shortcut.Description = 'PrintPal Print Assistant'
    $shortcut.Save()

    if (-not $NoDesktop) {
        $desktop = [Environment]::GetFolderPath('Desktop')
        $desktopShortcut = $shell.CreateShortcut((Join-Path $desktop 'PrintPal.lnk'))
        $desktopShortcut.TargetPath = $exe
        $desktopShortcut.WorkingDirectory = $InstallDir
        $desktopShortcut.IconLocation = "$exe,0"
        $desktopShortcut.Description = 'PrintPal Print Assistant'
        $desktopShortcut.Save()
    }
}

function Remove-PrintPalShortcuts {
    $startMenu = [Environment]::GetFolderPath('Programs')
    $folder = Join-Path $startMenu 'PrintPal'
    Remove-Item (Join-Path $folder 'PrintPal.lnk') -Force -ErrorAction SilentlyContinue
    Remove-Item $folder -Force -ErrorAction SilentlyContinue

    $desktop = [Environment]::GetFolderPath('Desktop')
    Remove-Item (Join-Path $desktop 'PrintPal.lnk') -Force -ErrorAction SilentlyContinue
}

function Write-PrintPalUninstallRegistry {
    param(
        [Parameter(Mandatory = $true)][string] $InstallDir,
        [Parameter(Mandatory = $true)][string] $Version
    )

    $key = 'HKLM:\Software\Microsoft\Windows\CurrentVersion\Uninstall\PrintPal'
    New-Item -Path $key -Force | Out-Null

    $installerDir = Join-Path $InstallDir 'installer'
    $uninstall = "powershell.exe -NoProfile -ExecutionPolicy Bypass -File `"$installerDir\Uninstall-PrintPal.ps1`" -InstallDir `"$InstallDir`""
    $repair = "powershell.exe -NoProfile -ExecutionPolicy Bypass -File `"$installerDir\Repair-PrintPal.ps1`" -InstallDir `"$InstallDir`""

    $values = @{
        DisplayName = 'PrintPal Print Assistant'
        DisplayVersion = $Version
        Publisher = 'PrintPal'
        InstallLocation = $InstallDir
        DisplayIcon = "$InstallDir\PrintPal.App.exe"
        UninstallString = $uninstall
        QuietUninstallString = "$uninstall -Quiet"
        ModifyPath = $repair
        InstallDate = (Get-Date).ToString('yyyyMMdd')
        NoModify = 0
        NoRepair = 0
    }

    foreach ($name in $values.Keys) {
        New-ItemProperty -Path $key -Name $name -Value $values[$name] -PropertyType String -Force | Out-Null
    }
}

function Remove-PrintPalUninstallRegistry {
    Remove-Item 'HKLM:\Software\Microsoft\Windows\CurrentVersion\Uninstall\PrintPal' -Recurse -Force -ErrorAction SilentlyContinue
}

function Write-PrintPalManifest {
    param(
        [Parameter(Mandatory = $true)][string] $InstallDir,
        [Parameter(Mandatory = $true)][string] $Version,
        [string] $QueueName = 'PrintPal Virtual Printer',
        [int] $Port = 8630
    )

    $exe = Join-Path $InstallDir 'PrintPal.App.exe'
    $manifest = [pscustomobject]@{
        product = 'PrintPal'
        version = $Version
        installedAt = (Get-Date).ToString('o')
        installDir = $InstallDir
        appExe = $exe
        appExeSha256 = if (Test-Path $exe) { (Get-FileHash $exe -Algorithm SHA256).Hash } else { $null }
        queueName = $QueueName
        port = $Port
    }

    $manifest | ConvertTo-Json -Depth 5 | Set-Content -Path (Join-Path $InstallDir 'install.json') -Encoding UTF8
}

function Get-PrintPalManifest {
    param([Parameter(Mandatory = $true)][string] $InstallDir)

    $path = Join-Path $InstallDir 'install.json'
    if (-not (Test-Path $path)) { return $null }
    return (Get-Content $path -Raw | ConvertFrom-Json)
}

function Write-PrintPalInstallLog {
    param([string] $Message)

    try {
        $dir = Join-Path $env:ProgramData 'PrintPal'
        New-Item -ItemType Directory -Force -Path $dir | Out-Null
        Add-Content -Path (Join-Path $dir 'install.log') -Value "[$(Get-Date -Format o)] $Message" -Encoding UTF8
    } catch {
        # Best effort.
    }
}

Export-ModuleMember -Function *

