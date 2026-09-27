using PasswordManagerLocal.Common.Contracts.Requests;

namespace PasswordManagerLocal.Windows.EndpointRpc.Contracts.Requests;

public sealed class UpdateCustomUserColorEndpointRequest
{
    public Guid Token { get; set; }
    public UpdateCustomUserColorRequest Request { get; set; } = null!;
}
