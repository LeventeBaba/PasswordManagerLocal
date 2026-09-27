using PasswordManagerLocal.Common.Contracts.Requests;

namespace PasswordManagerLocal.Windows.EndpointRpc.Contracts.Requests;

public sealed class ExportCustomUserColorsToUserEndpointRequest
{
    public Guid SourceToken { get; set; }
    public ExportCustomUserColorsToUserRequest Request { get; set; } = new();
}
