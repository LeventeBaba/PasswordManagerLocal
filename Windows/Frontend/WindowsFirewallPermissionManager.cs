using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PasswordManagerLocal.Common.Frontend.Services;
using PasswordManagerLocal.Common.Contracts.Sync;
using PasswordManagerLocal.Windows.Frontend.Diagnostics;
using PasswordManagerLocal.Windows.Ipc.Coordination;

namespace PasswordManagerLocal.Windows.Frontend;

internal sealed class WindowsFirewallPermissionManager : IFirewallPermissionManager
{
    private const string TcpAppRuleName = "PasswordManagerLocal Sync TCP App";
    private const string TcpPortRuleName = "PasswordManagerLocal Sync TCP Port";
    private const string MdnsAppRuleName = "PasswordManagerLocal mDNS UDP App";
    private const string MdnsPortRuleName = "PasswordManagerLocal mDNS UDP Port";
    private const string TcpOutboundAppRuleName = "PasswordManagerLocal Sync TCP Outbound App";
    private const string MdnsOutboundAppRuleName = "PasswordManagerLocal mDNS UDP Outbound App";

    private static readonly string[] LegacyRuleNames =
    [
        "PasswordManagerLocal Sync TCP",
        "PasswordManagerLocal mDNS UDP"
    ];

    private const string CheckScriptTemplate = """
param([string]$AppExe)
$ErrorActionPreference = 'Stop'
Import-Module NetSecurity -ErrorAction Stop
Import-Module NetConnection -ErrorAction SilentlyContinue

function Normalize-PmlPath {
    param([string]$Path)

    if ([string]::IsNullOrWhiteSpace($Path)) {
        return ''
    }

    try {
        return [System.IO.Path]::GetFullPath([Environment]::ExpandEnvironmentVariables($Path)).TrimEnd('\')
    } catch {
        return [Environment]::ExpandEnvironmentVariables($Path).TrimEnd('\')
    }
}

function Test-PmlProgramMatch {
    param(
        [string]$RuleProgram,
        [string]$AppExe
    )

    if ([string]::IsNullOrWhiteSpace($RuleProgram) -or [string]::IsNullOrWhiteSpace($AppExe)) {
        return $false
    }

    $program = Normalize-PmlPath $RuleProgram
    $app = Normalize-PmlPath $AppExe
    return [string]::Equals($program, $app, [System.StringComparison]::OrdinalIgnoreCase)
}

function Get-PmlActiveProfiles {
    $profiles = @()

    try {
        $connections = @(Get-NetConnectionProfile -ErrorAction Stop)
        foreach ($connection in $connections) {
            $category = [string]$connection.NetworkCategory
            if ($category -ieq 'DomainAuthenticated') {
                $category = 'Domain'
            }

            if ($category -in @('Domain', 'Private', 'Public')) {
                $profiles += $category
            }
        }
    } catch {
    }

    if ($profiles.Count -eq 0) {
        $profiles = @(Get-NetFirewallProfile -ErrorAction Stop | Where-Object {
            ([string]$_.Enabled) -ieq 'True'
        } | ForEach-Object { [string]$_.Name })
    }

    return @($profiles | Select-Object -Unique)
}

function Test-PmlRuleProfileMatch {
    param(
        $Rule,
        [string[]]$ActiveProfiles
    )

    $profileText = [string]$Rule.Profile
    if ($profileText -ieq 'Any') {
        return $true
    }

    foreach ($profile in $ActiveProfiles) {
        $pattern = '(^|,|\s){0}($|,|\s)' -f [regex]::Escape($profile)
        if ($profileText -match $pattern) {
            return $true
        }
    }

    return $false
}

function Test-PmlProtocolMatch {
    param(
        [string]$Actual,
        [string]$Expected
    )

    if ($Actual -ieq 'Any' -or $Actual -eq '256') {
        return $true
    }

    if ($Expected -ieq 'TCP') {
        return $Actual -ieq 'TCP' -or $Actual -eq '6'
    }

    if ($Expected -ieq 'UDP') {
        return $Actual -ieq 'UDP' -or $Actual -eq '17'
    }

    return $false
}

function Test-PmlPortMatch {
    param(
        $PortValue,
        [string]$ExpectedPort
    )

    $ports = @($PortValue | ForEach-Object { [string]$_ })
    return $ports -contains $ExpectedPort -or $ports -contains 'Any'
}

function Test-PmlEffectiveAllowRule {
    param(
        [string]$DisplayName,
        [string]$Direction,
        [string]$Protocol,
        [string]$Port,
        [string]$PortSide,
        [bool]$RequireProgram,
        [string]$AppExe,
        [string[]]$ActiveProfiles
    )

    $rules = @(Get-NetFirewallRule -PolicyStore ActiveStore -DisplayName $DisplayName -ErrorAction SilentlyContinue | Where-Object {
        ([string]$_.Enabled) -ieq 'True' -and
        ([string]$_.Direction) -ieq $Direction -and
        ([string]$_.Action) -ieq 'Allow'
    })

    foreach ($rule in $rules) {
        if (-not (Test-PmlRuleProfileMatch -Rule $rule -ActiveProfiles $ActiveProfiles)) {
            continue
        }

        $portFilters = @($rule | Get-NetFirewallPortFilter -ErrorAction SilentlyContinue)
        $portMatched = $false
        foreach ($portFilter in $portFilters) {
            $actualProtocol = [string]$portFilter.Protocol
            $actualPort = if ($PortSide -ieq 'Remote') { $portFilter.RemotePort } else { $portFilter.LocalPort }

            if ((Test-PmlProtocolMatch -Actual $actualProtocol -Expected $Protocol) -and
                (Test-PmlPortMatch -PortValue $actualPort -ExpectedPort $Port)) {
                $portMatched = $true
                break
            }
        }

        if (-not $portMatched) {
            continue
        }

        if ($RequireProgram) {
            $programMatched = $false
            $applicationFilters = @($rule | Get-NetFirewallApplicationFilter -ErrorAction SilentlyContinue)
            foreach ($applicationFilter in $applicationFilters) {
                if (Test-PmlProgramMatch -RuleProgram ([string]$applicationFilter.Program) -AppExe $AppExe) {
                    $programMatched = $true
                    break
                }
            }

            if (-not $programMatched) {
                continue
            }
        }

        return $true
    }

    return $false
}

function Get-PmlConflictingAppBlockRules {
    param(
        [string]$AppExe,
        [string[]]$ActiveProfiles
    )

    if ([string]::IsNullOrWhiteSpace($AppExe) -or -not (Test-Path -LiteralPath $AppExe)) {
        return @()
    }

    $matched = @()
    $rules = @(Get-NetFirewallRule -PolicyStore ActiveStore -ErrorAction SilentlyContinue | Where-Object {
        ([string]$_.Enabled) -ieq 'True' -and
        ([string]$_.Action) -ieq 'Block'
    })

    foreach ($rule in $rules) {
        if (-not (Test-PmlRuleProfileMatch -Rule $rule -ActiveProfiles $ActiveProfiles)) {
            continue
        }

        $filters = @($rule | Get-NetFirewallApplicationFilter -ErrorAction SilentlyContinue)
        foreach ($filter in $filters) {
            if (Test-PmlProgramMatch -RuleProgram ([string]$filter.Program) -AppExe $AppExe) {
                $matched += $rule
                break
            }
        }
    }

    return $matched
}

$activeProfiles = @(Get-PmlActiveProfiles)
if ($activeProfiles.Count -eq 0) {
    Write-Error 'No active Windows network/firewall profile could be determined.'
    exit 10
}

$enabledProfiles = @()
foreach ($profileName in $activeProfiles) {
    $profile = Get-NetFirewallProfile -PolicyStore ActiveStore -Name $profileName -ErrorAction SilentlyContinue
    if ($null -eq $profile -or ([string]$profile.Enabled) -ine 'True') {
        continue
    }

    $enabledProfiles += $profileName

    if (([string]$profile.AllowInboundRules) -ieq 'False') {
        Write-Error "Windows Firewall profile '$profileName' has Block all incoming connections enabled, so inbound allow rules are ignored."
        exit 11
    }

    if (([string]$profile.AllowLocalFirewallRules) -ieq 'False') {
        Write-Error "Windows Firewall profile '$profileName' is managed so local firewall rules are ignored. The app cannot make its local allow rules effective on this profile."
        exit 15
    }
}

if ($enabledProfiles.Count -eq 0) {
    Write-Output "OK: Windows Firewall is disabled for the active profile(s): $($activeProfiles -join ', ')."
    exit 0
}

$appBlockRules = @(Get-PmlConflictingAppBlockRules -AppExe $AppExe -ActiveProfiles $enabledProfiles)
if ($appBlockRules.Count -gt 0) {
    $ruleNames = ($appBlockRules | ForEach-Object { $_.DisplayName } | Select-Object -Unique) -join ', '
    Write-Error "An effective Windows Firewall block rule targets this executable: $ruleNames."
    exit 12
}

$tcpInboundOk =
    (Test-PmlEffectiveAllowRule -DisplayName '%TCP_PORT_RULE_NAME%' -Direction 'Inbound' -Protocol 'TCP' -Port '%SYNC_PORT%' -PortSide 'Local' -RequireProgram $false -AppExe $AppExe -ActiveProfiles $enabledProfiles) -or
    (Test-PmlEffectiveAllowRule -DisplayName '%TCP_APP_RULE_NAME%' -Direction 'Inbound' -Protocol 'TCP' -Port '%SYNC_PORT%' -PortSide 'Local' -RequireProgram $true -AppExe $AppExe -ActiveProfiles $enabledProfiles)

$udpInboundOk =
    (Test-PmlEffectiveAllowRule -DisplayName '%MDNS_PORT_RULE_NAME%' -Direction 'Inbound' -Protocol 'UDP' -Port '5353' -PortSide 'Local' -RequireProgram $false -AppExe $AppExe -ActiveProfiles $enabledProfiles) -or
    (Test-PmlEffectiveAllowRule -DisplayName '%MDNS_APP_RULE_NAME%' -Direction 'Inbound' -Protocol 'UDP' -Port '5353' -PortSide 'Local' -RequireProgram $true -AppExe $AppExe -ActiveProfiles $enabledProfiles)

if ([string]::IsNullOrWhiteSpace($AppExe) -or -not (Test-Path -LiteralPath $AppExe)) {
    Write-Error 'The running application executable could not be resolved, so outbound firewall permission cannot be verified.'
    exit 13
}

$tcpOutboundOk = Test-PmlEffectiveAllowRule -DisplayName '%TCP_OUTBOUND_APP_RULE_NAME%' -Direction 'Outbound' -Protocol 'TCP' -Port '%SYNC_PORT%' -PortSide 'Remote' -RequireProgram $true -AppExe $AppExe -ActiveProfiles $enabledProfiles
$udpOutboundOk = Test-PmlEffectiveAllowRule -DisplayName '%MDNS_OUTBOUND_APP_RULE_NAME%' -Direction 'Outbound' -Protocol 'UDP' -Port '5353' -PortSide 'Remote' -RequireProgram $true -AppExe $AppExe -ActiveProfiles $enabledProfiles

if (-not $tcpInboundOk -or -not $udpInboundOk -or -not $tcpOutboundOk -or -not $udpOutboundOk) {
    Write-Error "Required rules are not effective. TCP inbound=$tcpInboundOk, mDNS inbound=$udpInboundOk, TCP outbound=$tcpOutboundOk, mDNS outbound=$udpOutboundOk, active profiles=$($enabledProfiles -join ', ')."
    exit 14
}

Write-Output "OK: effective inbound and outbound local-network rules are active for profile(s): $($enabledProfiles -join ', ')."
exit 0
""";

    private const string ApplyScriptTemplate = """
param(
    [string]$AppExe,
    [string]$StatusFile
)

$ErrorActionPreference = 'Stop'

try {
Import-Module NetSecurity -ErrorAction Stop

function Normalize-PmlPath {
    param([string]$Path)

    if ([string]::IsNullOrWhiteSpace($Path)) {
        return ''
    }

    try {
        return [System.IO.Path]::GetFullPath([Environment]::ExpandEnvironmentVariables($Path)).TrimEnd('\')
    } catch {
        return [Environment]::ExpandEnvironmentVariables($Path).TrimEnd('\')
    }
}

function Test-PmlProgramMatch {
    param(
        [string]$RuleProgram,
        [string]$AppExe
    )

    if ([string]::IsNullOrWhiteSpace($RuleProgram) -or [string]::IsNullOrWhiteSpace($AppExe)) {
        return $false
    }

    $program = Normalize-PmlPath $RuleProgram
    $app = Normalize-PmlPath $AppExe
    return [string]::Equals($program, $app, [System.StringComparison]::OrdinalIgnoreCase)
}

function Remove-PmlFirewallRuleByDisplayName {
    param([string]$DisplayName)

    Get-NetFirewallRule -DisplayName $DisplayName -ErrorAction SilentlyContinue | Remove-NetFirewallRule -ErrorAction SilentlyContinue
}

function Remove-PmlConflictingAppBlockRules {
    param([string]$AppExe)

    if ([string]::IsNullOrWhiteSpace($AppExe) -or -not (Test-Path -LiteralPath $AppExe)) {
        return
    }

    $rules = @(Get-NetFirewallRule -ErrorAction SilentlyContinue | Where-Object {
        ([string]$_.Action) -ieq 'Block'
    })

    foreach ($rule in $rules) {
        $filters = @($rule | Get-NetFirewallApplicationFilter -ErrorAction SilentlyContinue)
        foreach ($filter in $filters) {
            if (Test-PmlProgramMatch -RuleProgram ([string]$filter.Program) -AppExe $AppExe) {
                $rule | Remove-NetFirewallRule -ErrorAction SilentlyContinue
                break
            }
        }
    }
}

%DELETE_LINES%
Remove-PmlConflictingAppBlockRules -AppExe $AppExe

New-NetFirewallRule -DisplayName '%TCP_PORT_RULE_NAME%' -Direction Inbound -Action Allow -Enabled True -Profile Any -Protocol TCP -LocalPort %SYNC_PORT% -RemoteAddress Any -InterfaceType Any -ErrorAction Stop | Out-Null
New-NetFirewallRule -DisplayName '%MDNS_PORT_RULE_NAME%' -Direction Inbound -Action Allow -Enabled True -Profile Any -Protocol UDP -LocalPort 5353 -RemoteAddress Any -InterfaceType Any -ErrorAction Stop | Out-Null

if (-not [string]::IsNullOrWhiteSpace($AppExe) -and (Test-Path -LiteralPath $AppExe)) {
    New-NetFirewallRule -DisplayName '%TCP_APP_RULE_NAME%' -Direction Inbound -Action Allow -Enabled True -Profile Any -Program $AppExe -Protocol TCP -LocalPort %SYNC_PORT% -RemoteAddress Any -InterfaceType Any -ErrorAction Stop | Out-Null
    New-NetFirewallRule -DisplayName '%MDNS_APP_RULE_NAME%' -Direction Inbound -Action Allow -Enabled True -Profile Any -Program $AppExe -Protocol UDP -LocalPort 5353 -RemoteAddress Any -InterfaceType Any -ErrorAction Stop | Out-Null
    New-NetFirewallRule -DisplayName '%TCP_OUTBOUND_APP_RULE_NAME%' -Direction Outbound -Action Allow -Enabled True -Profile Any -Program $AppExe -Protocol TCP -RemotePort %SYNC_PORT% -RemoteAddress Any -InterfaceType Any -ErrorAction Stop | Out-Null
    New-NetFirewallRule -DisplayName '%MDNS_OUTBOUND_APP_RULE_NAME%' -Direction Outbound -Action Allow -Enabled True -Profile Any -Program $AppExe -Protocol UDP -RemotePort 5353 -RemoteAddress Any -InterfaceType Any -ErrorAction Stop | Out-Null
} else {
    throw 'The running application executable could not be resolved.'
}

if (-not [string]::IsNullOrWhiteSpace($StatusFile)) {
    Set-Content -LiteralPath $StatusFile -Value 'OK: Windows Firewall inbound and outbound rules were added successfully.' -Encoding UTF8
}

exit 0
} catch {
    $message = $_.Exception.Message
    if (-not [string]::IsNullOrWhiteSpace($StatusFile)) {
        Set-Content -LiteralPath $StatusFile -Value ("ERROR: " + $message) -Encoding UTF8
    }

    [Console]::Error.WriteLine($message)
    exit 1
}
""";

    public async Task<FirewallPermissionCheckResult> CheckAsync(CancellationToken ct = default)
    {
        if (!OperatingSystem.IsWindows())
            return FirewallPermissionCheckResult.Unsupported();

        var appExePath = GetApplicationPath();
        if (string.IsNullOrWhiteSpace(appExePath) || !File.Exists(appExePath))
            appExePath = string.Empty;

        var result = await RunPowerShellScriptAsync(CreateCheckScript(), appExePath, elevated: false, ct);
        var details = GetBestProcessDetails(result);

        if (result.ExitCode == 0)
        {
            WindowsFrontendDebugLog.Info($"Windows Firewall effective-policy check succeeded. {details}");
            return new FirewallPermissionCheckResult
            {
                IsSupported = true,
                IsConfigured = true,
                CanRequestPermission = true,
                Details = details
            };
        }

        WindowsFrontendDebugLog.Error($"Windows Firewall effective-policy check failed. {details}");
        return new FirewallPermissionCheckResult
        {
            IsSupported = true,
            IsConfigured = false,
            CanRequestPermission = true,
            Details = details
        };
    }

    public async Task<FirewallPermissionCheckResult> RequestPermissionAsync(CancellationToken ct = default)
    {
        if (!OperatingSystem.IsWindows())
            return FirewallPermissionCheckResult.Unsupported();

        var appExePath = GetApplicationPath();
        if (string.IsNullOrWhiteSpace(appExePath) || !File.Exists(appExePath))
            appExePath = string.Empty;

        var applyResult = await RunPowerShellScriptAsync(CreateApplyScript(), appExePath, elevated: true, ct);
        if (applyResult.ExitCode != 0)
        {
            var details = GetBestProcessDetails(applyResult);
            WindowsFrontendDebugLog.Error($"Windows Firewall configuration failed. {details}");
            return new FirewallPermissionCheckResult
            {
                IsSupported = true,
                IsConfigured = false,
                CanRequestPermission = true,
                Details = details
            };
        }

        FirewallPermissionCheckResult? verification = null;
        for (var attempt = 0; attempt < 4; attempt++)
        {
            if (attempt > 0)
                await Task.Delay(TimeSpan.FromMilliseconds(250), ct);

            verification = await CheckAsync(ct);
            if (verification.IsConfigured)
            {
                var details = GetBestProcessDetails(applyResult);
                WindowsFrontendDebugLog.Info($"Windows Firewall configuration and effective-policy verification succeeded. {details}");
                return new FirewallPermissionCheckResult
                {
                    IsSupported = true,
                    IsConfigured = true,
                    CanRequestPermission = true,
                    Details = details
                };
            }
        }

        var applyDetails = GetBestProcessDetails(applyResult);
        var verificationDetails = verification?.Details;
        var combinedDetails = string.IsNullOrWhiteSpace(verificationDetails)
            ? $"{applyDetails} The firewall rules were created, but they are not present in the effective Windows Firewall policy."
            : $"{applyDetails} Effective-policy verification failed: {verificationDetails}";

        WindowsFrontendDebugLog.Error($"Windows Firewall rules were created but are not effective. {combinedDetails}");
        return new FirewallPermissionCheckResult
        {
            IsSupported = true,
            IsConfigured = false,
            CanRequestPermission = true,
            Details = combinedDetails
        };
    }

    private static string GetApplicationPath() =>
        Path.Combine(
            AppContext.BaseDirectory,
            WindowsExecutableNames.AgentExecutableFileName);

    private static string CreateCheckScript() =>
        CheckScriptTemplate
            .Replace("%TCP_PORT_RULE_NAME%", TcpPortRuleName, StringComparison.Ordinal)
            .Replace("%TCP_APP_RULE_NAME%", TcpAppRuleName, StringComparison.Ordinal)
            .Replace("%MDNS_PORT_RULE_NAME%", MdnsPortRuleName, StringComparison.Ordinal)
            .Replace("%MDNS_APP_RULE_NAME%", MdnsAppRuleName, StringComparison.Ordinal)
            .Replace("%TCP_OUTBOUND_APP_RULE_NAME%", TcpOutboundAppRuleName, StringComparison.Ordinal)
            .Replace("%MDNS_OUTBOUND_APP_RULE_NAME%", MdnsOutboundAppRuleName, StringComparison.Ordinal)
            .Replace("%SYNC_PORT%", SyncProtocolDefaults.TcpPort.ToString(), StringComparison.Ordinal);

    private static string CreateApplyScript()
    {
        var allRuleNames = GetManagedFirewallRuleNames();
        var deleteLines = string.Join(
            Environment.NewLine,
            allRuleNames.Select(name =>
                $"Remove-PmlFirewallRuleByDisplayName -DisplayName '{EscapePowerShellSingleQuotedString(name)}'"));

        return ApplyScriptTemplate
            .Replace("%DELETE_LINES%", deleteLines, StringComparison.Ordinal)
            .Replace("%TCP_PORT_RULE_NAME%", TcpPortRuleName, StringComparison.Ordinal)
            .Replace("%MDNS_PORT_RULE_NAME%", MdnsPortRuleName, StringComparison.Ordinal)
            .Replace("%TCP_APP_RULE_NAME%", TcpAppRuleName, StringComparison.Ordinal)
            .Replace("%MDNS_APP_RULE_NAME%", MdnsAppRuleName, StringComparison.Ordinal)
            .Replace("%TCP_OUTBOUND_APP_RULE_NAME%", TcpOutboundAppRuleName, StringComparison.Ordinal)
            .Replace("%MDNS_OUTBOUND_APP_RULE_NAME%", MdnsOutboundAppRuleName, StringComparison.Ordinal)
            .Replace("%SYNC_PORT%", SyncProtocolDefaults.TcpPort.ToString(), StringComparison.Ordinal);
    }

    private static IEnumerable<string> GetManagedFirewallRuleNames() =>
        new[]
        {
            TcpAppRuleName,
            TcpPortRuleName,
            MdnsAppRuleName,
            MdnsPortRuleName,
            TcpOutboundAppRuleName,
            MdnsOutboundAppRuleName
        }.Concat(LegacyRuleNames);

    private static async Task<(int ExitCode, string Output, string Error)> RunPowerShellScriptAsync(
        string script,
        string appExePath,
        bool elevated,
        CancellationToken ct)
    {
        var scriptPath = Path.Combine(Path.GetTempPath(), $"pml-firewall-{Guid.NewGuid():N}.ps1");
        var statusPath = elevated ? Path.Combine(Path.GetTempPath(), $"pml-firewall-status-{Guid.NewGuid():N}.txt") : null;
        await File.WriteAllTextAsync(scriptPath, script, ct);

        try
        {
            var startInfo = CreatePowerShellStartInfo(scriptPath, appExePath, statusPath, elevated);
            return await ExecutePowerShellAsync(startInfo, statusPath, elevated, ct);
        }
        catch (System.ComponentModel.Win32Exception ex) when ((uint)ex.NativeErrorCode == 1223)
        {
            return (-1, string.Empty, "The permission request was canceled by the user.");
        }
        finally
        {
            TryDeleteFile(scriptPath);
            if (!string.IsNullOrWhiteSpace(statusPath))
                TryDeleteFile(statusPath);
        }
    }

    private static ProcessStartInfo CreatePowerShellStartInfo(
        string scriptPath,
        string appExePath,
        string? statusPath,
        bool elevated)
    {
        var arguments = $"-NoProfile -ExecutionPolicy Bypass -File {QuoteArgument(scriptPath)} -AppExe {QuoteArgument(appExePath)}";
        if (!string.IsNullOrWhiteSpace(statusPath))
            arguments += $" -StatusFile {QuoteArgument(statusPath)}";

        return new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = arguments,
            UseShellExecute = elevated,
            CreateNoWindow = !elevated,
            Verb = elevated ? "runas" : string.Empty,
            RedirectStandardOutput = !elevated,
            RedirectStandardError = !elevated
        };
    }

    private static async Task<(int ExitCode, string Output, string Error)> ExecutePowerShellAsync(
        ProcessStartInfo startInfo,
        string? statusPath,
        bool elevated,
        CancellationToken ct)
    {
        using var process = Process.Start(startInfo);
        if (process is null)
            return (-1, string.Empty, "Could not start PowerShell.");

        if (elevated)
            return await ReadElevatedProcessResultAsync(process, statusPath, ct);

        var outputTask = process.StandardOutput.ReadToEndAsync(ct);
        var errorTask = process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);
        return (process.ExitCode, (await outputTask).Trim(), (await errorTask).Trim());
    }

    private static async Task<(int ExitCode, string Output, string Error)> ReadElevatedProcessResultAsync(
        Process process,
        string? statusPath,
        CancellationToken ct)
    {
        await process.WaitForExitAsync(ct);
        var output = !string.IsNullOrWhiteSpace(statusPath) && File.Exists(statusPath)
            ? await File.ReadAllTextAsync(statusPath, ct)
            : string.Empty;
        return (process.ExitCode, output.Trim(), string.Empty);
    }

    private static string GetBestProcessDetails((int ExitCode, string Output, string Error) result)
    {
        if (!string.IsNullOrWhiteSpace(result.Error))
            return result.Error;

        if (!string.IsNullOrWhiteSpace(result.Output))
            return result.Output;

        return $"PowerShell exited with code {result.ExitCode}.";
    }

    private static string QuoteArgument(string value) =>
        $"\"{value.Replace("\"", "\\\"")}\"";

    private static string EscapePowerShellSingleQuotedString(string value) =>
        value.Replace("'", "''");

    private static void TryDeleteFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch
        {
        }
    }
}
