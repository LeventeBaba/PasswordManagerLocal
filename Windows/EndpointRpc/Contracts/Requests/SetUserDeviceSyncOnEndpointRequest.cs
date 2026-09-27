namespace PasswordManagerLocal.Windows.EndpointRpc.Contracts.Requests;

public sealed class SetUserDeviceSyncOnEndpointRequest
{
    public Guid Token { get; set; }
    public Guid DeviceId { get; set; }
    public bool IsSyncOn { get; set; }
}
