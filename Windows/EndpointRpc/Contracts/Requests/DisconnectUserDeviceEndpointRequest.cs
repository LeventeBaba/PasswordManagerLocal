namespace PasswordManagerLocal.Windows.EndpointRpc.Contracts.Requests;

public sealed class DisconnectUserDeviceEndpointRequest
{
    public Guid Token { get; set; }
    public Guid DeviceId { get; set; }
    public byte[] MasterPassword { get; set; } = [];
}
