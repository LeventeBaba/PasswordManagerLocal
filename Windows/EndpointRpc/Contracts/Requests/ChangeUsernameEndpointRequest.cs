namespace PasswordManagerLocal.Windows.EndpointRpc.Contracts.Requests;

public sealed class ChangeUsernameEndpointRequest
{
    public Guid Token { get; set; }
    public string NewUsername { get; set; } = string.Empty;
}
