using PasswordManagerLocal.Common.Contracts.Requests;

namespace PasswordManagerLocal.Windows.EndpointRpc.Contracts.Requests;

public sealed class UpdatePasswordTagEndpointRequest
{
    public Guid Token { get; set; }
    public UpdatePasswordTagRequest Request { get; set; } = null!;
}
