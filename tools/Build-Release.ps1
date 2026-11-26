<#
.SYNOPSIS
    Builds a release of NotOnlyFans for GitHub: one zip that installs on another PC without the .NET SDK.

.DESCRIPTION
    Makes artifacts\NotOnlyFans-<version>-x64.zip with:
      NotOnlyFans.msix            the app, signed (see Signing.ps1), .NET included
      Dependencies\               the Windows App SDK runtime the app needs
      NotOnlyFans.cer             the public part of the signing certificate (Windows must trust it to install)
      Service\                    the background service, .NET included
      Install.ps1                 installs all of it (tools\release-kit\Install.ps1)
      Install-Service.ps1, Uninstall-Service.ps1
    Nothing is installed on this PC.

.PARAMETER Version
    Three numbers, e.g. 0.1.0 (the package version gets a fourth, 0).

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File tools\Build-Release.ps1 -Version 0.1.0
#>
param([Parameter(Mandatory)][ValidatePattern('^\d+\.\d+\.\d+$')][string]$Version)

$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
. (Join-Path $PSScriptRoot 'Signing.ps1')

$name = "NotOnlyFans-$Version-x64"
$artifacts = Join-Path $repo 'artifacts'
$out = Join-Path $artifacts $name
$work = Join-Path $env:TEMP 'NotOnlyFans-release'
Remove-Item $out, $work -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force $out, (Join-Path $out 'Dependencies') | Out-Null

try {
    # The app: signed MSIX with .NET inside, plus the Windows App SDK runtime package.
    $cert = Get-SigningCertificate
    $package = Build-AppPackage -Repo $repo -Version "$Version.0" -Certificate $cert -OutDir (Join-Path $work 'app') -SelfContained
    Copy-Item $package.Msix.FullName (Join-Path $out 'NotOnlyFans.msix')
    if ($package.Runtime) { Copy-Item $package.Runtime.FullName (Join-Path $out 'Dependencies') }
    Export-Certificate -Cert $cert -FilePath (Join-Path $out 'NotOnlyFans.cer') | Out-Null

    # The service: self-contained, so the PC needs no .NET runtime.
    Write-Host 'Building NotOnlyFans.Service...'
    & dotnet publish (Join-Path $repo 'NotOnlyFans.Service\NotOnlyFans.Service.csproj') -c Release -r win-x64 `
        --self-contained true -p:Platform=x64 -o (Join-Path $out 'Service') --nologo -v:minimal `
        -nodeReuse:false -p:UseSharedCompilation=false
    if ($LASTEXITCODE -ne 0) { throw "Build failed (dotnet publish exit code $LASTEXITCODE)." }
    Get-ChildItem (Join-Path $out 'Service') -Filter *.pdb | Remove-Item

    # Scripts.
    Copy-Item (Join-Path $PSScriptRoot 'release-kit\Install.ps1') $out
    Copy-Item (Join-Path $PSScriptRoot 'Install-Service.ps1'), (Join-Path $PSScriptRoot 'Uninstall-Service.ps1') $out
    Copy-Item (Join-Path $repo 'LICENSE') $out -ErrorAction SilentlyContinue

    $zip = Join-Path $artifacts "$name.zip"
    Remove-Item $zip -Force -ErrorAction SilentlyContinue
    Compress-Archive -Path (Join-Path $out '*') -DestinationPath $zip
    $hash = (Get-FileHash $zip -Algorithm SHA256).Hash
    Write-Host ''
    Write-Host "Done: $zip ($([math]::Round((Get-Item $zip).Length / 1MB)) MB)"
    Write-Host "SHA-256: $hash"
}
catch {
    Write-Host "FAILED: $_" -ForegroundColor Red
    exit 1
}
finally {
    Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
}
