using PasswordManagerLocal.Common.Contracts.Responses;

namespace PasswordManagerLocal.Windows.EndpointRpc.Contracts.Responses;

public sealed class GetDeviceEnrollmentStatusEndpointResponse
{
    public DeviceEnrollmentStatusResponse Status { get; set; } = new();
}
