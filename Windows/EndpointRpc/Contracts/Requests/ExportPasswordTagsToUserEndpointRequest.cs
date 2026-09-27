using PasswordManagerLocal.Common.Contracts.Requests;

namespace PasswordManagerLocal.Windows.EndpointRpc.Contracts.Requests;

public sealed class ExportPasswordTagsToUserEndpointRequest
{
    public Guid SourceToken { get; set; }
    public ExportPasswordTagsToUserRequest Request { get; set; } = new();
}
