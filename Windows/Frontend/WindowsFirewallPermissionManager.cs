using System.ComponentModel;
using System.Diagnostics;
using PasswordManagerLocal.Common.Frontend.Services;
using PasswordManagerLocal.Windows.Frontend.Diagnostics;
using PasswordManagerLocal.Windows.Frontend.Firewall;
using PasswordManagerLocal.Windows.Ipc.Constants.Launch;

namespace PasswordManagerLocal.Windows.Frontend;

internal sealed class WindowsFirewallPermissionManager : IFirewallPermissionManager
{
    public Task<FirewallPermissionCheckResult> CheckAsync(CancellationToken ct = default)
    {
        if (!OperatingSystem.IsWindows())
            return Task.FromResult(FirewallPermissionCheckResult.Unsupported());
        return Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            var agentPath = WindowsFirewallSetup.GetAgentPath();
            if (!File.Exists(agentPath))
                return Failure("The sibling Agent executable could not be found. Reinstall the complete app folder.", false);
            try
            {
                using var policy = new WindowsFirewallPolicy();
                var result = new WindowsFirewallConfigurationService(policy, agentPath).Check();
                WindowsFrontendDebugLog.Info($"Windows Firewall API check: configured={result.IsConfigured}; {result.Details}");
                return result;
            }
            catch (Exception exception)
            {
                WindowsFrontendDebugLog.Error($"Windows Firewall API check failed: {exception}");
                return Failure($"Windows Firewall could not be inspected (0x{exception.HResult:X8}). {exception.Message}");
            }
        }, ct);
    }

    public async Task<FirewallPermissionCheckResult> RequestPermissionAsync(CancellationToken ct = default)
    {
        var before = await CheckAsync(ct);
        if (before.IsConfigured || !before.CanRequestPermission)
            return before;
        var executable = Path.Combine(AppContext.BaseDirectory, WindowsExecutableNames.UiExecutableFileName);
        if (!File.Exists(executable))
            return Failure("The application executable could not be found. Reinstall the complete app folder.", false);
        try
        {
            ct.ThrowIfCancellationRequested();
            // Only this fixed operation is elevated. No ports, application paths,
            // commands, or script contents are accepted from the caller.
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = executable,
                Arguments = WindowsFirewallSetup.Argument,
                WorkingDirectory = AppContext.BaseDirectory,
                UseShellExecute = true,
                Verb = "runas"
            });
            if (process is null)
                return Failure("Windows could not start the firewall permission request.");
            await process.WaitForExitAsync(ct);
            FirewallPermissionCheckResult? verified = null;
            for (var attempt = 0; attempt < 4; attempt++)
            {
                if (attempt != 0)
                    await Task.Delay(250, ct);
                verified = await CheckAsync(ct);
                if (verified.IsConfigured)
                    return verified;
            }
            return Failure($"{WindowsFirewallSetup.DescribeExitCode(process.ExitCode)} {verified?.Details}",
                verified?.CanRequestPermission ?? true);
        }
        catch (Win32Exception exception) when (exception.NativeErrorCode == 1223)
        {
            return Failure("The Windows administrator permission request was canceled.");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception)
        {
            WindowsFrontendDebugLog.Error($"Windows Firewall setup failed: {exception}");
            return Failure($"Windows Firewall setup could not run (0x{exception.HResult:X8}). {exception.Message}");
        }
    }

    private static FirewallPermissionCheckResult Failure(string details, bool canRequest = true) => new()
    {
        IsSupported = true, IsConfigured = false, CanRequestPermission = canRequest, Details = details
    };
}
