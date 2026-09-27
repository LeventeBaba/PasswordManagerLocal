namespace PasswordManagerLocal.Windows.EndpointRpc.Authorization;

public interface IEndpointUiRegistrationResolver
{
    bool TryResolve(
        int processId,
        int windowsSessionId,
        Guid instanceId,
        out long registrationGeneration);

    bool IsCurrent(
        int processId,
        int windowsSessionId,
        Guid instanceId,
        long registrationGeneration);
}
