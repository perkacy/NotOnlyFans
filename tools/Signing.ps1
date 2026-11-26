<#
.SYNOPSIS
    Shared by Install.ps1 and Build-Release.ps1: the certificate NotOnlyFans packages are signed with.

.DESCRIPTION
    Windows installs an MSIX package only if it is signed and the signer is trusted. NotOnlyFans is signed with
    a self-made code-signing certificate whose subject must match Publisher in NotOnlyFans\Package.appxmanifest.
    It is made once and kept in your user certificate store (with its private key, which never leaves this PC);
    only its public part (.cer) is exported for the PCs that install the package.
#>

$script:Publisher = 'CN=Kacper'               # must match Publisher in NotOnlyFans\Package.appxmanifest
$script:PackageName = '3b77e012-8ffd-4b74-84be-7e8a4e5f8b17'

function Get-SigningCertificate {
    $codeSigning = '1.3.6.1.5.5.7.3.3'
    $cert = Get-ChildItem Cert:\CurrentUser\My |
        Where-Object { $_.Subject -eq $script:Publisher -and $_.HasPrivateKey -and $_.NotAfter -gt (Get-Date).AddDays(30) -and
                       $_.EnhancedKeyUsageList.ObjectId -contains $codeSigning } |
        Sort-Object NotAfter -Descending | Select-Object -First 1
    if (-not $cert) {
        Write-Host 'Making a signing certificate for this PC...'
        $cert = New-SelfSignedCertificate -Type Custom -Subject $script:Publisher -KeyUsage DigitalSignature `
            -FriendlyName 'NotOnlyFans package signing' -CertStoreLocation Cert:\CurrentUser\My `
            -TextExtension @("2.5.29.37={text}$codeSigning", '2.5.29.19={text}') -NotAfter (Get-Date).AddYears(10)
    }
    return $cert
}

<#
    Builds the app as a signed MSIX into $OutDir and returns the package and the Windows App SDK runtime package
    it needs (@{ Msix; Runtime }). $Version has four parts (MSIX rule).
#>
function Build-AppPackage([string]$Repo, [string]$Version, $Certificate, [string]$OutDir, [switch]$SelfContained) {
    Remove-Item $OutDir -Recurse -Force -ErrorAction SilentlyContinue
    Write-Host "Building NotOnlyFans $Version..."
    $extra = @()
    if ($SelfContained) { $extra += '--self-contained', 'true' }
    & dotnet publish (Join-Path $Repo 'NotOnlyFans\NotOnlyFans.csproj') -c Release -r win-x64 -p:Platform=x64 @extra `
        "-p:AppVersion=$Version" -p:GenerateAppxPackageOnBuild=true -p:AppxBundle=Never `
        -p:AppxPackageSigningEnabled=true "-p:PackageCertificateThumbprint=$($Certificate.Thumbprint)" `
        "-p:AppxPackageDir=$OutDir\" --nologo -v:minimal -nodeReuse:false -p:UseSharedCompilation=false
    if ($LASTEXITCODE -ne 0) { throw "Build failed (dotnet publish exit code $LASTEXITCODE)." }
    $msix = Get-ChildItem $OutDir -Recurse -Filter "NotOnlyFans_${Version}_x64.msix" | Select-Object -First 1
    if (-not $msix) { throw "The package was not made (no NotOnlyFans_${Version}_x64.msix in $OutDir)." }
    $runtime = Get-ChildItem $OutDir -Recurse -Filter 'Microsoft.WindowsAppRuntime*.msix' |
        Where-Object { $_.Directory.Name -eq 'x64' } | Select-Object -First 1
    return @{ Msix = $msix; Runtime = $runtime }
}
