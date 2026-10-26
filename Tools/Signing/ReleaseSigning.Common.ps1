#Requires -Version 5.1

function Get-ReleaseSigningSettings {
    $root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
    $settings = @{
        AndroidKeyStore = (Join-Path $root 'PublishKeys\PasswordManagerLocal-release.jks')
        AndroidKeyAlias = 'passwordmanagerlocal'
        AndroidCertificateSha256 = ''
        WindowsCertificateThumbprint = ''
        WindowsCertificateStoreLocation = 'CurrentUser'
        WindowsTimestampUrl = ''
        WindowsSignToolPath = ''
    }
    $localConfig = Join-Path $root 'PublishKeys\Signing.local.json'
    if (Test-Path -LiteralPath $localConfig -PathType Leaf) {
        $config = Get-Content -LiteralPath $localConfig -Raw | ConvertFrom-Json
        foreach ($property in $config.PSObject.Properties) {
            if (-not $settings.ContainsKey($property.Name)) {
                throw "Unknown signing setting '$($property.Name)' in $localConfig. Passwords belong in the prompt or environment, not this file."
            }
            $settings[$property.Name] = [string]$property.Value
        }
    }
    if (-not [IO.Path]::IsPathRooted($settings.AndroidKeyStore)) {
        $settings.AndroidKeyStore = Join-Path $root $settings.AndroidKeyStore
    }
    return [PSCustomObject]$settings
}

function ConvertFrom-SigningSecureString {
    param([Parameter(Mandatory = $true)][Security.SecureString]$Value)
    $pointer = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($Value)
    try { return [Runtime.InteropServices.Marshal]::PtrToStringBSTR($pointer) }
    finally { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($pointer) }
}

function Resolve-SigningPasswordReference {
    param([Parameter(Mandatory = $true)][string]$Reference)
    if ($Reference.StartsWith('env:', [StringComparison]::OrdinalIgnoreCase)) {
        $value = [Environment]::GetEnvironmentVariable($Reference.Substring(4), 'Process')
    }
    elseif ($Reference.StartsWith('file:', [StringComparison]::OrdinalIgnoreCase)) {
        # SDK password files use the first line. Never log their contents.
        $value = Get-Content -LiteralPath $Reference.Substring(5) -TotalCount 1
    }
    else { throw 'Signing passwords must use an env: or file: reference; plaintext MSBuild passwords are rejected.' }
    if ([string]::IsNullOrEmpty($value)) { throw 'The signing password reference is missing or empty. Use publish.cmd for secure prompts, or set the referenced secret.' }
    return [string]$value
}

function Invoke-SigningToolCaptured {
    param([Parameter(Mandatory = $true)][string]$FilePath, [Parameter(Mandatory = $true)][string[]]$Arguments)
    # Windows PowerShell 5.1 represents native stderr as ErrorRecords, including
    # successful keytool warnings. Capture it without making those terminating.
    $previousPreference = $ErrorActionPreference
    try {
        $ErrorActionPreference = 'Continue'
        $output = & $FilePath @Arguments 2>&1
        $code = $LASTEXITCODE
    }
    finally { $ErrorActionPreference = $previousPreference }
    return [PSCustomObject]@{ ExitCode = $code; Output = ($output -join "`n") }
}

function Find-SigningJavaTool {
    param([Parameter(Mandatory = $true)][string]$Name, [string]$JavaSdkDirectory)
    foreach ($root in @($JavaSdkDirectory, $env:JAVA_HOME, $env:JavaSdkDirectory)) {
        if ([string]::IsNullOrWhiteSpace($root)) { continue }
        # Also accept the user's existing JAVA_HOME that points directly to bin.
        foreach ($path in @((Join-Path $root "bin\$Name.exe"), (Join-Path $root "$Name.exe"), (Join-Path $root "bin/$Name"))) {
            if (Test-Path -LiteralPath $path -PathType Leaf) { return $path }
        }
    }
    $command = Get-Command $Name -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($command) { return $command.Source }
    throw "$Name was not found. Set JAVA_HOME to your JDK directory or add its bin directory to PATH."
}

function Find-SigningApkSignerJar {
    param([string]$AndroidSdkDirectory)
    $roots = @($AndroidSdkDirectory, $env:ANDROID_SDK_ROOT, $env:ANDROID_HOME)
    if ($env:LOCALAPPDATA) { $roots += Join-Path $env:LOCALAPPDATA 'Android\Sdk' }
    foreach ($programRoot in @(${env:ProgramFiles(x86)}, $env:ProgramFiles)) {
        if ($programRoot) { $roots += Join-Path $programRoot 'Android\android-sdk' }
    }
    foreach ($root in ($roots | Select-Object -Unique)) {
        if ([string]::IsNullOrWhiteSpace($root)) { continue }
        $buildTools = Join-Path $root 'build-tools'
        if (-not (Test-Path -LiteralPath $buildTools -PathType Container)) { continue }
        foreach ($directory in (Get-ChildItem -LiteralPath $buildTools -Directory | Sort-Object {
            try { [version](($_.Name -split '-')[0]) } catch { [version]'0.0' }
        } -Descending)) {
            $jar = Join-Path $directory.FullName 'lib\apksigner.jar'
            if (Test-Path -LiteralPath $jar -PathType Leaf) { return $jar }
        }
    }
    throw 'apksigner.jar was not found. Install Android SDK Build Tools and set ANDROID_SDK_ROOT if necessary. Signature verification cannot be skipped.'
}

function Get-AndroidReleaseCertificate {
    param(
        [Parameter(Mandatory = $true)][string]$KeyStore,
        [Parameter(Mandatory = $true)][string]$KeyAlias,
        [Parameter(Mandatory = $true)][string]$StorePassReference,
        [string]$JavaSdkDirectory,
        [string]$ExpectedCertificateSha256
    )
    if (-not (Test-Path -LiteralPath $KeyStore -PathType Leaf)) { throw "Release keystore not found: $KeyStore" }
    if ([string]::IsNullOrWhiteSpace($KeyAlias) -or $KeyAlias -ieq 'androiddebugkey' -or [IO.Path]::GetFileName($KeyStore) -ieq 'debug.keystore') {
        throw 'A dedicated Android release key is required. The Android debug key is forbidden.'
    }
    $keytool = Find-SigningJavaTool -Name 'keytool' -JavaSdkDirectory $JavaSdkDirectory
    $variable = 'PML_KEYTOOL_SECRET_' + [Guid]::NewGuid().ToString('N')
    try {
        [Environment]::SetEnvironmentVariable($variable, (Resolve-SigningPasswordReference $StorePassReference), 'Process')
        $result = Invoke-SigningToolCaptured -FilePath $keytool -Arguments @('-exportcert', '-rfc', '-keystore', $KeyStore, '-alias', $KeyAlias, '-storepass:env', $variable)
        if ($result.ExitCode -ne 0) { throw 'Could not read the release certificate. Check the keystore password, alias, and file.' }
        $match = [regex]::Match($result.Output, '-----BEGIN CERTIFICATE-----(?<data>[\s\S]+?)-----END CERTIFICATE-----')
        if (-not $match.Success) { throw 'keytool did not return an X.509 release certificate.' }
        $certificate = [Security.Cryptography.X509Certificates.X509Certificate2]::new([Convert]::FromBase64String($match.Groups['data'].Value))
        try {
            Assert-AndroidReleaseCertificate -Certificate $certificate
            $sha = [Security.Cryptography.SHA256]::Create()
            try { $fingerprint = [BitConverter]::ToString($sha.ComputeHash($certificate.RawData)).Replace('-', '') }
            finally { $sha.Dispose() }
            if ($ExpectedCertificateSha256) {
                $expected = $ExpectedCertificateSha256.Replace(':', '').Replace(' ', '').ToUpperInvariant()
                if ($expected -notmatch '^[0-9A-F]{64}$' -or $expected -cne $fingerprint) { throw 'The release keystore certificate does not match the configured SHA-256 fingerprint.' }
            }
            return $fingerprint
        }
        finally { $certificate.Dispose() }
    }
    finally { [Environment]::SetEnvironmentVariable($variable, $null, 'Process') }
}

function Assert-AndroidReleasePrivateKey {
    param(
        [Parameter(Mandatory = $true)][string]$KeyStore,
        [Parameter(Mandatory = $true)][string]$KeyAlias,
        [Parameter(Mandatory = $true)][string]$StorePassReference,
        [Parameter(Mandatory = $true)][string]$KeyPassReference,
        [string]$JavaSdkDirectory
    )
    $keytool = Find-SigningJavaTool -Name 'keytool' -JavaSdkDirectory $JavaSdkDirectory
    $storePassword = Resolve-SigningPasswordReference $StorePassReference
    $keyPassword = Resolve-SigningPasswordReference $KeyPassReference
    $suffix = [Guid]::NewGuid().ToString('N')
    $storeVariable = 'PML_KEYTOOL_STORE_' + $suffix
    $keyVariable = 'PML_KEYTOOL_KEY_' + $suffix
    try {
        [Environment]::SetEnvironmentVariable($storeVariable, $storePassword, 'Process')
        [Environment]::SetEnvironmentVariable($keyVariable, $keyPassword, 'Process')
        # Creating a public CSR proves the private key can be unlocked and used.
        # Capture the CSR in memory; keytool neither changes the keystore nor
        # sends the request anywhere, and no private key is exported.
        $result = Invoke-SigningToolCaptured -FilePath $keytool -Arguments @(
            '-certreq', '-keystore', $KeyStore, '-alias', $KeyAlias,
            '-storepass:env', $storeVariable, '-keypass:env', $keyVariable
        )
        if ($result.ExitCode -ne 0 -or $result.Output -notmatch '-----BEGIN (?:NEW )?CERTIFICATE REQUEST-----') {
            $diagnostic = $result.Output
            foreach ($secret in (@($storePassword, $keyPassword) | Sort-Object { $_.Length } -Descending)) {
                $diagnostic = $diagnostic.Replace($secret, '[redacted]')
            }
            throw "The Android private signing key could not be unlocked for alias '$KeyAlias'. Check the key password; it may differ from the keystore password.`n$diagnostic"
        }
    }
    finally {
        [Environment]::SetEnvironmentVariable($storeVariable, $null, 'Process')
        [Environment]::SetEnvironmentVariable($keyVariable, $null, 'Process')
    }
}

function Assert-AndroidReleaseCertificate {
    param([Parameter(Mandatory = $true)][Security.Cryptography.X509Certificates.X509Certificate2]$Certificate)
    if ($Certificate.Subject -match '(?i)(?:^|,\s*)CN\s*=\s*"?Android Debug"?(?:,|$)') {
        throw 'The selected certificate is an Android debug certificate, even if its file or alias was renamed.'
    }
    $now = Get-Date
    if ($Certificate.NotBefore -gt $now -or $Certificate.NotAfter -le $now) { throw 'The Android release certificate is not currently valid.' }
}

function Assert-AndroidApkSignerOutput {
    param([Parameter(Mandatory = $true)][string]$Output, [Parameter(Mandatory = $true)][string]$ExpectedCertificateSha256)
    # Count actual signers rather than certificate-detail lines. Build Tools 37
    # uses scheme labels such as "V3.0 Signer:" and can repeat the same signer
    # under several schemes; older tools use "Signer #1" or SDK-range labels.
    $text = $Output.Replace("`r`n", "`n").Replace("`r", "`n")
    $counts = [regex]::Matches($text, '(?im)^[\t ]*Number of signers:[\t ]*(\d+)[\t ]*$')
    if ($counts.Count -ne 1 -or $counts[0].Groups[1].Value -ne '1') {
        throw 'The APK must contain exactly one verified release signer.'
    }

    $signers = [regex]::Matches($text,
        '(?im)^[\t ]*(?:V\d+(?:\.\d+)*[\t ]+)?Signer(?:[\t ]+#(?<index>\d+)|[\t ]+\(minSdkVersion=[^\r\n]+\))?[\t ]*:?[\t ]+certificate SHA-256 digest:[\t ]*(?<digest>[0-9a-f:]+)[\t ]*$')
    if ($signers.Count -eq 0) {
        throw 'APK signature verification returned no readable signer certificate SHA-256 fingerprint.'
    }
    if (@($signers | Where-Object { $_.Groups['index'].Success -and $_.Groups['index'].Value -ne '1' }).Count -gt 0) {
        throw 'The APK must contain exactly one verified release signer.'
    }
    foreach ($signer in $signers) {
        $actual = $signer.Groups['digest'].Value.Replace(':', '').ToUpperInvariant()
        if ($actual -notmatch '^[0-9A-F]{64}$' -or $actual -cne $ExpectedCertificateSha256.ToUpperInvariant()) {
            throw 'APK signer does not match the selected release keystore. Debug or unexpected signatures are forbidden.'
        }
    }
}

function Assert-AndroidReleaseApk {
    param(
        [Parameter(Mandatory = $true)][string]$ApkPath,
        [Parameter(Mandatory = $true)][string]$ExpectedCertificateSha256,
        [string]$JavaSdkDirectory,
        [string]$AndroidSdkDirectory,
        [string]$ApkSignerJar
    )
    if (-not (Test-Path -LiteralPath $ApkPath -PathType Leaf)) { throw "Signed APK not found: $ApkPath" }
    $java = Find-SigningJavaTool -Name 'java' -JavaSdkDirectory $JavaSdkDirectory
    if (-not $ApkSignerJar) { $ApkSignerJar = Find-SigningApkSignerJar -AndroidSdkDirectory $AndroidSdkDirectory }
    $result = Invoke-SigningToolCaptured -FilePath $java -Arguments @('-jar', $ApkSignerJar, 'verify', '--verbose', '--print-certs', $ApkPath)
    if ($result.ExitCode -ne 0) {
        throw "APK cryptographic signature verification failed (exit $($result.ExitCode)).`nAPK: $ApkPath`nVerifier: $ApkSignerJar`n$($result.Output)"
    }
    try {
        Assert-AndroidApkSignerOutput -Output $result.Output -ExpectedCertificateSha256 $ExpectedCertificateSha256
    }
    catch {
        # Verification output contains public certificate details, never signing
        # passwords. Keep it in failures so tool errors and format changes are diagnosable.
        throw "$($_.Exception.Message)`nAPK: $ApkPath`nVerifier: $ApkSignerJar`nVerifier output:`n$($result.Output)"
    }
    foreach ($line in ($result.Output -split "`r?`n")) {
        if ($line -match '^WARNING:') { Write-Warning "apksigner: $line" }
    }
    Write-Host "Verified release APK signer SHA-256: $ExpectedCertificateSha256"
}
