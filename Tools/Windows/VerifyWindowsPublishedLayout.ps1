[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$PublishDirectory,

    [ValidateSet('win-x64')]
    [string]$RuntimeIdentifier = 'win-x64',

    [string]$RepositoryRoot
)

$ErrorActionPreference = 'Stop'
$publish = [System.IO.Path]::GetFullPath($PublishDirectory)
if (-not (Test-Path -LiteralPath $publish -PathType Container)) { throw "Publish directory is missing: $publish" }

function Assert-Files([string[]]$Names) {
    foreach ($name in $Names) {
        if (-not (Test-Path -LiteralPath (Join-Path $publish $name) -PathType Leaf)) {
            throw "Required final product file is missing: $name"
        }
    }
}

Assert-Files @(
    'PasswordManagerLocal.exe',
    'PasswordManagerLocal.dll',
    'PasswordManagerLocal.deps.json',
    'PasswordManagerLocal.runtimeconfig.json',
    'PasswordManagerLocal.Windows.Agent.exe',
    'PasswordManagerLocal.Windows.Agent.dll',
    'PasswordManagerLocal.Windows.Agent.deps.json',
    'PasswordManagerLocal.Windows.Agent.runtimeconfig.json',
    'PasswordManagerLocal.Common.Frontend.dll',
    'PasswordManagerLocal.Common.Backend.dll',
    'PasswordManagerLocal.Common.Backend.Hosting.dll',
    'PasswordManagerLocal.Common.Contracts.dll',
    'PasswordManagerLocal.Common.Preferences.dll',
    'PasswordManagerLocal.Windows.Backend.dll',
    'PasswordManagerLocal.Windows.EndpointRpc.Contracts.dll',
    'PasswordManagerLocal.Windows.EndpointRpc.Client.dll',
    'PasswordManagerLocal.Windows.EndpointRpc.Server.dll',
    'PasswordManagerLocal.Windows.Ipc.dll',
    'coreclr.dll',
    'hostfxr.dll',
    'hostpolicy.dll',
    'Avalonia.Win32.dll',
    'Avalonia.Skia.dll',
    'Microsoft.EntityFrameworkCore.Sqlite.dll',
    'Microsoft.Data.Sqlite.dll',
    'SQLitePCLRaw.core.dll',
    'SQLitePCLRaw.batteries_v2.dll',
    'SQLitePCLRaw.provider.e_sqlcipher.dll',
    'e_sqlcipher.dll',
    'NSec.Cryptography.dll',
    'libsodium.dll',
    'Google.Protobuf.dll',
    'Assets\app_icon.ico'
)

if (Test-Path -LiteralPath (Join-Path $publish 'AgentRuntime')) {
    throw 'The prohibited legacy AgentRuntime directory is present.'
}

foreach ($executable in @('PasswordManagerLocal.exe', 'PasswordManagerLocal.Windows.Agent.exe')) {
    $matches = @(Get-ChildItem -LiteralPath $publish -Recurse -File -Filter $executable)
    if ($matches.Count -ne 1 -or -not [string]::Equals($matches[0].DirectoryName, $publish, [StringComparison]::OrdinalIgnoreCase)) {
        throw "$executable must exist exactly once in the product root."
    }
}

$frontendDeps = Get-Content -LiteralPath (Join-Path $publish 'PasswordManagerLocal.deps.json') -Raw
$agentDeps = Get-Content -LiteralPath (Join-Path $publish 'PasswordManagerLocal.Windows.Agent.deps.json') -Raw
foreach ($name in @('PasswordManagerLocal.Common.Backend', 'PasswordManagerLocal.Windows.Backend', 'PasswordManagerLocal.Windows.EndpointRpc.Server', 'Microsoft.EntityFrameworkCore', 'Microsoft.Data.Sqlite', 'SQLitePCLRaw')) {
    if ($frontendDeps.Contains($name)) { throw "Frontend manifest contains backend-only dependency: $name" }
}
foreach ($name in @('PasswordManagerLocal.Common.Frontend', 'Avalonia', 'ReactiveUI', 'System.Windows.Forms', 'PresentationFramework', 'WindowsBase')) {
    if ($agentDeps.Contains($name)) { throw "Agent manifest contains frontend/desktop dependency: $name" }
}
foreach ($name in @('PasswordManagerLocal.Common.Backend', 'PasswordManagerLocal.Windows.EndpointRpc.Server', 'Microsoft.EntityFrameworkCore.Sqlite')) {
    if (-not $agentDeps.Contains($name)) { throw "Agent manifest is missing required dependency: $name" }
}
if ($frontendDeps.Contains('AgentRuntime') -or $agentDeps.Contains('AgentRuntime')) { throw 'A dependency manifest still references AgentRuntime.' }

$forbiddenNames = @(
    'Avalonia.DesignerSupport.dll', 'Avalonia.Remote.Protocol.dll', 'createdump.exe',
    'mscordaccore.dll', 'mscordbi.dll', 'Microsoft.DiaSymReader.Native.amd64.dll'
)
foreach ($file in Get-ChildItem -LiteralPath $publish -Recurse -File) {
    if ($file.Extension -ieq '.pdb' -or $file.Name -match '\.Tests?(\.|$)' -or $forbiddenNames -contains $file.Name -or $file.Name -like 'mscordaccore_*.dll') {
        throw "Development/debug/test artifact is present: $($file.FullName)"
    }
    if ($file.FullName.IndexOf('\staging\', [StringComparison]::OrdinalIgnoreCase) -ge 0 -or
        $file.FullName.IndexOf('\reports\', [StringComparison]::OrdinalIgnoreCase) -ge 0) {
        throw "Staging/report artifact is inside the product: $($file.FullName)"
    }
}

$runtimeConfigurations = @(
    [System.IO.Path]::Combine($publish, 'PasswordManagerLocal.runtimeconfig.json')
    [System.IO.Path]::Combine($publish, 'PasswordManagerLocal.Windows.Agent.runtimeconfig.json')
)
foreach ($runtimeConfiguration in $runtimeConfigurations) {
    $configurationText = Get-Content -LiteralPath $runtimeConfiguration -Raw
    if (-not $configurationText.Contains('includedFrameworks') -or -not $configurationText.Contains('MetadataUpdater.IsSupported')) {
        throw "Runtime configuration does not show an explicit trimmed self-contained publish: $runtimeConfiguration"
    }
}

$files = @(Get-ChildItem -LiteralPath $publish -Recurse -File)
$totalBytes = ($files | Measure-Object -Property Length -Sum).Sum
Write-Host "Verified sibling two-executable publication for $RuntimeIdentifier."
Write-Host 'Verified frontend/backend dependency boundaries and absence of the legacy nested runtime.'
Write-Host 'Verified trimmed self-contained runtime configuration and release artifact policy.'
Write-Host "Final files: $($files.Count); final bytes: $totalBytes"
