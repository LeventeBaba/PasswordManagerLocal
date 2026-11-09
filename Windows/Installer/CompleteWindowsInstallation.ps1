# The MSI passes the operation and the resolved directory as encoded data.
# Validation is read-only and runs before the transaction. Integration cleanup
# runs as an elevated commit action after the MSI transaction succeeds.
$ErrorActionPreference = 'Stop'

if (-not [Environment]::Is64BitProcess) {
    throw 'Installer maintenance requires 64-bit Windows PowerShell.'
}

function Get-LocalInstallPath([string]$Path) {
    if ($Path -notmatch '^[A-Za-z]:\\' -or $Path.Substring(2).Contains(':')) {
        throw 'Select an installation folder on a local fixed drive.'
    }
    $pathWithoutTrailingSlash = $Path.TrimEnd('\')
    $fullPath = [IO.Path]::GetFullPath($Path).TrimEnd('\')
    if ($fullPath.Length -le 2 -or $fullPath -ine $pathWithoutTrailingSlash -or
        $fullPath -match '[. ](?:\\|$)') {
        throw 'Select a dedicated installation folder with a full Windows path.'
    }
    return $fullPath
}

function Assert-ProtectedInstallPath([string]$Path) {
    $root = [IO.Path]::GetPathRoot($Path)
    $drive = [IO.DriveInfo]::new($root)
    if ($drive.DriveType -ne [IO.DriveType]::Fixed -or $drive.DriveFormat -ne 'NTFS') {
        throw 'Select an installation folder on a local fixed NTFS drive.'
    }

    $trusted = @(
        'S-1-5-18', # SYSTEM
        'S-1-5-32-544', # Administrators
        'S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464' # TrustedInstaller
    )
    $sections = [Security.AccessControl.AccessControlSections]'Access, Owner'
    $modifyExisting = [Security.AccessControl.FileSystemRights]'Delete, DeleteSubdirectoriesAndFiles, ChangePermissions, TakeOwnership, WriteAttributes, WriteExtendedAttributes'
    $createChild = [Security.AccessControl.FileSystemRights]'CreateFiles, CreateDirectories'
    $pathInfo = [IO.DirectoryInfo]::new($Path)
    $protectChildren = $true

    while ($null -ne $pathInfo) {
        try {
            $attributes = [IO.File]::GetAttributes($pathInfo.FullName)
        }
        catch [IO.FileNotFoundException], [IO.DirectoryNotFoundException] {
            $protectChildren = $true
            $pathInfo = $pathInfo.Parent
            continue
        }

        if (($attributes -band [IO.FileAttributes]::Directory) -eq 0 -or
            ($attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw 'The installation folder and its parent folders must be regular directories.'
        }

        $acl = $pathInfo.GetAccessControl($sections)
        $sidType = [Security.Principal.SecurityIdentifier]
        if ($acl.GetOwner($sidType).Value -notin $trusted) {
            throw 'The installation folder must be in a location controlled by Windows administrators.'
        }

        $descriptor = [Security.AccessControl.RawSecurityDescriptor]::new($acl.GetSecurityDescriptorBinaryForm(), 0)
        if ($null -eq $descriptor.DiscretionaryAcl) {
            throw 'The installation folder must have administrator-controlled access permissions.'
        }
        foreach ($rule in $acl.GetAccessRules($true, $true, $sidType)) {
            $inheritOnly = ($rule.PropagationFlags -band [Security.AccessControl.PropagationFlags]::InheritOnly) -ne 0
            if ($rule.AccessControlType -ne [Security.AccessControl.AccessControlType]::Allow -or
                $rule.IdentityReference.Value -in $trusted -or
                ($inheritOnly -and $rule.IdentityReference.Value -eq 'S-1-3-0')) {
                continue
            }

            # Existing ancestors can allow creation of other children, provided
            # users cannot change or remove the directories leading to this app.
            $rights = [int]$rule.FileSystemRights
            $canModify = ($rights -band ([int]$modifyExisting -bor 0x50000000)) -ne 0
            $canCreate = ($rights -band ([int]$createChild -bor 0x50000000)) -ne 0
            $appliesToChildren = $rule.InheritanceFlags -ne [Security.AccessControl.InheritanceFlags]::None
            if ((!$inheritOnly -and $canModify) -or
                ($protectChildren -and ((!$inheritOnly -and $canCreate) -or
                ($appliesToChildren -and ($canCreate -or $canModify))))) {
                throw 'Select an installation folder where only administrators can change the program files.'
            }
        }

        $protectChildren = $false
        $pathInfo = $pathInfo.Parent
    }
}

$installDirectory = Get-LocalInstallPath $pmlInstallDirectory
if ($pmlOperation -in @('validate', 'validateInstalled')) {
    Assert-ProtectedInstallPath $installDirectory
    $registered = Get-ItemProperty -LiteralPath 'HKLM:\Software\PasswordManagerLocal\Installer' -ErrorAction SilentlyContinue
    if ($pmlOperation -eq 'validateInstalled') {
        if (-not $registered.InstallLocation -or
            (Get-LocalInstallPath $registered.InstallLocation) -ine $installDirectory) {
            throw 'An upgrade or repair must use the existing installation folder.'
        }
    }
    elseif ($registered.InstallLocation -or ([IO.Directory]::Exists($installDirectory) -and
            @(Get-ChildItem -LiteralPath $installDirectory -Force).Count -ne 0)) {
        throw 'Select a new or empty folder for Password Manager Local.'
    }

    exit 0
}

$pmlRemoving = $pmlOperation -eq 'remove'
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
