using PasswordManagerLocal.Common.Contracts.Responses;

namespace PasswordManagerLocal.Windows.EndpointRpc.Contracts.Responses;

public sealed class GetAuthSessionStatusEndpointResponse
{
    public AuthSessionStatusResponse Status { get; set; } = new();
}
