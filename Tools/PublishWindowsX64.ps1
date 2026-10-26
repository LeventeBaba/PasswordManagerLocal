[CmdletBinding()]
param(
    [ValidateSet('Release')]
    [string]$Configuration = 'Release',

    [string]$OutputPath,

    [Nullable[long]]$BaselineTotalBytes,

    [switch]$RunSmokeTests,

    [switch]$Sign
)

$ErrorActionPreference = 'Stop'
$script = Join-Path $PSScriptRoot 'Windows\PublishWindowsProduct.ps1'
$arguments = @{
    Configuration = $Configuration
    RuntimeIdentifier = 'win-x64'
    RunSmokeTests = $RunSmokeTests
    Sign = $Sign
}
if (-not [string]::IsNullOrWhiteSpace($OutputPath)) { $arguments.OutputRoot = $OutputPath }
if ($null -ne $BaselineTotalBytes) { $arguments.BaselineTotalBytes = $BaselineTotalBytes }
& $script @arguments
