# Executed by the MSI as a commit action. The publisher prepends the operation
# flag and embeds this script. The system PowerShell process receives no user
# paths or commands from MSI properties.
$ErrorActionPreference = 'Stop'

if (-not [Environment]::Is64BitProcess) {
    throw 'Installer maintenance requires 64-bit Windows PowerShell.'
}
$programFiles = [Environment]::GetFolderPath([Environment+SpecialFolder]::ProgramFiles)
if (-not [IO.Path]::IsPathRooted($programFiles)) {
    throw 'Windows did not provide the 64-bit Program Files path.'
}
# Matches the private MSI directory rooted at ProgramFiles64Folder.
$installDirectory = Join-Path $programFiles 'PasswordManagerLocal'
$frontend = Join-Path $installDirectory 'PasswordManagerLocal.exe'
$agent = Join-Path $installDirectory 'PasswordManagerLocal.Windows.Agent.exe'
$managedRun = '"{0}" --background' -f $agent
$unifiedRun = '"{0}" --agent --background' -f $frontend
$stalePrograms = @($frontend, $agent)
$currentRun = $null

if (-not $pmlRemoving) {
    # Use the new payload's mode even when MSI defers file replacement to reboot.
    if ($pmlUnified) {
        $currentRun = $unifiedRun
        $stalePrograms = @($agent)
    }
    else {
        $currentRun = $managedRun
        $stalePrograms = @($frontend)
    }
}

$failed = $false
$users = [Microsoft.Win32.RegistryKey]::OpenBaseKey('Users', 'Registry64')
try {
    foreach ($sid in $users.GetSubKeyNames()) {
        # Only loaded local/domain and Microsoft Entra user hives.
        if ($sid -notmatch '^S-1-(?:5-21|12-1)(?:-\d+){4}$') { continue }
        try {
            $run = $users.OpenSubKey("$sid\Software\Microsoft\Windows\CurrentVersion\Run", $true)
            if ($null -eq $run) { continue }
            try {
                $value = $run.GetValue('PasswordManagerLocal.Agent', $null, 'DoNotExpandEnvironmentNames')
                if ($value -isnot [string] -or $value -notin @($managedRun, $unifiedRun)) { continue }
                if ($pmlRemoving) {
                    $run.DeleteValue('PasswordManagerLocal.Agent', $false)
                }
                elseif ($value -ine $currentRun) {
                    $run.SetValue('PasswordManagerLocal.Agent', $currentRun, 'String')
                }
            }
            finally { $run.Dispose() }
        }
        catch {
            $failed = $true
            Write-Warning 'Could not update a loaded Windows user''s sign-in entry.'
        }
    }
}
catch {
    $failed = $true
    Write-Warning 'Could not enumerate loaded Windows user hives.'
}
finally { $users.Dispose() }

try {
    Import-Module (Join-Path $PSHOME 'Modules\NetSecurity\NetSecurity.psd1')
    $ruleGroup = 'PasswordManagerLocal.LocalNetwork.v2'
    $ownedNames = @("$ruleGroup.Tcp.In", "$ruleGroup.Udp.In", "$ruleGroup.Tcp.Out", "$ruleGroup.Udp.Out")
    $legacyNames = @(
        'PasswordManagerLocal Sync TCP', 'PasswordManagerLocal Sync TCP App',
        'PasswordManagerLocal Sync TCP Port', 'PasswordManagerLocal Sync TCP Outbound App',
        'PasswordManagerLocal mDNS UDP', 'PasswordManagerLocal mDNS UDP App',
        'PasswordManagerLocal mDNS UDP Port', 'PasswordManagerLocal mDNS UDP Outbound App',
        'PasswordManagerLocal Discovery UDP App', 'PasswordManagerLocal Discovery UDP Port',
        'PasswordManagerLocal Discovery UDP Outbound App'
    )
    # Query the local persistent policy. Delete the specific returned rule
    # instance, since display names can be shared with other installations.
    foreach ($rule in @(Get-NetFirewallRule -PolicyStore PersistentStore -Action Allow)) {
        $appOwned = $rule.Group -ieq $ruleGroup -and $rule.DisplayName -in $ownedNames
        if (-not $appOwned -and $rule.DisplayName -notin $legacyNames) { continue }
        try {
            foreach ($filter in @($rule | Get-NetFirewallApplicationFilter)) {
                $program = [Environment]::ExpandEnvironmentVariables([string]$filter.Program).Trim('"')
                if (-not [IO.Path]::IsPathRooted($program) -or [IO.Path]::GetFullPath($program) -notin $stalePrograms) {
                    continue
                }

                Remove-NetFirewallRule -InputObject $rule -Confirm:$false
                break
            }
        }
        catch {
            $failed = $true
            Write-Warning 'Could not remove an app-owned local firewall rule.'
        }
    }
}
catch {
    $failed = $true
    Write-Warning 'Could not inspect the local Windows firewall policy.'
}

if ($failed) { exit 1 }
