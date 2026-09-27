using PasswordManagerLocal.Common.Contracts.Responses;

namespace PasswordManagerLocal.Windows.EndpointRpc.Contracts.Responses;

public sealed class GetUserProfileInfoEndpointResponse
{
    public UserProfileInfoResponse Profile { get; set; } = new();
}
