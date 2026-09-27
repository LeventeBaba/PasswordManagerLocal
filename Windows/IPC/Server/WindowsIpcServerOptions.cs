using PasswordManagerLocal.Windows.Ipc.Protocol;

namespace PasswordManagerLocal.Windows.Ipc.Server;

public sealed class WindowsIpcServerOptions
{
    public const int DefaultMaximumActiveRequestsPerConnection = 64;
    public const int MaximumConfigurableActiveRequestsPerConnection = 1024;

    public WindowsIpcServerOptions(
        IpcPeerRole serverRole,
        IEnumerable<IpcPeerRole> acceptedClientRoles,
        IpcCapabilities capabilities,
        int maximumActiveRequestsPerConnection = DefaultMaximumActiveRequestsPerConnection,
        bool managesUiRegistration = true,
        IpcCapabilities requiredClientCapabilities = IpcCapabilities.None)
    {
        if (!Enum.IsDefined(serverRole) || serverRole == IpcPeerRole.TestClient)
            throw new ArgumentOutOfRangeException(nameof(serverRole));
        if (capabilities == IpcCapabilities.None || HasUnknownCapabilities(capabilities))
            throw new ArgumentOutOfRangeException(nameof(capabilities));
        if (requiredClientCapabilities != IpcCapabilities.None &&
            HasUnknownCapabilities(requiredClientCapabilities))
        {
            throw new ArgumentOutOfRangeException(nameof(requiredClientCapabilities));
        }
        if (maximumActiveRequestsPerConnection <= 0 ||
            maximumActiveRequestsPerConnection > MaximumConfigurableActiveRequestsPerConnection)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumActiveRequestsPerConnection),
                maximumActiveRequestsPerConnection,
                $"The IPC active-request limit must be between 1 and {MaximumConfigurableActiveRequestsPerConnection}.");
        }

        ArgumentNullException.ThrowIfNull(acceptedClientRoles);
        var roles = acceptedClientRoles.ToHashSet();
        if (roles.Count == 0 || roles.Any(role => !Enum.IsDefined(role)))
            throw new ArgumentException("At least one valid client role is required.", nameof(acceptedClientRoles));

        ServerRole = serverRole;
        AcceptedClientRoles = roles;
        Capabilities = capabilities;
        MaximumActiveRequestsPerConnection = maximumActiveRequestsPerConnection;
        ManagesUiRegistration = managesUiRegistration;
        RequiredClientCapabilities = requiredClientCapabilities;
    }

    public IpcPeerRole ServerRole { get; }
    public IReadOnlySet<IpcPeerRole> AcceptedClientRoles { get; }
    public IpcCapabilities Capabilities { get; }
    public int MaximumActiveRequestsPerConnection { get; }
    public bool ManagesUiRegistration { get; }
    public IpcCapabilities RequiredClientCapabilities { get; }

    private static bool HasUnknownCapabilities(IpcCapabilities capabilities)
    {
        const IpcCapabilities known =
            IpcCapabilities.Control |
            IpcCapabilities.UiActivation |
            IpcCapabilities.Status |
            IpcCapabilities.EndpointRpc;
        return (capabilities & ~known) != 0;
    }
}
