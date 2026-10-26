#Requires -Version 5.1
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'ReleaseSigning.Common.ps1')
. (Join-Path $PSScriptRoot '..\Windows\WindowsReleaseSigning.ps1')
$script:checks = 0

function Assert-SigningCheck {
    param([bool]$Condition, [string]$Description)
    if (-not $Condition) { throw "FAILED: $Description" }
    $script:checks++
}

function Assert-SigningFailure {
    param([scriptblock]$Action, [string]$ExpectedMessage)
    $caught = $null
    try { & $Action | Out-Null } catch { $caught = $_.Exception.Message }
    Assert-SigningCheck ($null -ne $caught -and $caught.Contains($ExpectedMessage)) "Expected failure: $ExpectedMessage; got: $caught"
}

$temp = Join-Path ([IO.Path]::GetTempPath()) ('pml-signing-check-' + [Guid]::NewGuid().ToString('N'))
$secretName = 'PML_SIGNING_TEST_' + [Guid]::NewGuid().ToString('N')
$wrongSecretName = $secretName + '_WRONG'
$keySecretName = $secretName + '_KEY'
$reference = 'env:' + $secretName
New-Item -ItemType Directory -Path $temp | Out-Null
try {
    [Environment]::SetEnvironmentVariable($secretName, ('test-only-' + [Guid]::NewGuid().ToString('N')), 'Process')
    [Environment]::SetEnvironmentVariable($wrongSecretName, 'wrong-test-password', 'Process')
    [Environment]::SetEnvironmentVariable($keySecretName, ('key-only-' + [Guid]::NewGuid().ToString('N')), 'Process')
    $keytool = Find-SigningJavaTool -Name 'keytool'
    $release = Join-Path $temp 'release.jks'
    $renamedDebug = Join-Path $temp 'renamed.jks'
    foreach ($definition in @(
        @{ Path = $release; Subject = 'CN=Release Signing Test,O=Tests,C=HU' },
        @{ Path = $renamedDebug; Subject = 'CN=Android Debug,O=Android,C=US' }
    )) {
        $result = Invoke-SigningToolCaptured -FilePath $keytool -Arguments @(
            '-genkeypair', '-keystore', $definition.Path, '-storetype', 'JKS', '-alias', 'release',
            '-keyalg', 'RSA', '-keysize', '2048', '-validity', '3', '-dname', $definition.Subject,
            '-storepass:env', $secretName, '-keypass:env', $secretName, '-noprompt'
        )
        Assert-SigningCheck ($result.ExitCode -eq 0) 'Generate a disposable certificate'
    }

    $fingerprint = Get-AndroidReleaseCertificate -KeyStore $release -KeyAlias 'release' -StorePassReference $reference
    Assert-SigningCheck ($fingerprint -cmatch '^[0-9A-F]{64}$') 'Read actual JKS certificate SHA-256'
    $pinned = Get-AndroidReleaseCertificate -KeyStore $release -KeyAlias 'release' -StorePassReference $reference -ExpectedCertificateSha256 $fingerprint
    Assert-SigningCheck ($pinned -ceq $fingerprint) 'Accept matching certificate pin'
    Assert-SigningFailure { Get-AndroidReleaseCertificate -KeyStore $release -KeyAlias 'release' -StorePassReference $reference -ExpectedCertificateSha256 ('0' * 64) } 'does not match'
    Assert-SigningFailure { Get-AndroidReleaseCertificate -KeyStore $renamedDebug -KeyAlias 'release' -StorePassReference $reference } 'Android debug certificate'
    Assert-SigningFailure { Get-AndroidReleaseCertificate -KeyStore $release -KeyAlias 'androiddebugkey' -StorePassReference $reference } 'debug key is forbidden'
    Assert-SigningFailure { Get-AndroidReleaseCertificate -KeyStore $release -KeyAlias 'release' -StorePassReference ('env:' + $wrongSecretName) } 'Could not read the release certificate'
    Assert-SigningFailure { Resolve-SigningPasswordReference 'plaintext-password' } 'plaintext MSBuild passwords are rejected'
    Assert-SigningFailure { Resolve-SigningPasswordReference ('env:' + $secretName + '_MISSING') } 'missing or empty'

    $keyHash = (Get-FileHash -LiteralPath $release -Algorithm SHA256).Hash
    Assert-AndroidReleasePrivateKey -KeyStore $release -KeyAlias 'release' -StorePassReference $reference -KeyPassReference $reference
    Assert-SigningCheck ((Get-FileHash -LiteralPath $release -Algorithm SHA256).Hash -eq $keyHash) 'Validate private-key access without changing the keystore'
    Assert-SigningFailure { Assert-AndroidReleasePrivateKey -KeyStore $release -KeyAlias 'release' -StorePassReference $reference -KeyPassReference ('env:' + $wrongSecretName) } 'private signing key could not be unlocked'
    $result = Invoke-SigningToolCaptured -FilePath $keytool -Arguments @(
        '-keypasswd', '-keystore', $release, '-alias', 'release',
        '-storepass:env', $secretName, '-keypass:env', $secretName, '-new:env', $keySecretName
    )
    if ($result.ExitCode -ne 0) { throw 'Could not prepare the disposable key with a distinct private-key password.' }
    Assert-AndroidReleasePrivateKey -KeyStore $release -KeyAlias 'release' -StorePassReference $reference -KeyPassReference ('env:' + $keySecretName)
    $unchangedIdentity = Get-AndroidReleaseCertificate -KeyStore $release -KeyAlias 'release' -StorePassReference $reference
    Assert-SigningCheck ($unchangedIdentity -ceq $fingerprint) 'Accept distinct store/key passwords with the same signing identity'
    Assert-SigningFailure { Assert-AndroidReleasePrivateKey -KeyStore $release -KeyAlias 'release' -StorePassReference $reference -KeyPassReference $reference } 'private signing key could not be unlocked'

    $validOutput = "Verifies`r`nNumber of signers: 1`r`nSigner #1 certificate SHA-256 digest: $($fingerprint.ToLowerInvariant())`r`n"
    Assert-AndroidApkSignerOutput -Output $validOutput -ExpectedCertificateSha256 $fingerprint
    Assert-SigningCheck $true 'Accept expected APK signer'
    Assert-SigningFailure { Assert-AndroidApkSignerOutput -Output $validOutput -ExpectedCertificateSha256 ('0' * 64) } 'does not match'
    Assert-SigningFailure { Assert-AndroidApkSignerOutput -Output 'No signer' -ExpectedCertificateSha256 $fingerprint } 'exactly one'
    Assert-SigningFailure { Assert-AndroidApkSignerOutput -Output ($validOutput + "Signer #2 certificate SHA-256 digest: $fingerprint") -ExpectedCertificateSha256 $fingerprint } 'exactly one'

    $rangeOutput = "Verifies`nNumber of signers: 1`n" +
        "Signer (minSdkVersion=33, maxSdkVersion=2147483647) certificate SHA-256 digest: $fingerprint`n" +
        "Signer (minSdkVersion=24, maxSdkVersion=32) certificate SHA-256 digest: $fingerprint`n"
    Assert-AndroidApkSignerOutput -Output $rangeOutput -ExpectedCertificateSha256 $fingerprint
    Assert-SigningCheck $true 'Accept SDK-range labels for the same authorized certificate'
    Assert-SigningFailure { Assert-AndroidApkSignerOutput -Output ($rangeOutput + "Signer (minSdkVersion=21, maxSdkVersion=23) certificate SHA-256 digest: $('0' * 64)") -ExpectedCertificateSha256 $fingerprint } 'does not match'
    Assert-SigningFailure { Assert-AndroidApkSignerOutput -Output $validOutput.Replace('Number of signers: 1', 'Number of signers: 2') -ExpectedCertificateSha256 $fingerprint } 'exactly one'
    Assert-SigningFailure { Assert-AndroidApkSignerOutput -Output "Number of signers: 1`nUnrecognized certificate format" -ExpectedCertificateSha256 $fingerprint } 'no readable signer certificate'
    Assert-AndroidApkSignerOutput -Output ($validOutput + "Source Stamp Signer certificate SHA-256 digest: $('0' * 64)") -ExpectedCertificateSha256 $fingerprint
    Assert-SigningCheck $true 'Do not confuse a source stamp certificate with the APK signer'

    # Build Tools 37 prints scheme-qualified labels instead of "Signer #1".
    # The certificate digest must be checked, not the adjacent public-key digest.
    $schemeOutput = "Verifies`r`nNumber of signers: 1`r`n" +
        "V3.0 Signer: certificate DN: CN=Release Signing Test`r`n" +
        "V3.0 Signer: certificate SHA-256 digest: $($fingerprint.ToLowerInvariant())`r`n" +
        "V3.0 Signer: public key SHA-256 digest: $('0' * 64)`r`n"
    Assert-AndroidApkSignerOutput -Output $schemeOutput -ExpectedCertificateSha256 $fingerprint
    Assert-SigningCheck $true 'Accept the actual Build Tools 37 V3.0 Signer certificate format'
    $multiSchemeOutput = $schemeOutput +
        "V1 Signer #1: certificate SHA-256 digest: $fingerprint`n" +
        "V2 Signer #1: certificate SHA-256 digest: $fingerprint`n" +
        "V3.1 Signer: certificate SHA-256 digest: $fingerprint`n" +
        "V3.2 Signer: certificate SHA-256 digest: $fingerprint`n" +
        "V4 Signer: certificate SHA-256 digest: $fingerprint`n"
    Assert-AndroidApkSignerOutput -Output $multiSchemeOutput -ExpectedCertificateSha256 $fingerprint
    Assert-SigningCheck $true 'Accept the same authorized signer reported under several signature schemes'
    Assert-SigningFailure { Assert-AndroidApkSignerOutput -Output ($schemeOutput + "V3.1 Signer: certificate SHA-256 digest: $('0' * 64)") -ExpectedCertificateSha256 $fingerprint } 'does not match'
    Assert-SigningFailure { Assert-AndroidApkSignerOutput -Output ($schemeOutput + "V2 Signer #2: certificate SHA-256 digest: $fingerprint") -ExpectedCertificateSha256 $fingerprint } 'exactly one'

    # Tool failures must expose the real diagnostic, not be mistaken for a bad
    # signer count. No signing credentials are passed to APK verification.
    $apkFixture = Join-Path $temp 'verification.apk'
    [IO.File]::WriteAllText($apkFixture, 'fixture')
    $capturedTool = (Get-Command Invoke-SigningToolCaptured).ScriptBlock
    try {
        function Invoke-SigningToolCaptured {
            param([string]$FilePath, [string[]]$Arguments)
            return [PSCustomObject]@{ ExitCode = 1; Output = 'Verifier fixture diagnostic' }
        }
        Assert-SigningFailure { Assert-AndroidReleaseApk -ApkPath $apkFixture -ExpectedCertificateSha256 $fingerprint -ApkSignerJar 'fixture-apksigner.jar' } 'Verifier fixture diagnostic'
        function Invoke-SigningToolCaptured {
            param([string]$FilePath, [string[]]$Arguments)
            return [PSCustomObject]@{ ExitCode = 0; Output = "Number of signers: 1`nUnrecognized certificate format" }
        }
        Assert-SigningFailure { Assert-AndroidReleaseApk -ApkPath $apkFixture -ExpectedCertificateSha256 $fingerprint -ApkSignerJar 'fixture-apksigner.jar' } 'Unrecognized certificate format'
    }
    finally { Set-Item Function:Invoke-SigningToolCaptured -Value $capturedTool }

    $product = Join-Path $temp 'product'
    New-Item -ItemType Directory -Path (Join-Path $product 'hu') -Force | Out-Null
    foreach ($name in @('PasswordManagerLocal.exe', 'PasswordManagerLocal.dll', 'PasswordManagerLocal.Windows.Agent.exe',
        'PasswordManagerLocal.Windows.Agent.dll', 'PasswordManagerLocal.Common.Backend.dll', 'hu/PasswordManagerLocal.Common.Frontend.resources.dll',
        'ConfigureWindowsFirewall.ps1', 'System.Runtime.dll', 'Avalonia.dll', 'ThirdParty.exe', 'ThirdParty.ps1')) {
        [IO.File]::WriteAllText((Join-Path $product $name), 'test input')
    }
    $owned = @(Get-WindowsOwnedSigningFiles -ProductDirectory $product)
    Assert-SigningCheck ($owned.Count -eq 7) 'Select both own executables, own DLLs, satellite and firewall script'
    Assert-SigningCheck (@($owned | Where-Object { $_.Name -in @('System.Runtime.dll', 'Avalonia.dll', 'ThirdParty.exe', 'ThirdParty.ps1') }).Count -eq 0) 'Preserve third-party files'
    Remove-Item -LiteralPath (Join-Path $product 'PasswordManagerLocal.Windows.Agent.dll')
    Assert-SigningFailure { Get-WindowsOwnedSigningFiles -ProductDirectory $product } 'Required signing input is missing'

    # Exercise Windows signing orchestration without a Windows certificate or
    # native signer. Only the external tool and OS signature inspection are faked.
    [IO.File]::WriteAllText((Join-Path $product 'PasswordManagerLocal.Windows.Agent.dll'), 'restored')
    $fakeTool = Join-Path $temp 'fake-signtool.ps1'
    [IO.File]::WriteAllText($fakeTool, '$global:LASTEXITCODE = 0')
    $context = [PSCustomObject]@{ SignTool = $fakeTool; Thumbprint = ('A' * 40); StoreLocation = 'CurrentUser'; TimestampUrl = 'https://example.invalid/timestamp' }
    $script:testSignature = [PSCustomObject]@{
        Status = 'Valid'; SignerCertificate = [PSCustomObject]@{ Thumbprint = $context.Thumbprint }
        TimeStamperCertificate = [PSCustomObject]@{ Subject = 'Test timestamp' }
    }
    function Get-AuthenticodeSignature { param([string]$LiteralPath) return $script:testSignature }
    $signatureReport = Join-Path $temp 'signatures.json'
    Invoke-WindowsProductSigning -ProductDirectory $product -Context $context -ReportPath $signatureReport
    $report = Get-Content -LiteralPath $signatureReport -Raw | ConvertFrom-Json
    Assert-SigningCheck ($report.Files.Count -eq 7) 'Record only verified application signatures'
    Assert-SigningCheck (@($report.Files | Where-Object { $_.Sha256 -notmatch '^[0-9A-F]{64}$' }).Count -eq 0) 'Record final SHA-256 hashes'
    $script:testSignature.TimeStamperCertificate = $null
    Assert-SigningFailure { Assert-WindowsReleaseSignature -Path $release -Context $context } 'valid, timestamped signature'
    $script:testSignature.TimeStamperCertificate = [PSCustomObject]@{ Subject = 'Test timestamp' }
    $script:testSignature.SignerCertificate.Thumbprint = 'B' * 40
    Assert-SigningFailure { Assert-WindowsReleaseSignature -Path $release -Context $context } 'valid, timestamped signature'
    [IO.File]::WriteAllText($fakeTool, '$global:LASTEXITCODE = 2')
    Assert-SigningFailure { Invoke-WindowsProductSigning -ProductDirectory $product -Context $context -ReportPath $signatureReport } 'Windows signing failed'
    Assert-SigningFailure { Assert-WindowsReleaseSignature -Path $release -Context $context } 'signature or timestamp verification failed'

    Write-Host "Passed $script:checks release-signing checks. No app restore/build/test was run."
}
finally {
    [Environment]::SetEnvironmentVariable($secretName, $null, 'Process')
    [Environment]::SetEnvironmentVariable($wrongSecretName, $null, 'Process')
    [Environment]::SetEnvironmentVariable($keySecretName, $null, 'Process')
    Remove-Item -LiteralPath $temp -Recurse -Force
}
