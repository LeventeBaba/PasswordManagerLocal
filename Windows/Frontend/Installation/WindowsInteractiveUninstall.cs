using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using PasswordManagerLocal.Common.Contracts.Constants;

namespace PasswordManagerLocal.Windows.Frontend.Installation;

internal static class WindowsInteractiveUninstall
{
    public const string Argument = "--uninstall";

    private const string InstallerKey = @"Software\PasswordManagerLocalDemo\Installer";
    private const string StartupKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string StartupValue = "PasswordManagerLocal.Agent";
    private const int Yes = 6;
    private const int No = 7;
    private const uint YesNoCancelWithWarningAndDefaultNo = 0x00000133;
    private const uint ErrorIcon = 0x00000010;

    public static int Run()
    {
        try
        {
            var productCode = GetInstalledProductCode();
            var choice = MessageBox(
                IntPtr.Zero,
                """
                Delete the local vault and settings for this Windows user before uninstalling?

                Yes permanently deletes the encrypted vault, its key, and settings shared by the Demo and full editions for this Windows user. This happens even if uninstall is canceled later. Data on other devices is unaffected.
                No keeps this user's vault and settings.
                Cancel keeps the app installed.

                To delete data, first exit the running edition completely from its notification-area icon.
                """,
                ApplicationBranding.DisplayName,
                YesNoCancelWithWarningAndDefaultNo);

            if (choice is not (Yes or No))
                return 0;

            if (choice == Yes)
            {
                var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                if (string.IsNullOrWhiteSpace(localAppData))
                    throw new InvalidOperationException("Windows could not locate this user's Local AppData folder.");

                var dataPath = Path.Combine(localAppData, ApplicationFileNames.AppFolderName);
                var names = new WindowsInstanceNameProvider(
                    ApplicationFileNames.AppFolderName, dataPath, new WindowsUserIdentityProvider()).GetNames();
                WindowsLocalDataRemoval.Remove(dataPath, names);
                RemoveMatchingStartupEntry();
            }

            var startInfo = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "msiexec.exe"));
            startInfo.ArgumentList.Add("/x");
            startInfo.ArgumentList.Add(productCode.ToString("B"));
            using var installer = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Windows Installer could not be started.");
            return 0;
        }
        catch (InvalidOperationException exception)
        {
            ShowError(exception.Message);
            return 1;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or Win32Exception)
        {
            ShowError("The requested operation could not be completed. If you chose to delete local data, some files may already have been removed. Check that the app and its background Agent are closed, then try again.");
            return 1;
        }
    }

    private static Guid GetInstalledProductCode()
    {
        using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var installation = machine.OpenSubKey(InstallerKey);
        var installLocation = installation?.GetValue("InstallLocation") as string;
        var productCode = installation?.GetValue("ProductCode") as string;
        if (installLocation is null ||
            !Path.IsPathFullyQualified(installLocation) ||
            !string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(installLocation)),
                Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory), StringComparison.OrdinalIgnoreCase) ||
            !Guid.TryParseExact(productCode, "B", out var code))
        {
            throw new InvalidOperationException("Use this uninstall shortcut from the installed copy of Password Manager Local Demo.");
        }

        return code;
    }

    private static void RemoveMatchingStartupEntry()
    {
        using var key = Registry.CurrentUser.OpenSubKey(StartupKey, writable: true);
        var command = key?.GetValue(StartupValue, null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
        var managedAgent = Path.Combine(AppContext.BaseDirectory, WindowsExecutableNames.AgentExecutableFileName);
        var unifiedAgent = Path.Combine(AppContext.BaseDirectory, WindowsExecutableNames.UiExecutableFileName);
        if (key is not null &&
            (string.Equals(command, $"\"{managedAgent}\" {WindowsAgentLaunchArguments.Background}", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(command, $"\"{unifiedAgent}\" {WindowsAgentLaunchArguments.AgentRole} {WindowsAgentLaunchArguments.Background}", StringComparison.OrdinalIgnoreCase)))
        {
            key.DeleteValue(StartupValue, throwOnMissingValue: false);
        }
    }

    private static void ShowError(string text) =>
        _ = MessageBox(IntPtr.Zero, text, ApplicationBranding.DisplayName, ErrorIcon);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBox(IntPtr window, string text, string caption, uint type);
}
