namespace PasswordManagerLocal.Windows.EndpointRpc.Contracts.Requests;

public sealed class SetLocalUserSyncOnEndpointRequest
{
    public Guid Token { get; set; }
    public bool IsSyncOn { get; set; }
}
