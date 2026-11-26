<#
.SYNOPSIS
    Installs or updates the NotOnlyFans background service.

.DESCRIPTION
    Builds NotOnlyFans.Service, copies it to "C:\Program Files\NotOnlyFans\Service" and registers it
    as the Windows service "NotOnlyFans": starts with Windows, runs as Local System (needed for the
    motherboard sensor chip), restarts itself if it ever fails. Then starts it.

    Run it again after changing the service code: it stops the service (and any copy started by hand),
    replaces the files and starts it again.

    Needs administrator rights: if started without them it asks once (UAC) and continues elevated.
    The service also drives the fans and lights chosen in the app; whenever it stops, fans go back to the BIOS.

.PARAMETER From
    Install ready-made service files from this folder (a release download) instead of building them from the source.

.PARAMETER LogFile
    Optional: also write everything this script prints to this file.
#>
param([string]$LogFile, [switch]$IgnoreSmartAppControl, [string]$From)

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
$ServiceName = 'NotOnlyFans'

$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole(
    [Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
    $arguments = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$PSCommandPath`"")
    if ($LogFile) { $arguments += @('-LogFile', "`"$LogFile`"") }
    if ($IgnoreSmartAppControl) { $arguments += '-IgnoreSmartAppControl' }
    if ($From) { $arguments += @('-From', "`"$((Resolve-Path $From).Path)`"") }
    # WaitForExit, not -Wait: -Wait would also wait for build servers that dotnet leaves running.
    $p = Start-Process powershell.exe -Verb RunAs -ArgumentList $arguments -PassThru
    $p.WaitForExit()
    exit $p.ExitCode
}

if ($LogFile) { Start-Transcript -Path $LogFile -Force | Out-Null }
try {
    $repo = Split-Path $PSScriptRoot -Parent
    $target = Join-Path $env:ProgramFiles 'NotOnlyFans\Service'
    $exe = Join-Path $target 'NotOnlyFans.Service.exe'

    # 1. Stop what is running: the service, and a console copy started by hand (it would hold the sensor pipe).
    $service = Get-Service $ServiceName -ErrorAction SilentlyContinue
    if ($service -and $service.Status -ne 'Stopped') {
        Write-Host "Stopping the $ServiceName service..."
        Stop-Service $ServiceName -Force
        $service.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(30))
    }
    Get-Process 'NotOnlyFans.Service' -ErrorAction SilentlyContinue | ForEach-Object {
        Write-Host "Closing NotOnlyFans.Service started by hand (PID $($_.Id))..."
        $_ | Stop-Process -Force
        $_.WaitForExit(10000) | Out-Null
    }

    # 2. Build a release copy into a temporary folder (or take the ready-made one).
    if ($From) {
        $staging = (Resolve-Path $From).Path
        if (-not (Test-Path (Join-Path $staging 'NotOnlyFans.Service.exe'))) { throw "No NotOnlyFans.Service.exe in $staging." }
    }
    else {
        $staging = Join-Path $env:TEMP 'NotOnlyFans-service-publish'
        Remove-Item $staging -Recurse -Force -ErrorAction SilentlyContinue
        Write-Host 'Building NotOnlyFans.Service...'
        & dotnet publish (Join-Path $repo 'NotOnlyFans.Service\NotOnlyFans.Service.csproj') `
            -c Release -r win-x64 --self-contained false -p:Platform=x64 -o $staging --nologo -v:minimal `
            -nodeReuse:false -p:UseSharedCompilation=false
        if ($LASTEXITCODE -ne 0) { throw "Build failed (dotnet publish exit code $LASTEXITCODE)." }
    }

    # 3. Put it in Program Files.
    New-Item -ItemType Directory -Force -Path $target | Out-Null
    Copy-Item (Join-Path $staging '*') $target -Recurse -Force
    if (-not $From) { Remove-Item $staging -Recurse -Force -ErrorAction SilentlyContinue }
    Write-Host "Installed files in $target"

    # 4. Register (first time) or update the service.
    if (-not $service) {
        New-Service -Name $ServiceName -BinaryPathName "`"$exe`"" -DisplayName 'NotOnlyFans' `
            -Description 'Reads temperatures, drives fans and lights for the NotOnlyFans app.' `
            -StartupType Automatic | Out-Null
        Write-Host "Registered the $ServiceName service."
    }
    else {
        & sc.exe config $ServiceName binPath= "`"$exe`"" start= auto | Out-Null
        & sc.exe description $ServiceName 'Reads temperatures, drives fans and lights for the NotOnlyFans app.' | Out-Null
    }
    # If it ever crashes: restart after 5 s, 5 s, then 60 s (counter resets after a day).
    & sc.exe failure $ServiceName reset= 86400 actions= restart/5000/restart/5000/restart/60000 | Out-Null

    # 5. Start.
    Start-Service $ServiceName
    (Get-Service $ServiceName).WaitForStatus('Running', [TimeSpan]::FromSeconds(30))
    Write-Host "The $ServiceName service is running. It starts with Windows from now on."
    exit 0
}
catch {
    Write-Host "FAILED: $_" -ForegroundColor Red
    exit 1
}
finally {
    if ($LogFile) { Stop-Transcript | Out-Null }
}
