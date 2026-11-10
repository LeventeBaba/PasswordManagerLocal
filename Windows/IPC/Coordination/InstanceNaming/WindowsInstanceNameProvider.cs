using System.Security.Cryptography;
using System.Text;

namespace PasswordManagerLocal.Windows.Ipc.Coordination.InstanceNaming;

public sealed class WindowsInstanceNameProvider
{

    private readonly string _applicationIdentifier;
    private readonly string _applicationDataDirectory;
    private readonly IWindowsUserIdentityProvider _userIdentityProvider;

    public WindowsInstanceNameProvider(
        string applicationIdentifier,
        string applicationDataDirectory,
        IWindowsUserIdentityProvider userIdentityProvider)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(applicationIdentifier);
        ArgumentException.ThrowIfNullOrWhiteSpace(applicationDataDirectory);
        if (!Path.IsPathFullyQualified(applicationDataDirectory))
        {
            throw new ArgumentException(
                "The application data directory must be absolute.",
                nameof(applicationDataDirectory));
        }

        _applicationIdentifier = applicationIdentifier;
        _applicationDataDirectory = Path.GetFullPath(applicationDataDirectory);
        _userIdentityProvider = userIdentityProvider
            ?? throw new ArgumentNullException(nameof(userIdentityProvider));
    }

    public WindowsInstanceNames GetNames(string? pipeApplicationIdentifier = null)
    {
        var userIdentifier = _userIdentityProvider.GetStableUserIdentifier();
        ArgumentException.ThrowIfNullOrWhiteSpace(userIdentifier);

        var lockSuffix = GetSuffix(_applicationIdentifier, userIdentifier);
        // Both editions share ownership of the vault. Edition-specific pipes keep
        // the UI from connecting to the other edition's Agent.
        var pipeSuffix = pipeApplicationIdentifier is null
            ? lockSuffix
            : GetSuffix(pipeApplicationIdentifier, userIdentifier);
        var lockDirectory = Path.Combine(
            _applicationDataDirectory,
            CoordinationConstants.WindowsInstanceNameProviderInstanceLocksDirectoryName);

        return new WindowsInstanceNames(
            Path.Combine(lockDirectory, $"agent.{lockSuffix}.lock"),
            Path.Combine(lockDirectory, $"ui.{lockSuffix}.lock"),
            $"PasswordManagerLocal.Control.{pipeSuffix}",
            $"PasswordManagerLocal.UiActivation.{pipeSuffix}",
            $"PasswordManagerLocal.Endpoints.{pipeSuffix}");
    }

    private static string GetSuffix(string applicationIdentifier, string userIdentifier)
    {
        var material = Encoding.UTF8.GetBytes($"{applicationIdentifier}\n{userIdentifier}");
        return Convert.ToHexString(SHA256.HashData(material))[..32].ToLowerInvariant();
    }
}
