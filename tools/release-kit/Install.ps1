<#
.SYNOPSIS
    Installs NotOnlyFans from a release download (this folder): the app and its background service.

.DESCRIPTION
    1. Asks once for administrator rights (UAC) to:
         - trust the certificate the app is signed with (NotOnlyFans.cer, put in Local Machine › Trusted People),
         - install the background service "NotOnlyFans" (reads sensors, drives fans and lights; Local System).
    2. Installs the app for you (Start menu, tray).
    Run it again to update. Uninstall: Windows Settings › Apps (the app), then Uninstall-Service.ps1 (the service).

    Right-click this file › Run with PowerShell, or in PowerShell:
        powershell -ExecutionPolicy Bypass -File .\Install.ps1
#>
param([switch]$IgnoreSmartAppControl)

$ErrorActionPreference = 'Stop'
$here = $PSScriptRoot

# Smart App Control blocks programs whose signer Windows does not already know: the service would not start.
if ((Get-ItemProperty 'HKLM:\SYSTEM\CurrentControlSet\Control\CI\Policy' -ErrorAction SilentlyContinue).VerifiedAndReputablePolicyState -eq 1 -and
    -not $IgnoreSmartAppControl) {
    Write-Host 'Smart App Control is on: Windows would block NotOnlyFans (it is signed by its author, not by a company Windows knows).' -ForegroundColor Yellow
    Write-Host 'Nothing was changed. Turn Smart App Control off in Windows Security › App & browser control, then run this again.'
    exit 2
}

try {
    foreach ($file in 'NotOnlyFans.msix', 'NotOnlyFans.cer', 'Service\NotOnlyFans.Service.exe', 'Install-Service.ps1') {
        if (-not (Test-Path (Join-Path $here $file))) { throw "$file is missing. Unpack the whole zip, then run Install.ps1 from it." }
    }

    # ---------- 1. Administrator part (one UAC) ----------
    $cer = Join-Path $here 'NotOnlyFans.cer'
    $adminLog = Join-Path $env:TEMP 'NotOnlyFans-install-admin.log'
    $command = "`$ErrorActionPreference = 'Stop'; Start-Transcript -Path '$adminLog' -Force | Out-Null; try { " +
        "Import-Certificate -FilePath '$cer' -CertStoreLocation Cert:\LocalMachine\TrustedPeople | Out-Null; " +
        "& '$(Join-Path $here 'Install-Service.ps1')' -From '$(Join-Path $here 'Service')' -IgnoreSmartAppControl; if (`$LASTEXITCODE) { exit `$LASTEXITCODE }; " +
        "exit 0 } catch { Write-Host `"FAILED: `$_`"; exit 1 } finally { Stop-Transcript | Out-Null }"
    Write-Host 'Asking for administrator rights (trust the certificate, install the service)...'
    # Encoded: Start-Process does not quote arguments, so quotes and spaces in paths would not survive.
    $encoded = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($command))
    $p = Start-Process powershell.exe -Verb RunAs -PassThru -ArgumentList @(
        '-NoProfile', '-ExecutionPolicy', 'Bypass', '-EncodedCommand', $encoded)
    $p.WaitForExit()
    if (Test-Path $adminLog) {
        Get-Content $adminLog | Where-Object { $_ -match '^(Stopping|Closing|Installed|Registered|The NotOnlyFans|FAILED)' }
    }
    if ($p.ExitCode -ne 0) { throw "The administrator part failed (exit code $($p.ExitCode)). Details: $adminLog" }

    # ---------- 2. The app (for this user) ----------
    Write-Host 'Installing the app...'
    $add = @{ Path = (Join-Path $here 'NotOnlyFans.msix'); ForceApplicationShutdown = $true }
    $dependencies = @(Get-ChildItem (Join-Path $here 'Dependencies') -Filter *.msix -ErrorAction SilentlyContinue)
    if ($dependencies) { $add.DependencyPath = $dependencies.FullName }
    Add-AppxPackage @add

    Write-Host 'NotOnlyFans is installed. Open it from Start.' -ForegroundColor Green

    # LibreHardwareMonitor reaches the CPU and the motherboard's sensor chip through the PawnIO driver.
    if (-not (Get-Service PawnIO -ErrorAction SilentlyContinue)) {
        Write-Host ''
        Write-Host 'The PawnIO driver is not installed: CPU and motherboard sensors (and motherboard fans) will be missing.' -ForegroundColor Yellow
        Write-Host 'Install it from https://pawnio.eu (or: winget install namazso.PawnIO), then restart the NotOnlyFans service or Windows.'
    }
    exit 0
}
catch {
    Write-Host "FAILED: $_" -ForegroundColor Red
    exit 1
}
