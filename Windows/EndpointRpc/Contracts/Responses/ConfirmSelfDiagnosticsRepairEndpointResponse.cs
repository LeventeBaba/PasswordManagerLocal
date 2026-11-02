using PasswordManagerLocal.Common.Contracts.Responses;

namespace PasswordManagerLocal.Windows.EndpointRpc.Contracts.Responses;

public sealed class ConfirmSelfDiagnosticsRepairEndpointResponse
{
    public SelfDiagnosticsResultResponse Result { get; set; } = new();
}
