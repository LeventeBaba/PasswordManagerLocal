namespace PasswordManagerLocal.Windows.EndpointRpc.Contracts.Requests;

public sealed class SetRememberMeEndpointRequest
{
    public Guid Token { get; set; }
    public bool RememberMe { get; set; }
}
