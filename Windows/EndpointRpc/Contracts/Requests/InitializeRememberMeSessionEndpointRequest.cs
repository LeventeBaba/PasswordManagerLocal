namespace PasswordManagerLocal.Windows.EndpointRpc.Contracts.Requests;

public sealed class InitializeRememberMeSessionEndpointRequest
{
    public Guid UserId { get; set; }
}
