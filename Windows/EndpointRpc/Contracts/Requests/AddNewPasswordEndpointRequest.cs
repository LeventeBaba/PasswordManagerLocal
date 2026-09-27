using PasswordManagerLocal.Common.Contracts.Requests;

namespace PasswordManagerLocal.Windows.EndpointRpc.Contracts.Requests;

public sealed class AddNewPasswordEndpointRequest
{
    public Guid Token { get; set; }
    public NewPasswordRequest Request { get; set; } = new();
}
