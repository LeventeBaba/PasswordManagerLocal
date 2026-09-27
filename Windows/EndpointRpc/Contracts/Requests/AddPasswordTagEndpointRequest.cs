using PasswordManagerLocal.Common.Contracts.Requests;

namespace PasswordManagerLocal.Windows.EndpointRpc.Contracts.Requests;

public sealed class AddPasswordTagEndpointRequest
{
    public Guid Token { get; set; }
    public NewPasswordTagRequest Request { get; set; } = new();
}
