using PasswordManagerLocal.Common.Contracts.Requests;

namespace PasswordManagerLocal.Windows.EndpointRpc.Contracts.Requests;

public sealed class RemoveLocalProfileEndpointRequest
{
    public LoginRequest Request { get; set; } = new();
    public bool AcknowledgeDataLoss { get; set; }
}
