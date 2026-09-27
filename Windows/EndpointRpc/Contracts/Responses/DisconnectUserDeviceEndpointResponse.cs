using PasswordManagerLocal.Common.Contracts.Responses;

namespace PasswordManagerLocal.Windows.EndpointRpc.Contracts.Responses;

public sealed class DisconnectUserDeviceEndpointResponse
{
    public DeviceRemovalResultResponse Result { get; set; } = new();
}
