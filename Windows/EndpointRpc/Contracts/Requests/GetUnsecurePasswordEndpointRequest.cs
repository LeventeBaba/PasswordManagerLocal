namespace PasswordManagerLocal.Windows.EndpointRpc.Contracts.Requests;

public sealed class GetUnsecurePasswordEndpointRequest
{
    public Guid Token { get; set; }
    public Guid PasswordId { get; set; }
}
