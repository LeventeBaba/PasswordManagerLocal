[CmdletBinding()]
param(
    [string]$RepositoryRoot
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if ([string]::IsNullOrWhiteSpace($RepositoryRoot)) {
    $RepositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
} else {
    $RepositoryRoot = [IO.Path]::GetFullPath($RepositoryRoot)
}

$failures = [System.Collections.Generic.List[string]]::new()

function Assert-Source([bool]$Condition, [string]$Message) {
    if (-not $Condition) {
        $script:failures.Add($Message)
    }
}

function Read-RequiredText([string]$RelativePath) {
    $path = Join-Path $RepositoryRoot $RelativePath
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        $script:failures.Add("Required source file is missing: $RelativePath")
        return ''
    }

    return Get-Content -LiteralPath $path -Raw
}

$manifestRelativePath = 'Android\Frontend\Properties\AndroidManifest.xml'
$manifestPath = Join-Path $RepositoryRoot $manifestRelativePath
$manifestText = Read-RequiredText $manifestRelativePath
$mainActivityText = Read-RequiredText 'Android\Frontend\MainActivity.cs'
$serviceText = Read-RequiredText 'Android\Frontend\Runtime\PasswordManagerBackgroundService.cs'
$receiverText = Read-RequiredText 'Android\Frontend\Background\AndroidBackgroundRestorationReceiver.cs'
$connectorText = Read-RequiredText 'Android\Frontend\Runtime\AndroidRuntimeServiceConnector.cs'
$foregroundControllerText = Read-RequiredText 'Android\Frontend\Runtime\AndroidForegroundServiceController.cs'
$runtimeHostText = Read-RequiredText 'Android\Runtime\AndroidRuntimeServiceHost.cs'

if (-not [string]::IsNullOrWhiteSpace($manifestText)) {
    try {
        [xml]$manifestXml = $manifestText
        Assert-Source ($null -ne $manifestXml.manifest.application) 'Android manifest has no application element.'
    } catch {
        $failures.Add("Android manifest is not valid XML: $($_.Exception.Message)")
    }
}

foreach ($permission in @(
    'android.permission.INTERNET',
    'android.permission.FOREGROUND_SERVICE',
    'android.permission.FOREGROUND_SERVICE_CONNECTED_DEVICE',
    'android.permission.POST_NOTIFICATIONS',
    'android.permission.RECEIVE_BOOT_COMPLETED'
)) {
    Assert-Source ($manifestText.Contains($permission, [StringComparison]::Ordinal)) `
        "Android manifest is missing permission: $permission"
}

foreach ($fragment in @(
    'PasswordManagerBackgroundService',
    'android:exported="false"',
    'android:foregroundServiceType="connectedDevice"',
    'android:stopWithTask="false"',
    'AndroidBackgroundRestorationReceiver',
    'android.intent.action.BOOT_COMPLETED',
    'android.intent.action.MY_PACKAGE_REPLACED'
)) {
    Assert-Source ($manifestText.Contains($fragment, [StringComparison]::Ordinal)) `
        "Android manifest is missing required declaration: $fragment"
}

Assert-Source (-not $manifestText.Contains('android.intent.action.USER_UNLOCKED', [StringComparison]::Ordinal)) `
    'USER_UNLOCKED must remain a dynamically registered receiver action, not a manifest service-start action.'
Assert-Source (-not $manifestText.Contains('android.intent.action.LOCKED_BOOT_COMPLETED', [StringComparison]::Ordinal)) `
    'LOCKED_BOOT_COMPLETED must not start the credential-protected runtime.'

Assert-Source ($mainActivityText.Contains('MainLauncher = true', [StringComparison]::Ordinal)) `
    'MainActivity is not marked as the launcher activity.'
Assert-Source ($mainActivityText.Contains('Exported = true', [StringComparison]::Ordinal)) `
    'MainActivity is not exported for launcher activation.'
Assert-Source (-not $mainActivityText.Contains('AndroidBackendRuntimeFactory', [StringComparison]::Ordinal)) `
    'MainActivity must not construct the Android backend runtime.'

Assert-Source ($serviceText.Contains('AndroidRuntimeServiceHost', [StringComparison]::Ordinal)) `
    'The foreground service does not own AndroidRuntimeServiceHost.'
Assert-Source ($serviceText.Contains('RegisterDeferredUnlockReceiver', [StringComparison]::Ordinal)) `
    'The service does not dynamically register deferred unlock restoration.'
Assert-Source ($receiverText.Contains('BootCompleted', [StringComparison]::Ordinal) -or
               $receiverText.Contains('ActionBootCompleted', [StringComparison]::Ordinal)) `
    'The restoration receiver does not handle boot completion.'

Assert-Source ($connectorText.Contains('ServiceBindTimeout', [StringComparison]::Ordinal)) `
    'Activity-to-service attachment has no named bounded timeout.'
Assert-Source ($connectorText.Contains('CancelAfter(ServiceBindTimeout)', [StringComparison]::Ordinal)) `
    'Activity-to-service attachment does not apply its timeout.'
Assert-Source ($connectorText.Contains('WaitForServiceAsync(timeoutSource.Token)', [StringComparison]::Ordinal)) `
    'Service binding does not use the bounded attachment token.'
Assert-Source ($connectorText.Contains('AttachInteractiveClientAsync(timeoutSource.Token)', [StringComparison]::Ordinal)) `
    'Interactive attachment does not use the bounded attachment token.'

Assert-Source ($foregroundControllerText.Contains('ic_stat_password_manager', [StringComparison]::OrdinalIgnoreCase)) `
    'The foreground notification does not reference the dedicated status icon.'
Assert-Source ($runtimeHostText.Contains('ResetDatabaseAndAcquireAsync', [StringComparison]::Ordinal)) `
    'Android database reset is not coordinated through the runtime lifetime owner.'
Assert-Source ($runtimeHostText.Contains('EnterForeground', [StringComparison]::Ordinal)) `
    'Android runtime host does not restore foreground state through the foreground controller.'
Assert-Source ($runtimeHostText.Contains('_runtimeUnsafe', [StringComparison]::Ordinal)) `
    'Android runtime host has no fail-closed unsafe-process state.'

foreach ($resourceRelativePath in @(
    'Android\Frontend\Resources\drawable\ic_stat_password_manager.xml',
    'Android\Frontend\Resources\drawable\icon.png',
    'Android\Frontend\Resources\xml\file_paths.xml'
)) {
    Assert-Source (Test-Path -LiteralPath (Join-Path $RepositoryRoot $resourceRelativePath) -PathType Leaf) `
        "Required Android resource is missing: $resourceRelativePath"
}

if ($failures.Count -gt 0) {
    $message = "Android manifest/resource verification failed:`n - " + ($failures -join "`n - ")
    throw $message
}

Write-Host 'Android manifest and source-resource invariants are valid.'
Write-Host "Manifest: $manifestPath"
