<#
.SYNOPSIS
    Removes the NotOnlyFans background service and its files from Program Files.

.DESCRIPTION
    Stops and deletes the Windows service "NotOnlyFans" and removes "C:\Program Files\NotOnlyFans\Service".
    The app's own settings (names, pins) are not touched.
    Needs administrator rights: if started without them it asks once (UAC) and continues elevated.

.PARAMETER LogFile
    Optional: also write everything this script prints to this file.
#>
param([string]$LogFile)

$ErrorActionPreference = 'Stop'
$ServiceName = 'NotOnlyFans'

$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole(
    [Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
    $arguments = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$PSCommandPath`"")
    if ($LogFile) { $arguments += @('-LogFile', "`"$LogFile`"") }
    # WaitForExit, not -Wait: -Wait would also wait for build servers that dotnet leaves running.
    $p = Start-Process powershell.exe -Verb RunAs -ArgumentList $arguments -PassThru
    $p.WaitForExit()
    exit $p.ExitCode
}

if ($LogFile) { Start-Transcript -Path $LogFile -Force | Out-Null }
try {
    $service = Get-Service $ServiceName -ErrorAction SilentlyContinue
    if ($service) {
        if ($service.Status -ne 'Stopped') {
            Stop-Service $ServiceName -Force
            $service.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(30))
        }
        & sc.exe delete $ServiceName | Out-Null
        Write-Host "Removed the $ServiceName service."
    }
    else {
        Write-Host "The $ServiceName service is not installed."
    }

    $target = Join-Path $env:ProgramFiles 'NotOnlyFans\Service'
    if (Test-Path $target) {
        Remove-Item $target -Recurse -Force
        Write-Host "Removed $target"
    }
    exit 0
}
catch {
    Write-Host "FAILED: $_" -ForegroundColor Red
    exit 1
}
finally {
    if ($LogFile) { Stop-Transcript | Out-Null }
}
