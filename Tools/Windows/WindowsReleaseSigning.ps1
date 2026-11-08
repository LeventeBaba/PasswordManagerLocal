#Requires -Version 5.1
. (Join-Path $PSScriptRoot '..\Signing\ReleaseSigning.Common.ps1')

function Get-WindowsReleaseSigningContext {
    $settings = Get-ReleaseSigningSettings
    if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT) { throw 'Windows Authenticode publishing requires Windows.' }
    $thumbprint = $settings.WindowsCertificateThumbprint.Replace(' ', '').ToUpperInvariant()
    if ($thumbprint -notmatch '^[0-9A-F]{40}$') {
        throw 'Windows publishing requires a code-signing certificate. Set WindowsCertificateThumbprint in PublishKeys\Signing.local.json. The Android .jks cannot sign Windows binaries. See Docs\RELEASE_SIGNING.md.'
    }
    if ($settings.WindowsCertificateStoreLocation -notin @('CurrentUser', 'LocalMachine')) { throw 'WindowsCertificateStoreLocation must be CurrentUser or LocalMachine.' }
    $timestampUri = $null
    if (-not [Uri]::TryCreate($settings.WindowsTimestampUrl, [UriKind]::Absolute, [ref]$timestampUri) -or $timestampUri.Scheme -notin @('http', 'https')) {
        throw 'Set WindowsTimestampUrl to your certificate provider RFC 3161 timestamp service in PublishKeys\Signing.local.json.'
    }
    $certificate = Get-Item -LiteralPath "Cert:\$($settings.WindowsCertificateStoreLocation)\My\$thumbprint" -ErrorAction Stop
    if (-not $certificate.HasPrivateKey) { throw 'The Windows certificate has no accessible private key. Install or connect the provider token and its software.' }
    if ($certificate.NotBefore -gt (Get-Date) -or $certificate.NotAfter -le (Get-Date)) { throw 'The Windows signing certificate is not currently valid.' }
    $codeSigningUsage = @($certificate.Extensions | Where-Object {
        $_ -is [Security.Cryptography.X509Certificates.X509EnhancedKeyUsageExtension]
    } | ForEach-Object { $_.EnhancedKeyUsages } | Where-Object { $_.Value -eq '1.3.6.1.5.5.7.3.3' })
    if ($codeSigningUsage.Count -eq 0) {
        throw 'The Windows certificate must explicitly permit Code Signing.'
    }
    $chain = [Security.Cryptography.X509Certificates.X509Chain]::new()
    try {
        $chain.ChainPolicy.RevocationMode = [Security.Cryptography.X509Certificates.X509RevocationMode]::Online
        $null = $chain.ChainPolicy.ApplicationPolicy.Add([Security.Cryptography.Oid]::new('1.3.6.1.5.5.7.3.3'))
        $chain.ChainPolicy.UrlRetrievalTimeout = [TimeSpan]::FromSeconds(30)
        if (-not $chain.Build($certificate)) { throw 'The Windows certificate trust or revocation check failed. A trusted code-signing certificate is required.' }
    }
    finally { $chain.Dispose() }

    $signTool = $settings.WindowsSignToolPath
    if (-not $signTool) {
        $command = Get-Command signtool.exe -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($command) { $signTool = $command.Source }
    }
    if (-not $signTool -and ${env:ProgramFiles(x86)}) {
        $sdkBin = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\bin'
        if (Test-Path -LiteralPath $sdkBin) {
            foreach ($version in (Get-ChildItem -LiteralPath $sdkBin -Directory | Sort-Object {
                try { [version]$_.Name } catch { [version]'0.0' }
            } -Descending)) {
                $candidate = Join-Path $version.FullName 'x64\signtool.exe'
                if (Test-Path -LiteralPath $candidate -PathType Leaf) { $signTool = $candidate; break }
            }
        }
    }
    if (-not $signTool -or -not (Test-Path -LiteralPath $signTool -PathType Leaf)) { throw 'SignTool was not found. Install Windows SDK Signing Tools or set WindowsSignToolPath.' }
    return [PSCustomObject]@{
        SignTool = $signTool
        Thumbprint = $thumbprint
        StoreLocation = $settings.WindowsCertificateStoreLocation
        TimestampUrl = $settings.WindowsTimestampUrl
    }
}

function Get-WindowsOwnedSigningFiles {
    param(
        [Parameter(Mandatory = $true)][string]$ProductDirectory,
        [switch]$UnifiedNativeAot
    )
    $requiredFiles = @('PasswordManagerLocal.exe')
    if (-not $UnifiedNativeAot) {
        $requiredFiles += @('PasswordManagerLocal.dll', 'PasswordManagerLocal.Windows.Agent.exe', 'PasswordManagerLocal.Windows.Agent.dll')
    }
    foreach ($required in $requiredFiles) {
        if (-not (Test-Path -LiteralPath (Join-Path $ProductDirectory $required) -PathType Leaf)) { throw "Required signing input is missing: $required" }
    }
    return @(Get-ChildItem -LiteralPath $ProductDirectory -Recurse -File | Where-Object {
        $_.Extension -in @('.dll', '.exe') -and $_.BaseName -match '^PasswordManagerLocal(?:\.|$)'
    } | Sort-Object FullName)
}

function Assert-WindowsReleaseSignature {
    param([Parameter(Mandatory = $true)][string]$Path, [Parameter(Mandatory = $true)][PSCustomObject]$Context)
    & $Context.SignTool verify /pa /all /tw $Path | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "Windows signature or timestamp verification failed: $Path" }
    $signature = Get-AuthenticodeSignature -LiteralPath $Path
    if ($signature.Status -ne 'Valid' -or $null -eq $signature.SignerCertificate -or
        $signature.SignerCertificate.Thumbprint -ine $Context.Thumbprint -or $null -eq $signature.TimeStamperCertificate) {
        throw "The Windows file must have a valid, timestamped signature from the configured publisher: $Path"
    }
}

function Invoke-WindowsReleaseFileSigning {
    param([Parameter(Mandatory = $true)][string]$Path, [Parameter(Mandatory = $true)][PSCustomObject]$Context)

    $arguments = @('sign', '/sha1', $Context.Thumbprint, '/s', 'My', '/fd', 'SHA256', '/tr', $Context.TimestampUrl, '/td', 'SHA256')
    if ($Context.StoreLocation -eq 'LocalMachine') { $arguments += '/sm' }
    $arguments += $Path
    & $Context.SignTool @arguments | Out-Host
    # Warnings, including failed timestamping, are failures too (SignTool exit 2).
    if ($LASTEXITCODE -ne 0) { throw "Windows signing failed: $Path" }
    Assert-WindowsReleaseSignature -Path $Path -Context $Context
}

function Invoke-WindowsProductSigning {
    param(
        [Parameter(Mandatory = $true)][string]$ProductDirectory,
        [Parameter(Mandatory = $true)][PSCustomObject]$Context,
        [Parameter(Mandatory = $true)][string]$ReportPath,
        [switch]$UnifiedNativeAot
    )
    $files = @(Get-WindowsOwnedSigningFiles -ProductDirectory $ProductDirectory -UnifiedNativeAot:$UnifiedNativeAot)
    $entries = @()
    foreach ($file in $files) {
        Invoke-WindowsReleaseFileSigning -Path $file.FullName -Context $Context
        $entries += [PSCustomObject]@{
            File = $file.FullName.Substring([IO.Path]::GetFullPath($ProductDirectory).TrimEnd('\', '/').Length + 1).Replace('\', '/')
            CertificateThumbprint = $Context.Thumbprint
            Sha256 = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash
        }
    }
    $report = [PSCustomObject]@{ SigningMode = 'Signed'; CertificateThumbprint = $Context.Thumbprint; Files = $entries }
    [IO.File]::WriteAllText($ReportPath, ($report | ConvertTo-Json -Depth 5), [Text.UTF8Encoding]::new($false))
    Write-Host "Verified Windows publisher signatures on $($files.Count) application files."
}
