namespace PasswordManagerLocal.Windows.EndpointRpc.Contracts.Requests;

public sealed class RenewAuthSessionEndpointRequest
{
    public Guid Token { get; set; }
}
