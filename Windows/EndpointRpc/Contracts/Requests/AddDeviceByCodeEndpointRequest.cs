namespace PasswordManagerLocal.Windows.EndpointRpc.Contracts.Requests;

public sealed class AddDeviceByCodeEndpointRequest
{
    public Guid Token { get; set; }
    public string Code { get; set; } = string.Empty;
}
