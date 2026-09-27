using PasswordManagerLocal.Common.Contracts.Requests;

namespace PasswordManagerLocal.Windows.EndpointRpc.Contracts.Requests;

public sealed class ExportPasswordsToUserEndpointRequest
{
    public Guid SourceToken { get; set; }
    public ExportPasswordsToUserRequest Request { get; set; } = new();
}
