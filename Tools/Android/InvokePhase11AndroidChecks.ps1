[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string]$PackageName,
    [Parameter(Mandatory)] [string]$ActivityName,
    [string]$ApkPath,
    [string]$DeviceSerial,
    [ValidateRange(5, 300)] [int]$TimeoutSeconds = 30,
    [string]$OutputDirectory,
    [switch]$SendRestorationBroadcasts,
    [switch]$ForceStopAndRelaunch
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Resolve-Adb {
    $command = Get-Command 'adb' -ErrorAction SilentlyContinue
    if ($null -ne $command) {
        return $command.Source
    }

    foreach ($root in @($env:ANDROID_SDK_ROOT, $env:ANDROID_HOME)) {
        if ([string]::IsNullOrWhiteSpace($root)) {
            continue
        }

        $candidate = Join-Path $root 'platform-tools\adb.exe'
        if (Test-Path -LiteralPath $candidate -PathType Leaf) {
            return (Resolve-Path -LiteralPath $candidate).Path
        }
    }

    throw 'ADB was not found. Add Android SDK platform-tools to PATH or set ANDROID_SDK_ROOT.'
}

function Invoke-BoundedProcess(
    [string]$FilePath,
    [string[]]$Arguments,
    [int]$Timeout,
    [switch]$AllowFailure) {
    $startInfo = [Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $FilePath
    $startInfo.UseShellExecute = $false
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    foreach ($argument in $Arguments) {
        [void]$startInfo.ArgumentList.Add($argument)
    }

    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $startInfo
    try {
        if (-not $process.Start()) {
            throw "Could not start process: $FilePath"
        }

        $stdoutTask = $process.StandardOutput.ReadToEndAsync()
        $stderrTask = $process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit($Timeout * 1000)) {
            try { $process.Kill($true) } catch { }
            throw "Command timed out after $Timeout seconds: $FilePath $($Arguments -join ' ')"
        }

        $stdout = $stdoutTask.GetAwaiter().GetResult()
        $stderr = $stderrTask.GetAwaiter().GetResult()
        if ($process.ExitCode -ne 0 -and -not $AllowFailure) {
            throw "Command failed ($($process.ExitCode)): $FilePath $($Arguments -join ' ')`n$stdout`n$stderr"
        }

        return [pscustomobject]@{
            ExitCode = $process.ExitCode
            StandardOutput = $stdout
            StandardError = $stderr
        }
    } finally {
        $process.Dispose()
    }
}

function Invoke-Adb([string[]]$Arguments, [switch]$AllowFailure) {
    $allArguments = [System.Collections.Generic.List[string]]::new()
    if (-not [string]::IsNullOrWhiteSpace($script:SelectedDeviceSerial)) {
        $allArguments.Add('-s')
        $allArguments.Add($script:SelectedDeviceSerial)
    }
    foreach ($argument in $Arguments) {
        $allArguments.Add($argument)
    }

    return Invoke-BoundedProcess $script:AdbPath $allArguments.ToArray() $TimeoutSeconds -AllowFailure:$AllowFailure
}

function Save-Result([string]$Name, $Result) {
    $path = Join-Path $script:ResultsDirectory $Name
    @(
        "ExitCode: $($Result.ExitCode)",
        '',
        'STDOUT:',
        $Result.StandardOutput,
        '',
        'STDERR:',
        $Result.StandardError
    ) | Set-Content -LiteralPath $path -Encoding utf8
}

$script:AdbPath = Resolve-Adb
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path (Get-Location) ("phase11-android-checks-" + (Get-Date -Format 'yyyyMMdd-HHmmss'))
}
$script:ResultsDirectory = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $script:ResultsDirectory -Force | Out-Null

$deviceList = Invoke-BoundedProcess $script:AdbPath @('devices') $TimeoutSeconds
$availableDevices = @($deviceList.StandardOutput -split "`r?`n" | ForEach-Object {
    if ($_ -match '^([^\s]+)\s+device$') { $matches[1] }
} | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })

if ([string]::IsNullOrWhiteSpace($DeviceSerial)) {
    if ($availableDevices.Count -ne 1) {
        throw "Exactly one ready Android device is required when -DeviceSerial is omitted. Ready devices: $($availableDevices -join ', ')"
    }
    $script:SelectedDeviceSerial = $availableDevices[0]
} else {
    if ($availableDevices -notcontains $DeviceSerial) {
        throw "The requested device is not ready: $DeviceSerial"
    }
    $script:SelectedDeviceSerial = $DeviceSerial
}

if (-not [string]::IsNullOrWhiteSpace($ApkPath)) {
    if (-not (Test-Path -LiteralPath $ApkPath -PathType Leaf)) {
        throw "APK not found: $ApkPath"
    }
    Save-Result '01-install.txt' (Invoke-Adb @('install', '-r', (Resolve-Path -LiteralPath $ApkPath).Path))
}

Save-Result '02-clear-logcat.txt' (Invoke-Adb @('logcat', '-c'))
$component = "$PackageName/$ActivityName"
Save-Result '03-launch.txt' (Invoke-Adb @('shell', 'am', 'start', '-W', '-n', $component))
Start-Sleep -Seconds 2
Save-Result '04-process.txt' (Invoke-Adb @('shell', 'pidof', $PackageName) -AllowFailure)
Save-Result '05-services.txt' (Invoke-Adb @('shell', 'dumpsys', 'activity', 'services', $PackageName) -AllowFailure)
$notificationResult = Invoke-Adb @('shell', 'dumpsys', 'notification') -AllowFailure
$notificationLines = @($notificationResult.StandardOutput -split "`r?`n" | Where-Object {
    $_.Contains($PackageName, [StringComparison]::OrdinalIgnoreCase) -or
    $_.Contains('PasswordManager', [StringComparison]::OrdinalIgnoreCase)
})
$notificationLines | Set-Content -LiteralPath (Join-Path $script:ResultsDirectory '06-notifications.txt') -Encoding utf8
Save-Result '07-package.txt' (Invoke-Adb @('shell', 'dumpsys', 'package', $PackageName) -AllowFailure)

if ($SendRestorationBroadcasts) {
    Save-Result '08-boot-broadcast.txt' (Invoke-Adb @(
        'shell', 'am', 'broadcast',
        '-a', 'android.intent.action.BOOT_COMPLETED',
        '-p', $PackageName) -AllowFailure)
    Save-Result '09-package-replaced-broadcast.txt' (Invoke-Adb @(
        'shell', 'am', 'broadcast',
        '-a', 'android.intent.action.MY_PACKAGE_REPLACED',
        '-p', $PackageName) -AllowFailure)
}

if ($ForceStopAndRelaunch) {
    Save-Result '10-force-stop.txt' (Invoke-Adb @('shell', 'am', 'force-stop', $PackageName))
    Save-Result '11-relaunch.txt' (Invoke-Adb @('shell', 'am', 'start', '-W', '-n', $component))
    Start-Sleep -Seconds 2
    Save-Result '12-services-after-relaunch.txt' (Invoke-Adb @('shell', 'dumpsys', 'activity', 'services', $PackageName) -AllowFailure)
}

$logcat = Invoke-Adb @('logcat', '-d', '-v', 'threadtime') -AllowFailure
$filteredLog = @($logcat.StandardOutput -split "`r?`n" | Where-Object {
    $_.Contains($PackageName, [StringComparison]::OrdinalIgnoreCase) -or
    $_.Contains('PasswordManager', [StringComparison]::OrdinalIgnoreCase)
})
$filteredLog | Set-Content -LiteralPath (Join-Path $script:ResultsDirectory '13-filtered-logcat.txt') -Encoding utf8

@(
    "DeviceSerial=$script:SelectedDeviceSerial",
    "PackageName=$PackageName",
    "ActivityName=$ActivityName",
    "TimeoutSeconds=$TimeoutSeconds",
    "CompletedAt=$([DateTimeOffset]::Now.ToString('O'))"
) | Set-Content -LiteralPath (Join-Path $script:ResultsDirectory 'summary.txt') -Encoding utf8

Write-Host "Phase 11 Android diagnostics completed: $script:ResultsDirectory"
