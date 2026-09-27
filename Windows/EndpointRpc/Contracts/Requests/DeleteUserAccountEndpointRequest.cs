namespace PasswordManagerLocal.Windows.EndpointRpc.Contracts.Requests;

public sealed class DeleteUserAccountEndpointRequest
{
    public Guid Token { get; set; }
    public byte[] Password { get; set; } = [];
}
