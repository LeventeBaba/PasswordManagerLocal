namespace PasswordManagerLocal.Windows.EndpointRpc.Contracts.Requests;

public sealed class SetLocalDeviceNameEndpointRequest
{
    public Guid Token { get; set; }
    public string Name { get; set; } = string.Empty;
}
