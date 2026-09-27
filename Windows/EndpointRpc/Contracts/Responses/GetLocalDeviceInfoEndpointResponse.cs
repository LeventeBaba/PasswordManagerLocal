using PasswordManagerLocal.Common.Contracts.Responses;

namespace PasswordManagerLocal.Windows.EndpointRpc.Contracts.Responses;

public sealed class GetLocalDeviceInfoEndpointResponse
{
    public LocalDeviceInfoResponse Device { get; set; } = new();
}
