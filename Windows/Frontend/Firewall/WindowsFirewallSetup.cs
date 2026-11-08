using System.Runtime.InteropServices;
using PasswordManagerLocal.Windows.Frontend.Diagnostics;
using PasswordManagerLocal.Windows.Ipc.Constants.Launch;

namespace PasswordManagerLocal.Windows.Frontend.Firewall;

internal static class WindowsFirewallSetup
{
    internal const string Argument = "--configure-firewall";
    internal const int InvalidArgumentsExitCode = 2;

    internal static string GetAgentPath() =>
        Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            Program.UseUnifiedExecutable
                ? WindowsExecutableNames.UiExecutableFileName
                : WindowsExecutableNames.AgentExecutableFileName));

    internal static int Run()
    {
        if (!OperatingSystem.IsWindows())
            return 1;
        var agentPath = GetAgentPath();
        if (!File.Exists(agentPath))
            return 11;
        try
        {
            using var policy = new WindowsFirewallPolicy();
            var result = new WindowsFirewallConfigurationService(policy, agentPath).Configure();
            if (result.IsConfigured)
                return 0;
            WindowsFrontendDebugLog.Error($"Windows Firewall setup: {result.Details}");
            return result.CanRequestPermission ? 1 : 10;
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException ||
            exception is COMException { HResult: unchecked((int)0x80070005) })
        {
            return 5;
        }
        catch (Exception exception)
        {
            WindowsFrontendDebugLog.Error($"Windows Firewall API setup failed: {exception}");
            return 1;
        }
    }

    internal static string DescribeExitCode(int code) => code switch
    {
        0 => "Windows saved the firewall rules, but verification has not succeeded.",
        5 => "Windows denied permission to change firewall rules.",
        10 => "Existing Windows firewall policy prevents this setup from taking effect.",
        11 => "The executable that runs the Agent is missing.",
        _ => $"Windows Firewall setup did not complete (exit code {code})."
    };
}
