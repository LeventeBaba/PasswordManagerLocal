namespace PasswordManagerLocal.Windows.EndpointRpc.Contracts.Requests;

public sealed class RefreshUserDevicePresenceEndpointRequest
{
    public Guid Token { get; set; }
}
