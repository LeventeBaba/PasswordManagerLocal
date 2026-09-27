[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('Hold', 'Probe')]
    [string]$Mode,

    [string]$LockPath = (Join-Path $env:LOCALAPPDATA 'PasswordManagerLocal\phase9-diagnostics\cross-session.lock')
)

$ErrorActionPreference = 'Stop'
$isWindowsPlatform = [Environment]::OSVersion.Platform -eq [PlatformID]::Win32NT
if (-not $isWindowsPlatform) {
    throw 'This diagnostic requires Windows.'
}
if ([string]::IsNullOrWhiteSpace($env:LOCALAPPDATA)) {
    throw 'LOCALAPPDATA is unavailable.'
}

$LockPath = [System.IO.Path]::GetFullPath($LockPath)
New-Item -ItemType Directory -Path (Split-Path -Parent $LockPath) -Force | Out-Null

try {
    $stream = [System.IO.File]::Open(
        $LockPath,
        [System.IO.FileMode]::OpenOrCreate,
        [System.IO.FileAccess]::ReadWrite,
        [System.IO.FileShare]::None)
}
catch [System.IO.IOException] {
    Write-Host "HELD: $LockPath"
    exit 2
}

if ($Mode -eq 'Probe') {
    $stream.Dispose()
    Write-Host "FREE: $LockPath"
    exit 0
}

try {
    Write-Host "HOLDING: $LockPath"
    Write-Host 'Run this script with -Mode Probe in another Windows session for the same user.'
    [void](Read-Host 'Press Enter to release the diagnostic lock')
}
finally {
    $stream.Dispose()
}
