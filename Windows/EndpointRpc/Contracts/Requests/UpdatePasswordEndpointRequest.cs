using PasswordManagerLocal.Common.Contracts.Requests;

namespace PasswordManagerLocal.Windows.EndpointRpc.Contracts.Requests;

public sealed class UpdatePasswordEndpointRequest
{
    public Guid Token { get; set; }
    public UpdatePasswordRequest Request { get; set; } = null!;
}
