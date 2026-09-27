<#
.SYNOPSIS
Inspects or changes the PasswordManagerLocal agent current-user startup entry.

.DESCRIPTION
This development and diagnostic script manages only the PasswordManagerLocal
value under HKCU\Software\Microsoft\Windows\CurrentVersion\Run. Production code
does not call this script.

.PARAMETER Action
Inspect, Register, or Unregister.

.PARAMETER AgentPath
Absolute path to PasswordManagerLocal.Windows.Agent.exe. Required for Register.

.EXAMPLE
.\ConfigureBackgroundSyncStartup.ps1 -Action Inspect

.EXAMPLE
.\ConfigureBackgroundSyncStartup.ps1 -Action Register -AgentPath 'C:\Apps\PasswordManagerLocal.Windows.Agent.exe'

.EXAMPLE
.\ConfigureBackgroundSyncStartup.ps1 -Action Unregister
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('Inspect', 'Register', 'Unregister')]
    [string]$Action,

    [Parameter()]
    [string]$AgentPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$runKeyPath = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$valueName = 'PasswordManagerLocal.Agent'
$backgroundArgument = '--background'

function Get-ExpectedCommand {
    param([Parameter(Mandatory = $true)][string]$Path)

    if (-not [System.IO.Path]::IsPathFullyQualified($Path)) {
        throw 'AgentPath must be an absolute path.'
    }

    $fullPath = [System.IO.Path]::GetFullPath($Path)
    if ([System.IO.Path]::GetFileName($fullPath) -ne 'PasswordManagerLocal.Windows.Agent.exe') {
        throw 'AgentPath must identify PasswordManagerLocal.Windows.Agent.exe.'
    }

    if ($fullPath.Contains('"')) {
        throw 'AgentPath contains an invalid quotation mark.'
    }

    return '"{0}" {1}' -f $fullPath, $backgroundArgument
}

function Get-CurrentCommand {
    if (-not (Test-Path -LiteralPath $runKeyPath)) {
        return $null
    }

    $properties = Get-ItemProperty -LiteralPath $runKeyPath -Name $valueName -ErrorAction SilentlyContinue
    if ($null -eq $properties) {
        return $null
    }

    return $properties.$valueName
}

switch ($Action) {
    'Inspect' {
        $current = Get-CurrentCommand
        if ($null -eq $current) {
            Write-Output "Startup entry '$valueName' is absent."
        }
        else {
            Write-Output "Startup entry '$valueName': $current"
        }
    }

    'Register' {
        if ([string]::IsNullOrWhiteSpace($AgentPath)) {
            throw 'AgentPath is required when Action is Register.'
        }

        $command = Get-ExpectedCommand -Path $AgentPath
        if (-not (Test-Path -LiteralPath $runKeyPath)) {
            New-Item -Path $runKeyPath -Force | Out-Null
        }

        New-ItemProperty -LiteralPath $runKeyPath -Name $valueName -Value $command -PropertyType String -Force | Out-Null
        $readBack = Get-CurrentCommand
        if ($readBack -cne $command) {
            throw 'The startup entry could not be verified after registration.'
        }

        Write-Output "Registered '$valueName': $readBack"
    }

    'Unregister' {
        if (Test-Path -LiteralPath $runKeyPath) {
            Remove-ItemProperty -LiteralPath $runKeyPath -Name $valueName -ErrorAction SilentlyContinue
        }

        if ($null -ne (Get-CurrentCommand)) {
            throw 'The startup entry is still present after removal.'
        }

        Write-Output "Startup entry '$valueName' is absent."
    }
}
