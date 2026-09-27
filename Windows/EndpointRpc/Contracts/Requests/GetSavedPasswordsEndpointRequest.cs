namespace PasswordManagerLocal.Windows.EndpointRpc.Contracts.Requests;

public sealed class GetSavedPasswordsEndpointRequest
{
    public Guid Token { get; set; }
}
