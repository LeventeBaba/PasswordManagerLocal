namespace PasswordManagerLocal.Windows.EndpointRpc.Contracts.Responses;

public sealed class RenewAuthSessionEndpointResponse
{
    public Guid RenewedToken { get; set; }
}
