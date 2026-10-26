[CmdletBinding()]
param(
    [Alias('w')]
    [switch]$Windows,

    [Alias('a')]
    [switch]$Android,

    [Alias('f')]
    [switch]$Full,

    [switch]$SignWindows,

    [Parameter(Position = 0)]
    [string]$AndroidVersion
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Signing\ReleaseSigning.Common.ps1')
$script:OriginalSigningSecrets = @{}
$script:AndroidSigningSettings = $null
$script:AndroidTargetFramework = 'net10.0-android36.0'
$script:AndroidTargetApiLevel = 36
$script:DefaultAndroidVersion = 10
$script:AndroidApiLevels = @{
    10 = 29
    11 = 30
    12 = 31
    13 = 33
    14 = 34
    15 = 35
    16 = 36
}

function Write-Usage {
    Write-Host 'Usage:'
    Write-Host '  .\publish.cmd -W           Publish trimmed, self-contained Windows x64'
    Write-Host '  .\publish.cmd -W -SignWindows  Publish Windows with verified Authenticode signatures'
    Write-Host '  .\publish.cmd -A           Publish Android APKs supporting Android 10 and later'
    Write-Host '  .\publish.cmd -A 16        Publish an ARM64 APK requiring Android 16 or later'
    Write-Host '  .\publish.cmd -F [10-16]   Publish Windows x64 and Android'
    Write-Host ''
    Write-Host 'The Android number selects the minimum installable Android version.'
    Write-Host 'All Android builds compile and target Android 16 (API 36).'
    Write-Host 'Android 10-15 builds produce ARM64 and ARM32 APKs; Android 16 produces ARM64.'
    Write-Host 'Android publishing requires PublishKeys\PasswordManagerLocal-release.jks and securely prompts for passwords.'
    Write-Host 'Windows publishing is unsigned for testing unless -SignWindows is supplied.'
    Write-Host 'Signed Windows publishing requires a trusted code-signing certificate. See Docs\RELEASE_SIGNING.md.'
}

function Initialize-AndroidReleaseSigning {
    $script:AndroidSigningSettings = Get-ReleaseSigningSettings
    if (-not (Test-Path -LiteralPath $script:AndroidSigningSettings.AndroidKeyStore -PathType Leaf)) {
        throw "Release keystore not found: $($script:AndroidSigningSettings.AndroidKeyStore)"
    }
    foreach ($name in @('PML_ANDROID_STORE_PASSWORD', 'PML_ANDROID_KEY_PASSWORD')) {
        $script:OriginalSigningSecrets[$name] = [Environment]::GetEnvironmentVariable($name, 'Process')
    }
    if ([string]::IsNullOrEmpty($env:PML_ANDROID_STORE_PASSWORD)) {
        $secret = Read-Host 'Android release keystore password' -AsSecureString
        try { $env:PML_ANDROID_STORE_PASSWORD = ConvertFrom-SigningSecureString $secret }
        finally { $secret.Dispose() }
    }
    if ([string]::IsNullOrEmpty($env:PML_ANDROID_KEY_PASSWORD)) {
        $secret = Read-Host 'Android key password (Enter = same as keystore password)' -AsSecureString
        try {
            if ($secret.Length -eq 0) { $env:PML_ANDROID_KEY_PASSWORD = $env:PML_ANDROID_STORE_PASSWORD }
            else { $env:PML_ANDROID_KEY_PASSWORD = ConvertFrom-SigningSecureString $secret }
        }
        finally { $secret.Dispose() }
    }
    $null = Resolve-SigningPasswordReference 'env:PML_ANDROID_KEY_PASSWORD'

    # Each invocation gets distinct native-tool references. A reused build
    # process must never resolve a reference to a previous publish's password.
    $secretSuffix = [Guid]::NewGuid().ToString('N')
    $storeVariable = 'PML_PUBLISH_STORE_' + $secretSuffix
    $keyVariable = 'PML_PUBLISH_KEY_' + $secretSuffix
    $script:OriginalSigningSecrets[$storeVariable] = $null
    $script:OriginalSigningSecrets[$keyVariable] = $null
    [Environment]::SetEnvironmentVariable($storeVariable, $env:PML_ANDROID_STORE_PASSWORD, 'Process')
    [Environment]::SetEnvironmentVariable($keyVariable, $env:PML_ANDROID_KEY_PASSWORD, 'Process')
    $script:AndroidStorePassReference = 'env:' + $storeVariable
    $script:AndroidKeyPassReference = 'env:' + $keyVariable
    $script:AndroidSigningFingerprint = Get-AndroidReleaseCertificate `
        -KeyStore $script:AndroidSigningSettings.AndroidKeyStore -KeyAlias $script:AndroidSigningSettings.AndroidKeyAlias `
        -StorePassReference $script:AndroidStorePassReference `
        -ExpectedCertificateSha256 $script:AndroidSigningSettings.AndroidCertificateSha256
    $javaTool = Find-SigningJavaTool -Name 'java'
    $javaBin = Split-Path -Parent $javaTool
    $script:AndroidJavaSdkDirectory = Split-Path -Parent $javaBin
    if ((Split-Path -Leaf $javaBin) -ine 'bin') { throw 'The JDK java executable must be inside its bin directory.' }
    Assert-AndroidReleasePrivateKey -KeyStore $script:AndroidSigningSettings.AndroidKeyStore `
        -KeyAlias $script:AndroidSigningSettings.AndroidKeyAlias `
        -StorePassReference $script:AndroidStorePassReference -KeyPassReference $script:AndroidKeyPassReference `
        -JavaSdkDirectory $script:AndroidJavaSdkDirectory
    $script:AndroidApkSignerJar = Find-SigningApkSignerJar
    Write-Host 'Validated Android private signing key access.'
    Write-Host "Android release signer SHA-256: $script:AndroidSigningFingerprint"
    Write-Host "APK signature verifier: $script:AndroidApkSignerJar"
}

function Resolve-AndroidPublishSettings {
    param(
        [string]$RequestedVersion
    )

    if ([string]::IsNullOrWhiteSpace($RequestedVersion)) {
        $version = $script:DefaultAndroidVersion
    }
    else {
        $version = 0
        if (-not [int]::TryParse($RequestedVersion, [ref]$version)) {
            throw [System.FormatException]::new(
                "Invalid Android version '$RequestedVersion'. Enter a whole number from 10 through 16, for example: publish -A 16"
            )
        }
    }

    if (-not $script:AndroidApiLevels.ContainsKey($version)) {
        $supportedVersions = ($script:AndroidApiLevels.Keys | Sort-Object) -join ', '
        throw [System.ArgumentOutOfRangeException]::new(
            'AndroidVersion',
            $version,
            "Unsupported Android version. Supported values are: $supportedVersions."
        )
    }

    [PSCustomObject]@{
        Version = $version
        MinimumApiLevel = $script:AndroidApiLevels[$version]
    }
}

function Invoke-CommandChecked {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Description,

        [Parameter(Mandatory = $true)]
        [scriptblock]$Command
    )

    Write-Host ''
    Write-Host $Description
    Write-Host ('-' * $Description.Length)

    & $Command | Out-Host
    $exitCode = $LASTEXITCODE

    if ($exitCode -ne 0) {
        throw "$Description failed with exit code $exitCode."
    }
}

function Get-VersionSortValue {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Value
    )

    $stablePart = ($Value -split '-', 2)[0]
    try {
        return [version]$stablePart
    }
    catch {
        return [version]'0.0'
    }
}

function Find-AndroidBuildTool {
    param(
        [Parameter(Mandatory = $true)]
        [string]$ToolName
    )

    $sdkCandidates = New-Object System.Collections.Generic.List[string]

    foreach ($candidate in @($env:ANDROID_SDK_ROOT, $env:ANDROID_HOME)) {
        if (-not [string]::IsNullOrWhiteSpace($candidate)) {
            $sdkCandidates.Add($candidate)
        }
    }

    if (-not [string]::IsNullOrWhiteSpace($env:LOCALAPPDATA)) {
        $sdkCandidates.Add((Join-Path $env:LOCALAPPDATA 'Android\Sdk'))
    }

    if (-not [string]::IsNullOrWhiteSpace(${env:ProgramFiles(x86)})) {
        $sdkCandidates.Add((Join-Path ${env:ProgramFiles(x86)} 'Android\android-sdk'))
    }

    if (-not [string]::IsNullOrWhiteSpace($env:ProgramFiles)) {
        $sdkCandidates.Add((Join-Path $env:ProgramFiles 'Android\android-sdk'))
    }

    foreach ($sdkRoot in ($sdkCandidates | Select-Object -Unique)) {
        $buildToolsRoot = Join-Path $sdkRoot 'build-tools'
        if (-not (Test-Path $buildToolsRoot)) {
            continue
        }

        $buildToolVersions = Get-ChildItem -Path $buildToolsRoot -Directory |
            Sort-Object -Property @{ Expression = { Get-VersionSortValue $_.Name } } -Descending

        foreach ($buildToolVersion in $buildToolVersions) {
            $toolPath = Join-Path $buildToolVersion.FullName $ToolName
            if (Test-Path $toolPath) {
                return $toolPath
            }
        }
    }

    return $null
}

function Test-AndroidPackage {
    param(
        [Parameter(Mandatory = $true)]
        [string]$ApkPath,

        [Parameter(Mandatory = $true)]
        [int]$ExpectedMinimumApiLevel,

        [Parameter(Mandatory = $true)]
        [string]$ExpectedAbi
    )

    $aapt2 = Find-AndroidBuildTool 'aapt2.exe'
    if (-not $aapt2) {
        Write-Warning 'aapt2.exe was not found. APK manifest and ABI validation was skipped.'
    }
    else {
        $badgingOutput = & $aapt2 dump badging $ApkPath 2>&1
        if ($LASTEXITCODE -ne 0) {
            throw "aapt2 could not inspect '$ApkPath'.`n$($badgingOutput -join "`n")"
        }

        $badgingText = $badgingOutput -join "`n"
        $minimumSdkMatch = [regex]::Match($badgingText, "sdkVersion:'(?<value>\d+)'", 'IgnoreCase')
        $targetSdkMatch = [regex]::Match($badgingText, "targetSdkVersion:'(?<value>\d+)'", 'IgnoreCase')
        $nativeCodeMatch = [regex]::Match($badgingText, "native-code:(?<value>[^\r\n]+)", 'IgnoreCase')

        if (-not $minimumSdkMatch.Success) {
            throw "The APK minimum SDK could not be determined: '$ApkPath'."
        }

        $actualMinimumApiLevel = [int]$minimumSdkMatch.Groups['value'].Value
        if ($actualMinimumApiLevel -ne $ExpectedMinimumApiLevel) {
            throw "APK minimum SDK validation failed. Expected API $ExpectedMinimumApiLevel but found API $actualMinimumApiLevel."
        }

        if (-not $targetSdkMatch.Success) {
            throw "The APK target SDK could not be determined: '$ApkPath'."
        }

        $actualTargetApiLevel = [int]$targetSdkMatch.Groups['value'].Value
        if ($actualTargetApiLevel -ne $script:AndroidTargetApiLevel) {
            throw "APK target SDK validation failed. Expected API $($script:AndroidTargetApiLevel) but found API $actualTargetApiLevel."
        }

        if (-not $nativeCodeMatch.Success) {
            throw "The APK does not report any packaged native CPU architectures: '$ApkPath'."
        }

        $nativeCode = $nativeCodeMatch.Groups['value'].Value
        $escapedExpectedAbi = [regex]::Escape($ExpectedAbi)
        if ($nativeCode -notmatch "'$escapedExpectedAbi'") {
            throw "APK CPU architecture validation failed. Expected '$ExpectedAbi'. Reported architectures: $nativeCode"
        }

        foreach ($unexpectedAbi in @('armeabi-v7a', 'arm64-v8a', 'x86', 'x86_64') | Where-Object { $_ -ne $ExpectedAbi }) {
            $escapedUnexpectedAbi = [regex]::Escape($unexpectedAbi)
            if ($nativeCode -match "'$escapedUnexpectedAbi'") {
                throw "APK CPU architecture validation failed. The architecture-specific APK unexpectedly also contains '$unexpectedAbi'. Reported architectures: $nativeCode"
            }
        }

        Write-Host "Verified minimum SDK: API $actualMinimumApiLevel"
        Write-Host "Verified target SDK: API $actualTargetApiLevel"
        Write-Host "Verified CPU architecture: $ExpectedAbi"
    }

    $zipalign = Find-AndroidBuildTool 'zipalign.exe'
    if (-not $zipalign) {
        Write-Warning 'zipalign.exe was not found. APK 16 KB ZIP alignment validation was skipped.'
    }
    else {
        $zipalignOutput = & $zipalign -c -P 16 -v 4 $ApkPath 2>&1
        if ($LASTEXITCODE -ne 0) {
            throw "APK 16 KB ZIP alignment validation failed for '$ApkPath'.`n$($zipalignOutput -join "`n")"
        }

        Write-Host 'Verified APK ZIP alignment for 16 KB page-size devices.'
    }
}

function Publish-WindowsApp {
    $script = Join-Path $script:Root 'Tools\Windows\PublishWindowsProduct.ps1'

    Invoke-CommandChecked 'Publishing minimal shared Windows x64 product' {
        & $script -Configuration Release -RuntimeIdentifier win-x64 -Sign:$SignWindows
    }
}

function Publish-AndroidArchitecture {
    param(
        [Parameter(Mandatory = $true)]
        [PSCustomObject]$Settings,

        [Parameter(Mandatory = $true)]
        [PSCustomObject]$Architecture,

        [Parameter(Mandatory = $true)]
        [string]$Project,

        [Parameter(Mandatory = $true)]
        [string]$OutputRoot
    )

    $targetBinRoot = Join-Path $script:Root "Android\Frontend\bin\Release\$($script:AndroidTargetFramework)"
    $architectureOutput = Join-Path $OutputRoot $Architecture.Abi

    if (Test-Path $targetBinRoot) {
        Remove-Item -Path $targetBinRoot -Recurse -Force
    }

    if (Test-Path $architectureOutput) {
        Remove-Item -Path $architectureOutput -Recurse -Force
    }

    New-Item -ItemType Directory -Path $architectureOutput -Force | Out-Null

    $buildArguments = @(
        'build',
        $Project,
        '--disable-build-servers',
        '--verbosity', 'normal',
        '-c', 'Release',
        '-f', $script:AndroidTargetFramework,
        '-r', $Architecture.RuntimeIdentifier,
        '-t:SignAndroidPackage',
        '-p:AndroidKeyStore=true',
        '-p:AndroidUseApkSigner=true',
        "-p:JavaSdkDirectory=$script:AndroidJavaSdkDirectory",
        "-p:ApkSignerJar=$script:AndroidApkSignerJar",
        "-p:AndroidSigningKeyStore=$($script:AndroidSigningSettings.AndroidKeyStore)",
        "-p:AndroidSigningKeyAlias=$($script:AndroidSigningSettings.AndroidKeyAlias)",
        "-p:AndroidSigningStorePass=$script:AndroidStorePassReference",
        "-p:AndroidSigningKeyPass=$script:AndroidKeyPassReference",
        "-p:PmlAndroidCertificateSha256=$script:AndroidSigningFingerprint",
        "-p:SupportedOSPlatformVersion=$($Settings.MinimumApiLevel).0",
        "-p:AndroidMinimumVersion=$($Settings.Version)",
        "-p:AndroidArtifactsPublishDir=$architectureOutput",
        '-p:WarningsAsErrors=XA0141'
    )

    Invoke-CommandChecked "Publishing Android $($Architecture.Abi) APK" {
        dotnet @buildArguments
    }

    $signedApk = Get-ChildItem -Path $architectureOutput -Recurse -File -Filter '*.apk' |
        Where-Object { $_.Name -match '-signed\.apk$' } |
        Sort-Object LastWriteTimeUtc -Descending |
        Select-Object -First 1

    if (-not $signedApk) {
        $signedApk = Get-ChildItem -Path $targetBinRoot -Recurse -File -Filter '*.apk' |
            Where-Object { $_.Name -match '-signed\.apk$' } |
            Sort-Object LastWriteTimeUtc -Descending |
            Select-Object -First 1
    }

    if (-not $signedApk) {
        throw "No newly built signed APK was found for $($Architecture.Abi)."
    }

    $finalName = "PasswordManagerLocal-android-$($Settings.Version)-plus-$($Architecture.Abi)-signed.apk"
    $finalPath = Join-Path $OutputRoot $finalName

    Write-Host ''
    Write-Host "Validating APK: $finalName"
    Assert-AndroidReleaseApk -ApkPath $signedApk.FullName -ExpectedCertificateSha256 $script:AndroidSigningFingerprint `
        -JavaSdkDirectory $script:AndroidJavaSdkDirectory -ApkSignerJar $script:AndroidApkSignerJar
    Test-AndroidPackage -ApkPath $signedApk.FullName -ExpectedMinimumApiLevel $Settings.MinimumApiLevel -ExpectedAbi $Architecture.Abi
    Copy-Item -LiteralPath $signedApk.FullName -Destination $finalPath -Force

    return $finalPath
}

function Publish-AndroidApp {
    param(
        [Parameter(Mandatory = $true)]
        [PSCustomObject]$Settings
    )

    $project = Join-Path $script:Root 'Android\Frontend\PasswordManagerLocal.Android.Frontend.csproj'
    $outputRoot = Join-Path $script:Root "artifacts\publish\PasswordManagerLocal.Android.Frontend\android-$($Settings.Version)-plus"

    if (Test-Path $outputRoot) {
        Remove-Item -Path $outputRoot -Recurse -Force
    }

    New-Item -ItemType Directory -Path $outputRoot -Force | Out-Null

    $architectures = @(
        [PSCustomObject]@{
            RuntimeIdentifier = 'android-arm64'
            Abi = 'arm64-v8a'
        }
    )

    if ($Settings.Version -le 15) {
        $architectures += [PSCustomObject]@{
            RuntimeIdentifier = 'android-arm'
            Abi = 'armeabi-v7a'
        }
    }

    Write-Host ''
    Write-Host "Android compile/target version: Android 16 (API $($script:AndroidTargetApiLevel))"
    Write-Host "Android minimum install version: Android $($Settings.Version) (API $($Settings.MinimumApiLevel))"
    Write-Host "APK CPU architectures: $(($architectures.Abi) -join ', ')"

    $publishedApks = @()
    foreach ($architecture in $architectures) {
        $publishedApks += Publish-AndroidArchitecture -Settings $Settings -Architecture $architecture -Project $project -OutputRoot $outputRoot
    }

    Write-Host ''
    Write-Host "Android publish output: $outputRoot"
    foreach ($publishedApk in $publishedApks) {
        Write-Host "Signed APK: $(Split-Path -Leaf $publishedApk)"
    }
}

$script:Root = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
Set-Location $script:Root

$selectedCount = 0
if ($Windows.IsPresent) { $selectedCount++ }
if ($Android.IsPresent) { $selectedCount++ }
if ($Full.IsPresent) { $selectedCount++ }

if ($selectedCount -eq 0) {
    Write-Usage
    exit 1
}

if ($selectedCount -gt 1) {
    Write-Host 'Choose exactly one publish mode: -W, -A, or -F.' -ForegroundColor Red
    Write-Usage
    exit 2
}

if (-not [string]::IsNullOrWhiteSpace($AndroidVersion) -and -not ($Android.IsPresent -or $Full.IsPresent)) {
    Write-Host 'An Android version can only be supplied with -A or -F.' -ForegroundColor Red
    Write-Usage
    exit 2
}

if ($SignWindows.IsPresent -and -not ($Windows.IsPresent -or $Full.IsPresent)) {
    Write-Host '-SignWindows can only be supplied with -W or -F.' -ForegroundColor Red
    Write-Usage
    exit 2
}

$androidSettings = $null
if ($Android.IsPresent -or $Full.IsPresent) {
    try {
        $androidSettings = Resolve-AndroidPublishSettings -RequestedVersion $AndroidVersion
    }
    catch [System.FormatException] {
        Write-Host $_.Exception.Message -ForegroundColor Red
        exit 2
    }
    catch [System.ArgumentOutOfRangeException] {
        Write-Host $_.Exception.Message -ForegroundColor Red
        exit 3
    }
}

try {
    if ($Android.IsPresent -or $Full.IsPresent) { Initialize-AndroidReleaseSigning }
    if ($Full.IsPresent) {
        Publish-WindowsApp
        Publish-AndroidApp -Settings $androidSettings
        exit 0
    }

    if ($Windows.IsPresent) {
        Publish-WindowsApp
    }

    if ($Android.IsPresent) {
        Publish-AndroidApp -Settings $androidSettings
    }
}
catch {
    Write-Host ''
    Write-Host $_.Exception.Message -ForegroundColor Red
    exit 1
}
finally {
    foreach ($entry in $script:OriginalSigningSecrets.GetEnumerator()) {
        [Environment]::SetEnvironmentVariable($entry.Key, $entry.Value, 'Process')
    }
}
