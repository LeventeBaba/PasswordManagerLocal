using System.Security.Cryptography;
using System.Text;

namespace PasswordManagerLocal.Windows.Ipc.Coordination;

public sealed class WindowsInstanceNameProvider
{
    private const string InstanceLocksDirectoryName = "instance-locks";

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

    public WindowsInstanceNames GetNames()
    {
        var userIdentifier = _userIdentityProvider.GetStableUserIdentifier();
        ArgumentException.ThrowIfNullOrWhiteSpace(userIdentifier);

        var material = Encoding.UTF8.GetBytes($"{_applicationIdentifier}\n{userIdentifier}");
        var suffix = Convert.ToHexString(SHA256.HashData(material))[..32].ToLowerInvariant();
        var lockDirectory = Path.Combine(
            _applicationDataDirectory,
            InstanceLocksDirectoryName);

        return new WindowsInstanceNames(
            Path.Combine(lockDirectory, $"agent.{suffix}.lock"),
            Path.Combine(lockDirectory, $"ui.{suffix}.lock"),
            $"PasswordManagerLocal.Control.{suffix}",
            $"PasswordManagerLocal.UiActivation.{suffix}",
            $"PasswordManagerLocal.Endpoints.{suffix}");
    }
}
