using PasswordManagerLocal.Common.Contracts.Constants;
using System.Text;
using System.Text.Json;

namespace PasswordManagerLocal.Common.Frontend.Services;

internal static class WindowsFirewallConfigurationStore
{
    private static readonly object Gate = new();
    private static string? _configurationPath;

    public static void Initialize(string applicationDataDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(applicationDataDirectory);
        lock (Gate)
        {
            var path = Path.Combine(
                Path.GetFullPath(applicationDataDirectory),
                ApplicationFileNames.WindowsFirewallConfigurationFileName);
            if (_configurationPath is not null &&
                !string.Equals(_configurationPath, path, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "Windows firewall configuration was already initialized for another directory.");
            }

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            _configurationPath = path;
        }
    }

    public static bool IsConfigured()
    {
        lock (Gate)
        {
            try
            {
                var path = GetConfigurationPath();
                if (!File.Exists(path))
                    return false;
                var json = File.ReadAllText(path, Encoding.UTF8);
                var configuration = JsonSerializer.Deserialize(
                    json,
                    WindowsFirewallConfigurationJsonContext.Default.WindowsFirewallConfiguration);
                return configuration?.IsConfigured == true;
            }
            catch
            {
                return false;
            }
        }
    }

    public static void SetConfigured(bool isConfigured)
    {
        lock (Gate)
        {
            var path = GetConfigurationPath();
            var directory = Path.GetDirectoryName(path)!;
            var temporaryPath = Path.Combine(
                directory,
                $"{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
            try
            {
                Directory.CreateDirectory(directory);
                var json = JsonSerializer.Serialize(
                    new WindowsFirewallConfiguration { IsConfigured = isConfigured },
                    WindowsFirewallConfigurationJsonContext.Default.WindowsFirewallConfiguration);
                File.WriteAllText(temporaryPath, json, new UTF8Encoding(false));
                File.Move(temporaryPath, path, overwrite: true);
            }
            catch
            {
            }
            finally
            {
                try
                {
                    if (File.Exists(temporaryPath))
                        File.Delete(temporaryPath);
                }
                catch
                {
                }
            }
        }
    }

    private static string GetConfigurationPath() =>
        _configurationPath ?? throw new InvalidOperationException(
            "Windows firewall configuration has not been initialized.");
}
