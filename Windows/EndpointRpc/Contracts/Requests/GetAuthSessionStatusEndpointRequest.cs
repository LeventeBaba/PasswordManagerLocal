namespace PasswordManagerLocal.Windows.EndpointRpc.Contracts.Requests;

public sealed class GetAuthSessionStatusEndpointRequest
{
    public Guid Token { get; set; }
}
