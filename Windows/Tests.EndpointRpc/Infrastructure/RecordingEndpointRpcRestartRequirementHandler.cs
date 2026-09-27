using PasswordManagerLocal.Windows.EndpointRpc.Server;

namespace PasswordManagerLocal.Windows.Tests.EndpointRpc.Infrastructure;

public sealed class RecordingEndpointRpcRestartRequirementHandler : IEndpointRpcRestartRequirementHandler
{
    public int RequestCount { get; private set; }
    public Exception? Failure { get; private set; }

    public void RequireProcessRestart(Exception failure)
    {
        ArgumentNullException.ThrowIfNull(failure);
        RequestCount++;
        Failure = failure;
    }
}
