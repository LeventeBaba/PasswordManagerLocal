namespace PasswordManagerLocal.Windows.EndpointRpc.Contracts.Requests;

public sealed class DeletePasswordTagEndpointRequest
{
    public Guid Token { get; set; }
    public Guid PasswordTagId { get; set; }
}
