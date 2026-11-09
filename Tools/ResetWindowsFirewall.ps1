#Requires -Version 5.1
#Requires -RunAsAdministrator
<#
.SYNOPSIS
Removes only Password Manager Local firewall configuration for manual testing.
.DESCRIPTION
Close the app first. Removes known current/legacy app rules from the local
persistent policy and rules targeting the Agent executable. Does not reset
Windows Firewall, change profile defaults, or change domain/MDM policy.
Without AgentPath, rules targeting any PasswordManagerLocal.Windows.Agent.exe
installation are selected. With AgentPath, unnamed application rules are
selected only for those exact paths; known app-owned names are always selected.
ApplicationDataDirectory must refer to the app user's directory when elevation
uses a different administrator account. Only windows-firewall.json is removed.
This is a developer tool; it is never copied into application publications.
.EXAMPLE
.\Tools\ResetWindowsFirewall.ps1 -WhatIf
.EXAMPLE
.\Tools\ResetWindowsFirewall.ps1 -AgentPath 'C:\Apps\PasswordManagerLocal\PasswordManagerLocal.Windows.Agent.exe'
#>
[CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'Medium')]
param(
    [string[]]$AgentPath = @(),
    [string]$ApplicationDataDirectory = (Join-Path $env:LOCALAPPDATA 'PasswordManagerLocal')
)

$ErrorActionPreference = 'Stop'
Import-Module NetSecurity -ErrorAction Stop

$ruleGroup = 'PasswordManagerLocal.LocalNetwork.v2'
$ownedNames = @(
    "$ruleGroup.Tcp.In", "$ruleGroup.Udp.In", "$ruleGroup.Tcp.Out", "$ruleGroup.Udp.Out",
    'PasswordManagerLocal Sync TCP', 'PasswordManagerLocal Sync TCP App',
    'PasswordManagerLocal Sync TCP Port', 'PasswordManagerLocal Sync TCP Outbound App',
    'PasswordManagerLocal mDNS UDP', 'PasswordManagerLocal mDNS UDP App',
    'PasswordManagerLocal mDNS UDP Port', 'PasswordManagerLocal mDNS UDP Outbound App',
    'PasswordManagerLocal Discovery UDP App', 'PasswordManagerLocal Discovery UDP Port',
    'PasswordManagerLocal Discovery UDP Outbound App'
)

function ConvertTo-NormalizedProgram([string]$Program) {
    if ([string]::IsNullOrWhiteSpace($Program) -or $Program -eq 'Any' -or $Program -eq '*') { return '' }
    try { return [IO.Path]::GetFullPath([Environment]::ExpandEnvironmentVariables($Program.Trim('"'))) }
    catch { return '' }
}

$selectedPaths = @($AgentPath | ForEach-Object {
    $path = ConvertTo-NormalizedProgram $_
    if (-not $path -or [IO.Path]::GetFileName($path) -ine 'PasswordManagerLocal.Windows.Agent.exe') {
        throw "AgentPath must name PasswordManagerLocal.Windows.Agent.exe: $_"
    }
    $path
})

function Test-AppRule($Rule) {
    if ($ownedNames -contains [string]$Rule.DisplayName -or [string]$Rule.Group -eq $ruleGroup) { return $true }
    foreach ($filter in @($Rule | Get-NetFirewallApplicationFilter -ErrorAction Stop)) {
        $program = ConvertTo-NormalizedProgram ([string]$filter.Program)
        if (-not $program) { continue }
        if ($selectedPaths.Count -gt 0) {
            if ($selectedPaths -contains $program) { return $true }
        }
        elseif ([IO.Path]::GetFileName($program) -ieq 'PasswordManagerLocal.Windows.Agent.exe') {
            return $true
        }
    }
    return $false
}

Write-Host 'Close Password Manager Local before resetting its firewall configuration.'
$removed = 0
$localRules = @(Get-NetFirewallRule -PolicyStore PersistentStore -ErrorAction Stop)
foreach ($rule in $localRules) {
    if ((Test-AppRule $rule) -and $PSCmdlet.ShouldProcess(
        "$($rule.DisplayName) [$($rule.Name)]", 'Remove Password Manager Local firewall rule')) {
        $rule | Remove-NetFirewallRule -Confirm:$false -ErrorAction Stop
        $removed++
    }
}

$statusPath = Join-Path ([IO.Path]::GetFullPath($ApplicationDataDirectory)) 'windows-firewall.json'
if ((Test-Path -LiteralPath $statusPath -PathType Leaf) -and
    $PSCmdlet.ShouldProcess($statusPath, 'Remove cached Password Manager Local firewall status')) {
    Remove-Item -LiteralPath $statusPath -Force -ErrorAction Stop
}

Write-Host "Removed $removed local firewall rules. No profile defaults or other applications' rules were changed."
if (-not $WhatIfPreference) {
    $remaining = @(Get-NetFirewallRule -PolicyStore ActiveStore -ErrorAction Stop | Where-Object { Test-AppRule $_ })
    if ($remaining.Count -gt 0) {
        Write-Warning 'App rules remain in the effective policy. They may be managed by Group Policy/MDM; this tool does not remove managed policy.'
        $remaining | Select-Object DisplayName, Enabled, Direction, Action, PolicyStoreSourceType | Format-Table -AutoSize
    }
}
Write-Host 'Current profile policy (unchanged):'
Get-NetFirewallProfile -PolicyStore ActiveStore |
    Select-Object Name, Enabled, DefaultInboundAction, DefaultOutboundAction, AllowLocalFirewallRules |
    Format-Table -AutoSize
Write-Host 'A disabled firewall or permissive global policy can allow traffic even with all app rules removed.'
