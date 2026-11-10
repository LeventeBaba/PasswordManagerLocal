#Requires -Version 5.1
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][ValidateSet('Validate', 'Verify')][string]$Mode,
    [Parameter(Mandatory = $true)][string]$KeyStore,
    [Parameter(Mandatory = $true)][string]$KeyAlias,
    [Parameter(Mandatory = $true)][string]$StorePassReference,
    [Parameter(Mandatory = $true)][string]$KeyPassReference,
    [Alias('ApkPath')][string]$PackagePath,
    [string]$JavaSdkDirectory,
    [string]$AndroidSdkDirectory,
    [string]$ApkSignerJar,
    [string]$ExpectedCertificateSha256,
    [ValidateSet('apk', 'aab')][string]$PackageFormat = 'apk'
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '..\Signing\ReleaseSigning.Common.ps1')
try {
    $null = Resolve-SigningPasswordReference $KeyPassReference
    $fingerprint = Get-AndroidReleaseCertificate -KeyStore $KeyStore -KeyAlias $KeyAlias `
        -StorePassReference $StorePassReference -JavaSdkDirectory $JavaSdkDirectory `
        -ExpectedCertificateSha256 $ExpectedCertificateSha256
    if ($Mode -eq 'Verify') {
        if ($PackageFormat -eq 'aab') {
            Assert-AndroidReleaseBundle -BundlePath $PackagePath -KeyStore $KeyStore -KeyAlias $KeyAlias `
                -StorePassReference $StorePassReference -ExpectedCertificateSha256 $fingerprint -JavaSdkDirectory $JavaSdkDirectory
        }
        else {
            Assert-AndroidReleaseApk -ApkPath $PackagePath -ExpectedCertificateSha256 $fingerprint `
                -JavaSdkDirectory $JavaSdkDirectory -AndroidSdkDirectory $AndroidSdkDirectory -ApkSignerJar $ApkSignerJar
        }
    }
    else {
        Assert-AndroidReleasePrivateKey -KeyStore $KeyStore -KeyAlias $KeyAlias `
            -StorePassReference $StorePassReference -KeyPassReference $KeyPassReference `
            -JavaSdkDirectory $JavaSdkDirectory
        $null = Find-SigningJavaTool -Name 'java' -JavaSdkDirectory $JavaSdkDirectory
        if ($PackageFormat -eq 'aab') {
            $null = Find-SigningJavaTool -Name 'jarsigner' -JavaSdkDirectory $JavaSdkDirectory
        }
        elseif (-not $ApkSignerJar) {
            $null = Find-SigningApkSignerJar -AndroidSdkDirectory $AndroidSdkDirectory
        }
        Write-Host "Validated Android release certificate SHA-256: $fingerprint"
        Write-Host 'Validated Android private signing key access.'
    }
}
catch { Write-Error $_.Exception.Message; exit 1 }
