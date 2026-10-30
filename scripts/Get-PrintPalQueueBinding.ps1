#requires -Version 5.1
<#
.SYNOPSIS
    Checks whether the installed IPP queue is bound to the printer UUID that the
    running PrintPal agent currently advertises.

.DESCRIPTION
    Windows records the IPP printer-uuid in the WSD port when the queue is
    installed. PrintPal generates a printerUuid per data directory, so switching
    --data (or removing/recreating the database) makes the existing queue point
    at a printer identity that no longer exists. In that state every print job
    fails before it reaches PrintPal and app.log contains no JOB line.

    This script compares:
      queue  -> HKLM\SYSTEM\CurrentControlSet\Control\Print\Monitors\WSD Port\Ports\<port>\Printer UUID
      agent  -> printer-uuid from a Get-Printer-Attributes IPP request

    Exit codes:
      0  match
      1  mismatch
      2  could not check (queue/agent/port missing)

.EXAMPLE
    .\scripts\Get-PrintPalQueueBinding.ps1

.EXAMPLE
    .\scripts\Get-PrintPalQueueBinding.ps1 -QueueName 'PrintPal Virtual Printer' -Url 'http://127.0.0.1:8640/ipp/print'
#>
[CmdletBinding()]
param(
    [string] $QueueName = 'PrintPal Virtual Printer',
    [string] $Url = 'http://127.0.0.1:8630/ipp/print'
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Net.Http

function Get-IppPrinterUuid {
    param([string] $IppUri)

    # Minimal IPP/2.0 Get-Printer-Attributes request for printer-uuid.
    $body = New-Object 'System.Collections.Generic.List[byte]'
    $body.AddRange([byte[]](0x02, 0x00, 0x00, 0x0B, 0x00, 0x00, 0x00, 0x01, 0x01))

    function Add-IppAttr([byte] $tag, [string] $name, [string] $value) {
        $n = [Text.Encoding]::UTF8.GetBytes($name)
        $v = [Text.Encoding]::UTF8.GetBytes($value)
        $body.Add($tag)
        $body.Add([byte](($n.Length -shr 8) -band 0xFF))
        $body.Add([byte]($n.Length -band 0xFF))
        $body.AddRange($n)
        $body.Add([byte](($v.Length -shr 8) -band 0xFF))
        $body.Add([byte]($v.Length -band 0xFF))
        $body.AddRange($v)
    }

    Add-IppAttr 0x47 'attributes-charset' 'utf-8'
    Add-IppAttr 0x48 'attributes-natural-language' 'en-us'
    Add-IppAttr 0x45 'printer-uri' $IppUri
    Add-IppAttr 0x42 'requesting-user-name' $env:USERNAME
    Add-IppAttr 0x44 'requested-attributes' 'printer-uuid'
    $body.Add([byte]0x03)

    $client = New-Object System.Net.Http.HttpClient
    try {
        $content = New-Object System.Net.Http.ByteArrayContent (, $body.ToArray())
        $content.Headers.ContentType = [System.Net.Http.Headers.MediaTypeHeaderValue]::Parse('application/ipp')
        $httpUri = $IppUri -replace '^ipp://', 'http://'
        $response = $client.PostAsync($httpUri, $content).GetAwaiter().GetResult()
        $bytes = $response.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult()
    } finally {
        $client.Dispose()
    }

    $match = [regex]::Match([Text.Encoding]::ASCII.GetString($bytes), 'urn:uuid:[0-9a-fA-F-]{36}')
    if ($match.Success) { return $match.Value }
    return $null
}

try {
    $queue = Get-Printer -Name $QueueName -ErrorAction Stop
} catch {
    Write-Host "QUEUE MISSING: '$QueueName' is not installed."
    Write-Host 'Fix: install it with scripts\Install-PrintPalQueue.ps1 (administrator).'
    exit 2
}

Write-Host "Queue      : $($queue.Name)"
Write-Host "Port       : $($queue.PortName)"
Write-Host "Status     : $($queue.PrinterStatus)  Jobs: $($queue.JobCount)"

if ($queue.PortName -notlike 'WSD-*') {
    Write-Host ''
    Write-Host "WARNING: port '$($queue.PortName)' is not a WSD IPP port."
    Write-Host 'HTTP-style ports lose copies/sides/color; reinstall with Add-Printer -IppURL.'
    exit 2
}

$portKey = "HKLM:\SYSTEM\CurrentControlSet\Control\Print\Monitors\WSD Port\Ports\$($queue.PortName)"
$port = Get-ItemProperty -Path $portKey -ErrorAction SilentlyContinue
$queueUuid = if ($port) { $port.'Printer UUID' } else { $null }

if (-not $queueUuid) {
    Write-Host ''
    Write-Host "Could not read 'Printer UUID' from $portKey"
    Write-Host 'The port is probably stale; reinstall the queue.'
    exit 2
}

Write-Host "Queue UUID : $queueUuid"

try {
    $agentUuid = Get-IppPrinterUuid -IppUri ($Url -replace '^http://', 'ipp://')
} catch {
    Write-Host "AGENT UNREACHABLE: $($_.Exception.GetBaseException().Message)"
    Write-Host "Fix: start PrintPal and check $($Url -replace '/ipp/print$', '/health')."
    exit 2
}

if (-not $agentUuid) {
    Write-Host 'Agent did not return printer-uuid.'
    exit 2
}

Write-Host "Agent UUID : $agentUuid"
Write-Host ''

if ($agentUuid -eq ('urn:uuid:' + $queueUuid)) {
    Write-Host 'MATCH: the queue is bound to the running agent.' -ForegroundColor Green
    exit 0
}

Write-Host 'MISMATCH: the queue points at a different printer identity.' -ForegroundColor Red
Write-Host 'Jobs will fail before reaching PrintPal (app.log shows no JOB line).'
Write-Host ''
Write-Host 'Fix one of:'
Write-Host '  1) Start PrintPal with the data directory this queue was installed against, or'
Write-Host '  2) Reinstall the queue as administrator:'
Write-Host '       Restart-Service Spooler -Force'
Write-Host '       Get-Printer -Name ''PrintPal Virtual Printer'' | Remove-Printer'
Write-Host "       Add-Printer -IppURL $Url"
exit 1
