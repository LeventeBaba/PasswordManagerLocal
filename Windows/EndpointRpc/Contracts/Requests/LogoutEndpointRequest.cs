namespace PasswordManagerLocal.Windows.EndpointRpc.Contracts.Requests;

public sealed class LogoutEndpointRequest
{
    public Guid Token { get; set; }
}
