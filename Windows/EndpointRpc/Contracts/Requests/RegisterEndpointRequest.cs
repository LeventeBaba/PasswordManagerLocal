using PasswordManagerLocal.Common.Contracts.Requests;

namespace PasswordManagerLocal.Windows.EndpointRpc.Contracts.Requests;

public sealed class RegisterEndpointRequest
{
    public RegistrationRequest Request { get; set; } = new();
}
