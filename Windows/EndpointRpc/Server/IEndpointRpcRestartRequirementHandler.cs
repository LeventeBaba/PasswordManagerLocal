namespace PasswordManagerLocal.Windows.EndpointRpc.Server;

public interface IEndpointRpcRestartRequirementHandler
{
    void RequireProcessRestart(Exception failure);
}
