namespace PasswordManagerLocal.Windows.EndpointRpc.Contracts.Requests;

public sealed class SetUserDeviceNameEndpointRequest
{
    public Guid Token { get; set; }
    public Guid DeviceId { get; set; }
    public string Name { get; set; } = string.Empty;
}
