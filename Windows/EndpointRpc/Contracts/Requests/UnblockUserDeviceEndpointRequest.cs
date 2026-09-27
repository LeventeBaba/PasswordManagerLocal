namespace PasswordManagerLocal.Windows.EndpointRpc.Contracts.Requests;

public sealed class UnblockUserDeviceEndpointRequest
{
    public Guid Token { get; set; }
    public Guid DeviceId { get; set; }
}
