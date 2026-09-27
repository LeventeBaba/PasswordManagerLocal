[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string]$PackagePath,
    [string]$Aapt2Path,
    [string]$ApkAnalyzerPath,
    [string]$BundletoolJar,
    [string]$JavaPath = 'java',
    [string]$StringsToolPath,
    [string]$ManagedAssemblyInventoryPath,
    [string[]]$ExpectedAbis = @('arm64-v8a')
)

$ErrorActionPreference = 'Stop'
$failures = [System.Collections.Generic.List[string]]::new()

function Assert-Package([bool]$Condition, [string]$Message) {
    if (-not $Condition) { $script:failures.Add($Message) }
}

function Resolve-OptionalTool([string]$ExplicitPath, [string]$CommandName) {
    if (-not [string]::IsNullOrWhiteSpace($ExplicitPath)) {
        if (-not (Test-Path -LiteralPath $ExplicitPath -PathType Leaf)) {
            throw "Tool not found: $ExplicitPath"
        }

        return (Resolve-Path -LiteralPath $ExplicitPath).Path
    }

    $command = Get-Command $CommandName -ErrorAction SilentlyContinue
    if ($null -ne $command) { return $command.Source }
    return $null
}

function Get-ManifestContext([string]$Text, [string]$ComponentName, [int]$Radius = 16) {
    $lines = @($Text -split "`r?`n")
    $index = -1
    for ($i = 0; $i -lt $lines.Count; $i++) {
        if ($lines[$i].Contains($ComponentName)) {
            $index = $i
            break
        }
    }

    if ($index -lt 0) { return '' }
    $start = [Math]::Max(0, $index - $Radius)
    $end = [Math]::Min($lines.Count - 1, $index + $Radius)
    return ($lines[$start..$end] -join "`n")
}

function Invoke-Checked([string]$FilePath, [string[]]$Arguments) {
    $output = & $FilePath @Arguments 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "Tool failed ($LASTEXITCODE): $FilePath $($Arguments -join ' ')`n$output"
    }

    return ($output -join "`n")
}

function Get-BlobAssemblyInventory(
    [string]$BlobPath,
    [string]$StringsPath) {
    $matches = @(& $StringsPath $BlobPath 2>&1 | Where-Object {
        $_ -match '(?i)(PasswordManagerLocal|\.Tests?\.dll|Test\.dll)'
    })
    if ($LASTEXITCODE -ne 0) {
        throw "Strings tool failed ($LASTEXITCODE): $StringsPath $BlobPath"
    }

    return ($matches -join "`n")
}

if (-not (Test-Path -LiteralPath $PackagePath)) {
    throw "Package input not found: $PackagePath"
}

$item = Get-Item -LiteralPath $PackagePath
$entries = @()
$manifestText = $null

if ($item.PSIsContainer) {
    $entries = @(Get-ChildItem -LiteralPath $item.FullName -Recurse -File | ForEach-Object {
        [IO.Path]::GetRelativePath($item.FullName, $_.FullName).Replace('\', '/')
    })
    $plainManifest = Join-Path $item.FullName 'AndroidManifest.xml'
    if (Test-Path -LiteralPath $plainManifest -PathType Leaf) {
        $manifestText = Get-Content -LiteralPath $plainManifest -Raw
    }
} else {
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [IO.Compression.ZipFile]::OpenRead($item.FullName)
    try {
        $entries = @($archive.Entries | ForEach-Object FullName)
    } finally {
        $archive.Dispose()
    }

    switch ($item.Extension.ToLowerInvariant()) {
        '.apk' {
            $apkanalyzer = Resolve-OptionalTool $ApkAnalyzerPath 'apkanalyzer'
            $aapt2 = Resolve-OptionalTool $Aapt2Path 'aapt2'
            if ($null -ne $apkanalyzer) {
                $manifestText = Invoke-Checked $apkanalyzer @('manifest', 'print', $item.FullName)
            } elseif ($null -ne $aapt2) {
                $manifestText = Invoke-Checked $aapt2 @(
                    'dump',
                    'xmltree',
                    $item.FullName,
                    '--file',
                    'AndroidManifest.xml')
            } else {
                throw 'APK manifest verification requires apkanalyzer or aapt2. Supply -ApkAnalyzerPath or -Aapt2Path, or add one tool to PATH.'
            }
        }
        '.aab' {
            if ([string]::IsNullOrWhiteSpace($BundletoolJar) -or
                -not (Test-Path -LiteralPath $BundletoolJar -PathType Leaf)) {
                throw 'AAB manifest verification requires -BundletoolJar.'
            }

            $manifestText = Invoke-Checked $JavaPath @(
                '-jar',
                $BundletoolJar,
                'dump',
                'manifest',
                "--bundle=$($item.FullName)",
                '--module=base')
        }
        default {
            throw 'PackagePath must be an APK, AAB, or extracted package directory.'
        }
    }
}

if ([string]::IsNullOrWhiteSpace($manifestText)) {
    throw 'A readable decoded manifest was not available. For an extracted directory, provide a text AndroidManifest.xml; for APK/AAB input, provide the required Android SDK tool.'
}

foreach ($fragment in @(
    'PasswordManagerBackgroundService',
    'AndroidBackgroundRestorationReceiver',
    'MainActivity',
    'android.permission.FOREGROUND_SERVICE',
    'android.permission.FOREGROUND_SERVICE_CONNECTED_DEVICE',
    'android.permission.POST_NOTIFICATIONS',
    'android.permission.RECEIVE_BOOT_COMPLETED')) {
    Assert-Package ($manifestText.Contains($fragment)) "Packaged manifest is missing: $fragment"
}

foreach ($action in @('BOOT_COMPLETED', 'MY_PACKAGE_REPLACED')) {
    Assert-Package ($manifestText.Contains($action)) "Packaged manifest is missing restoration action: $action"
}
Assert-Package (-not $manifestText.Contains('USER_UNLOCKED')) `
    'Packaged manifest must not declare USER_UNLOCKED as a service-start receiver action.'
Assert-Package (-not $manifestText.Contains('LOCKED_BOOT_COMPLETED')) `
    'Packaged manifest unexpectedly declares LOCKED_BOOT_COMPLETED.'

$serviceContext = Get-ManifestContext $manifestText 'PasswordManagerBackgroundService'
$receiverContext = Get-ManifestContext $manifestText 'AndroidBackgroundRestorationReceiver'
$activityContext = Get-ManifestContext $manifestText 'MainActivity' 24
Assert-Package ($serviceContext -match '(?i)exported[^\r\n]*(false|0x0)') `
    'Packaged runtime service is not verifiably non-exported.'
Assert-Package ($serviceContext -match '(?i)foregroundServiceType[^\r\n]*(connectedDevice|0x10)') `
    'Packaged runtime service type is not verifiably connectedDevice.'
Assert-Package ($receiverContext -match '(?i)exported[^\r\n]*(false|0x0)') `
    'Packaged restoration receiver is not verifiably non-exported.'
Assert-Package ($activityContext -match '(?i)exported[^\r\n]*(true|0xffffffff)') `
    'Packaged MainActivity is not verifiably exported for launcher use.'
Assert-Package ($activityContext -match '(?i)android.intent.action.MAIN|\bMAIN\b') `
    'Packaged MainActivity is not verifiably associated with the MAIN action.'
Assert-Package ($activityContext -match '(?i)android.intent.category.LAUNCHER|\bLAUNCHER\b') `
    'Packaged MainActivity is not verifiably associated with the LAUNCHER category.'

Assert-Package (($entries | Where-Object {
    $_ -match 'ic_stat_password_manager'
}).Count -gt 0) 'Notification status icon is not packaged.'
Assert-Package (($entries | Where-Object {
    $_ -match '(FaultInjection|DiagnosticBuild|HiddenTestMode).*\.dll$'
}).Count -eq 0) 'A debug/test helper assembly appears in the package.'

$directRuntimeOwnerEntries = @($entries | Where-Object {
    [IO.Path]::GetFileName($_) -ieq 'PasswordManagerLocal.Android.Runtime.dll'
})
Assert-Package ($directRuntimeOwnerEntries.Count -le 1) `
    "Multiple direct Android runtime-owner assembly paths were found: $($directRuntimeOwnerEntries -join ', ')"

$managedInventoryText = (($entries | Where-Object { $_ -match '(?i)\.dll$' }) -join "`n")
$assemblyBlobEntries = @($entries | Where-Object {
    $_ -match '(?i)(^|/)libassemblies[^/]*\.blob\.so$'
})
if (-not [string]::IsNullOrWhiteSpace($ManagedAssemblyInventoryPath)) {
    if (-not (Test-Path -LiteralPath $ManagedAssemblyInventoryPath -PathType Leaf)) {
        throw "Managed assembly inventory not found: $ManagedAssemblyInventoryPath"
    }

    $managedInventoryText += "`n" + (Get-Content -LiteralPath $ManagedAssemblyInventoryPath -Raw)
} elseif ($assemblyBlobEntries.Count -gt 0) {
    $strings = Resolve-OptionalTool $StringsToolPath 'strings'
    if ($null -eq $strings) {
        throw 'Managed assemblies are stored in libassemblies.*.blob.so. Supply -StringsToolPath (GNU strings or llvm-strings) or -ManagedAssemblyInventoryPath to verify assembly names.'
    }

    $temporaryDirectory = Join-Path ([IO.Path]::GetTempPath()) ("phase11-package-" + [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $temporaryDirectory -Force | Out-Null
    $blobArchive = $null
    try {
        if (-not $item.PSIsContainer) {
            $blobArchive = [IO.Compression.ZipFile]::OpenRead($item.FullName)
        }

        foreach ($entryName in $assemblyBlobEntries) {
            if ($item.PSIsContainer) {
                $blobPath = Join-Path $item.FullName ($entryName.Replace('/', [IO.Path]::DirectorySeparatorChar))
            } else {
                $entry = $blobArchive.GetEntry($entryName)
                if ($null -eq $entry) { throw "Package entry disappeared while reading: $entryName" }
                $blobPath = Join-Path $temporaryDirectory ([IO.Path]::GetFileName($entryName))
                $input = $entry.Open()
                $output = [IO.File]::Create($blobPath)
                try {
                    $input.CopyTo($output)
                } finally {
                    $output.Dispose()
                    $input.Dispose()
                }
            }

            $managedInventoryText += "`n" + (Get-BlobAssemblyInventory $blobPath $strings)
        }
    } finally {
        if ($null -ne $blobArchive) { $blobArchive.Dispose() }
        Remove-Item -LiteralPath $temporaryDirectory -Recurse -Force -ErrorAction SilentlyContinue
    }
}

foreach ($assemblyName in @(
    'PasswordManagerLocal.Android.Frontend.dll',
    'PasswordManagerLocal.Android.Runtime.dll',
    'PasswordManagerLocal.Android.Backend.dll')) {
    Assert-Package ($managedInventoryText -match [regex]::Escape($assemblyName)) `
        "Managed assembly is not verifiably packaged: $assemblyName"
}
Assert-Package ($managedInventoryText -notmatch '(?im)(^|[/\\])[^/\\\r\n]*\.Tests?\.dll($|\s)') `
    'A test assembly is present in the managed assembly inventory.'
Assert-Package ($managedInventoryText -notmatch '(?i)(FaultInjection|DiagnosticBuild|HiddenTestMode)[^\r\n]*\.dll') `
    'A debug/test helper assembly is present in the managed assembly inventory.'

foreach ($abi in $ExpectedAbis) {
    $abiEntries = @($entries | Where-Object {
        $_ -match "(^|/)lib/$([regex]::Escape($abi))/"
    })
    Assert-Package ($abiEntries.Count -gt 0) "No native libraries were found for expected ABI: $abi"
    Assert-Package (($abiEntries | Where-Object {
        $_ -match '(sqlite|sqlcipher|e_sqlcipher)'
    }).Count -gt 0) "No SQLite/SQLCipher native dependency was found for ABI: $abi"
}

foreach ($abi in $ExpectedAbis) {
    $abiAssemblyBlobs = @($assemblyBlobEntries | Where-Object {
        $_ -match "(^|/)lib/$([regex]::Escape($abi))/libassemblies[^/]*\.blob\.so$"
    })
    if ($assemblyBlobEntries.Count -gt 0) {
        Assert-Package ($abiAssemblyBlobs.Count -eq 1) `
            "Expected one managed-assembly blob for ABI $abi; found $($abiAssemblyBlobs.Count)."
    }
}

$duplicateEntries = @($entries | Group-Object | Where-Object Count -gt 1)
Assert-Package ($duplicateEntries.Count -eq 0) 'The package contains duplicate archive entry paths.'

if ($failures.Count -gt 0) {
    $failures | ForEach-Object { Write-Error $_ }
    exit 1
}

Write-Host 'Android package verification passed.'
exit 0
