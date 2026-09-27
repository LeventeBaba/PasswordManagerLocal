using PasswordManagerLocal.Windows.Ipc.Protocol;

namespace PasswordManagerLocal.Windows.Ipc.Client;

public sealed class WindowsIpcClientOptions
{
    public const int DefaultMaximumPendingRequests = 64;
    public const int MaximumConfigurablePendingRequests = 1024;

    public WindowsIpcClientOptions(
        IpcPeerRole clientRole,
        IpcPeerRole expectedServerRole,
        IpcCapabilities capabilities,
        int processId,
        int windowsSessionId,
        Guid sessionId,
        int maximumPendingRequests = DefaultMaximumPendingRequests,
        IpcCapabilities requiredServerCapabilities = IpcCapabilities.None)
    {
        if (!Enum.IsDefined(clientRole))
            throw new ArgumentOutOfRangeException(nameof(clientRole));
        if (!Enum.IsDefined(expectedServerRole) || expectedServerRole == IpcPeerRole.TestClient)
            throw new ArgumentOutOfRangeException(nameof(expectedServerRole));
        if (capabilities == IpcCapabilities.None || HasUnknownCapabilities(capabilities))
            throw new ArgumentOutOfRangeException(nameof(capabilities));
        if (processId <= 0)
            throw new ArgumentOutOfRangeException(nameof(processId));
        if (windowsSessionId < 0)
            throw new ArgumentOutOfRangeException(nameof(windowsSessionId));
        if (sessionId == Guid.Empty)
            throw new ArgumentException("The IPC session ID cannot be empty.", nameof(sessionId));
        if (requiredServerCapabilities != IpcCapabilities.None &&
            HasUnknownCapabilities(requiredServerCapabilities))
        {
            throw new ArgumentOutOfRangeException(nameof(requiredServerCapabilities));
        }
        if (maximumPendingRequests <= 0 ||
            maximumPendingRequests > MaximumConfigurablePendingRequests)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumPendingRequests),
                maximumPendingRequests,
                $"The IPC pending-request limit must be between 1 and {MaximumConfigurablePendingRequests}.");
        }

        ClientRole = clientRole;
        ExpectedServerRole = expectedServerRole;
        Capabilities = capabilities;
        ProcessId = processId;
        WindowsSessionId = windowsSessionId;
        SessionId = sessionId;
        MaximumPendingRequests = maximumPendingRequests;
        RequiredServerCapabilities = requiredServerCapabilities;
    }

    public IpcPeerRole ClientRole { get; }
    public IpcPeerRole ExpectedServerRole { get; }
    public IpcCapabilities Capabilities { get; }
    public int ProcessId { get; }
    public int WindowsSessionId { get; }
    public Guid SessionId { get; }
    public int MaximumPendingRequests { get; }
    public IpcCapabilities RequiredServerCapabilities { get; }

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
