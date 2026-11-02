namespace PasswordManagerLocal.Windows.EndpointRpc.Contracts.Requests;

public sealed class ConfirmSelfDiagnosticsRepairEndpointRequest
{
    public Guid Token { get; set; }
    public string PlanId { get; set; } = string.Empty;
    public byte[] MasterPassword { get; set; } = [];
}
