#Requires -Version 5.1
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$RepositoryRoot,
    [Parameter(Mandatory = $true)][string]$ProductDirectory,
    [Parameter(Mandatory = $true)][string]$OutputRoot,
    [Parameter(Mandatory = $true)][string]$ReportRoot,
    [PSCustomObject]$SigningContext
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'WindowsReleaseSigning.ps1')

$licensePath = Join-Path $RepositoryRoot 'LICENSE.txt'
$licenseRtf = Join-Path $ReportRoot 'installer-license.rtf'
$project = Join-Path $RepositoryRoot 'Windows\Installer\PasswordManagerLocal.Windows.Installer.wixproj'
$buildRoot = Join-Path $OutputRoot 'installer-build'
$stage = Join-Path $buildRoot 'package'
$intermediateRoot = Join-Path $buildRoot 'obj'
$binaryLog = Join-Path $ReportRoot 'installer-build.binlog'
$installerName = 'PasswordManagerLocalDemo.msi'
# One fresh product identity per release, shared by both inputs to the transform.
$productCode = [Guid]::NewGuid().ToString('B').ToUpperInvariant()

# RTF uses signed 16-bit UTF-16 units for Unicode. Escape the exact supplied
# license for the wizard and package the original text file separately.
$license = [IO.File]::ReadAllText($licensePath).Replace("`r`n", "`n").Replace("`r", "`n")
$rtf = $license.Replace('\', '\\').Replace('{', '\{').Replace('}', '\}')
$rtf = [regex]::Replace($rtf, '[^\x00-\x7F]', {
    param($match)
    $code = [int][char]$match.Value[0]
    if ($code -gt 32767) { $code -= 65536 }
    return "\u${code}?"
})
$rtf = $rtf.Replace("`n", "\par`r`n")
[IO.File]::WriteAllText($licenseRtf, "{\rtf1\ansi\uc1 $rtf}", [Text.Encoding]::ASCII)

$maintenancePath = Join-Path $RepositoryRoot 'Windows\Installer\CompleteWindowsInstallation.ps1'
$maintenance = [IO.File]::ReadAllText($maintenancePath)
$unifiedFlag = if (Test-Path -LiteralPath (Join-Path $ProductDirectory 'PasswordManagerLocal.Windows.Agent.exe') -PathType Leaf) {
    '$false'
}
else {
    '$true'
}
$installationScript = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes(
    '$pmlUnified = ' + $unifiedFlag + "`n" + $maintenance))

$buildArguments = @(
    'build', $project,
    '--configuration', 'Release',
    # MSBuild requires a trailing separator on this global property. A forward
    # slash avoids escaping the closing quote for paths with spaces.
    "-p:BaseIntermediateOutputPath=$intermediateRoot/",
    '--output', $stage,
    "-p:PmlProductDirectory=$ProductDirectory",
    "-p:PmlProductCode=$productCode",
    "-p:PmlLicenseRtf=$licenseRtf",
    "-p:PmlWindowsInstallationScript=$installationScript",
    "-bl:$binaryLog"
)
Write-Host 'Building the Windows x64 MSI with English and Hungarian setup text...'
Write-Host "Installer build diagnostics: $binaryLog"
Push-Location -LiteralPath $RepositoryRoot
try {
    & dotnet @buildArguments
    if ($LASTEXITCODE -ne 0) {
        throw "MSI build failed with exit code $LASTEXITCODE. See '$binaryLog' and Docs\WINDOWS_INSTALLER.md for WiX setup."
    }
}
finally {
    Pop-Location
}

$stagedInstaller = Join-Path $stage "en-US\$installerName"
$hungarianInstaller = Join-Path $stage "hu-HU\$installerName"
foreach ($path in @($stagedInstaller, $hungarianInstaller)) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "WiX did not produce the expected installer input: $path"
    }
}

# Embed the translation before signing or moving the single public MSI.
& (Join-Path $PSScriptRoot 'EmbedWindowsInstallerHungarian.ps1') -EnglishMsi $stagedInstaller `
    -HungarianMsi $hungarianInstaller -TransformPath (Join-Path $buildRoot 'installer-hu-HU.mst')
if ($SigningContext) {
    Invoke-WindowsReleaseFileSigning -Path $stagedInstaller -Context $SigningContext
}

$installer = Join-Path $OutputRoot $installerName
Move-Item -LiteralPath $stagedInstaller -Destination $installer
Write-Host "Windows installer: $installer"
Write-Host 'Hungarian setup: msiexec /i "PasswordManagerLocalDemo.msi" TRANSFORMS=:1038'
