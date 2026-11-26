<#
.SYNOPSIS
    Installs or updates NotOnlyFans on this PC: the app (Start menu, tray, Start with Windows) and the
    background service.

.DESCRIPTION
    1. Builds the app as a signed MSIX package (Release, version from today's date and time, so every
       run is an update). The package is signed with a certificate made for this PC (see Signing.ps1,
       kept in your user certificate store); Windows installs only packages it trusts.
    2. Asks once for administrator rights (UAC) to trust that certificate on this PC and to install or
       update the background service (tools\Install-Service.ps1).
    3. Installs the package for you. Your names, order, pins and settings stay as they are; a copy left
       over from development (the app run from the source folder) is replaced and its settings move over.

    Run it again after changing the code: it updates both. Uninstall: Windows Settings › Apps (the app),
    tools\Uninstall-Service.ps1 (the service).

.PARAMETER AppOnly
    Only build and install the app (no UAC when the certificate is already trusted).

.PARAMETER LogFile
    Optional: also write everything this script prints to this file.
#>
param([switch]$AppOnly, [string]$LogFile, [switch]$IgnoreSmartAppControl)

$ErrorActionPreference = 'Stop'

# Smart App Control (Windows Security › App & browser control) blocks newly built programs that are not signed by a
# certificate Windows trusts. With it on, a fresh build would not start: the service would stay stopped (fans with the
# BIOS) and the app would close right after launch. Stop before anything is replaced.
if ((Get-ItemProperty 'HKLM:\SYSTEM\CurrentControlSet\Control\CI\Policy' -ErrorAction SilentlyContinue).VerifiedAndReputablePolicyState -eq 1 -and
    -not $IgnoreSmartAppControl) {
    Write-Host 'Smart App Control is on: Windows would block the new build (it is not signed by a trusted certificate).' -ForegroundColor Yellow
    Write-Host 'Nothing was changed; what is installed keeps running. Turn Smart App Control off in Windows Security,'
    Write-Host 'or sign the build with a trusted certificate and run again with -IgnoreSmartAppControl.'
    exit 2
}
$repo = Split-Path $PSScriptRoot -Parent
. (Join-Path $PSScriptRoot 'Signing.ps1')

if ($LogFile) { Start-Transcript -Path $LogFile -Force | Out-Null }
try {
    # ---------- 1. Certificate and signed package ----------
    $cert = Get-SigningCertificate
    $now = Get-Date
    $version = '1.0.{0}.{1}' -f (($now.Year % 100) * 1000 + $now.DayOfYear), ($now.Hour * 60 + $now.Minute)
    $staging = Join-Path $env:TEMP 'NotOnlyFans-app-package'
    $package = Build-AppPackage -Repo $repo -Version $version -Certificate $cert -OutDir $staging
    $msix = $package.Msix
    $runtime = $package.Runtime

    # ---------- 2. Administrator part (one UAC): trust the certificate, install the service ----------
    $trusted = Get-ChildItem Cert:\LocalMachine\TrustedPeople | Where-Object Thumbprint -eq $cert.Thumbprint
    if (-not $trusted -or -not $AppOnly) {
        $cer = Join-Path $staging 'NotOnlyFans.cer'
        Export-Certificate -Cert $cert -FilePath $cer | Out-Null
        $adminLog = Join-Path $staging 'admin.log'
        $steps = @("Import-Certificate -FilePath '$cer' -CertStoreLocation Cert:\LocalMachine\TrustedPeople | Out-Null")
        if (-not $AppOnly) { $steps += "& '$(Join-Path $PSScriptRoot 'Install-Service.ps1')' -IgnoreSmartAppControl; if (`$LASTEXITCODE) { exit `$LASTEXITCODE }" }
        $command = "`$ErrorActionPreference = 'Stop'; Start-Transcript -Path '$adminLog' -Force | Out-Null; " +
                   "try { $($steps -join '; '); exit 0 } catch { Write-Host `"FAILED: `$_`"; exit 1 } finally { Stop-Transcript | Out-Null }"
        Write-Host 'Asking for administrator rights (trust the certificate, install the service)...'
        # Encoded: Start-Process does not quote arguments, so quotes and spaces in paths would not survive.
        $encoded = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($command))
        $p = Start-Process powershell.exe -Verb RunAs -PassThru -ArgumentList @(
            '-NoProfile', '-ExecutionPolicy', 'Bypass', '-EncodedCommand', $encoded)
        $p.WaitForExit()
        if (Test-Path $adminLog) { Get-Content $adminLog | Where-Object { $_ -notmatch '^\*{5,}|^(Start|End) time|^Username|^RunAs|^Configuration|^Machine|^Host Application|^Process ID|^PS|^BuildVersion|^CLRVersion|^WSManStackVersion|^PSRemotingProtocolVersion|^SerializationVersion|^Windows PowerShell transcript|^Transcript started' } }
        if ($p.ExitCode -ne 0) { throw "The administrator part failed (exit code $($p.ExitCode))." }
    }

    # ---------- 3. The app ----------
    # A copy registered from the source folder (development) has the same identity: move its settings
    # over, remove it, then install the real package (Windows deletes a removed package's data).
    $dataRoot = Join-Path $env:LOCALAPPDATA 'Packages'
    $old = Get-AppxPackage -Name $packageName | Where-Object IsDevelopmentMode
    $backup = $null
    if ($old) {
        $data = Join-Path $dataRoot "$($old.PackageFamilyName)\LocalCache\Local\NotOnlyFans"
        if (Test-Path $data) {
            $backup = Join-Path $env:LOCALAPPDATA 'NotOnlyFans-settings-backup'
            Remove-Item $backup -Recurse -Force -ErrorAction SilentlyContinue
            Copy-Item $data $backup -Recurse
            Write-Host "Settings copied to $backup"
        }
        Get-Process NotOnlyFans -ErrorAction SilentlyContinue | Stop-Process -Force
        Write-Host 'Removing the development copy...'
        Remove-AppxPackage $old.PackageFullName
    }

    Write-Host 'Installing the app...'
    $add = @{ Path = $msix.FullName; ForceApplicationShutdown = $true }
    if ($runtime) { $add.DependencyPath = $runtime.FullName }
    Add-AppxPackage @add
    $installed = Get-AppxPackage -Name $packageName
    if ($backup) {
        $data = Join-Path $dataRoot "$($installed.PackageFamilyName)\LocalCache\Local\NotOnlyFans"
        New-Item -ItemType Directory -Force $data | Out-Null
        Copy-Item (Join-Path $backup '*') $data -Recurse -Force
        Write-Host 'Settings moved to the installed app.'
    }
    Remove-Item $staging -Recurse -Force -ErrorAction SilentlyContinue

    Write-Host "NotOnlyFans $($installed.Version) is installed. Open it from Start; Settings › Start with Windows keeps it in the tray from sign-in."
    exit 0
}
catch {
    Write-Host "FAILED: $_" -ForegroundColor Red
    exit 1
}
finally {
    if ($LogFile) { Stop-Transcript | Out-Null }
}
