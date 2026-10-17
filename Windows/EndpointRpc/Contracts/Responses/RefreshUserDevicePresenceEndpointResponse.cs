using PasswordManagerLocal.Common.Contracts.Responses;

namespace PasswordManagerLocal.Windows.EndpointRpc.Contracts.Responses;

public sealed class RefreshUserDevicePresenceEndpointResponse
{
    public IReadOnlyList<UserDeviceInfoResponse> Devices { get; set; } = [];
}
