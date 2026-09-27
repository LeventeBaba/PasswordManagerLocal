using PasswordManagerLocal.Common.Contracts.Responses;

namespace PasswordManagerLocal.Windows.EndpointRpc.Contracts.Responses;

public sealed class GetSavedPasswordsEndpointResponse
{
    public SavedPasswordsResponse Passwords { get; set; } = new();
}
