namespace PasswordManagerLocal.Windows.EndpointRpc.Contracts.Requests;

public sealed class RemovePasswordsEndpointRequest
{
    public Guid Token { get; set; }
    public IReadOnlyList<Guid> PasswordIds { get; set; } = [];
}
