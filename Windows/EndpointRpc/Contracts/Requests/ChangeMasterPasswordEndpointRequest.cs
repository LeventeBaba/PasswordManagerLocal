using PasswordManagerLocal.Common.Contracts.Requests;

namespace PasswordManagerLocal.Windows.EndpointRpc.Contracts.Requests;

public sealed class ChangeMasterPasswordEndpointRequest
{
    public MasterPasswordChangeRequest Request { get; set; } = null!;
}
