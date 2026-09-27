namespace PasswordManagerLocal.Windows.EndpointRpc.Authorization;

public interface IEndpointRpcAdmissionPolicy
{
    bool CanAcceptConnection { get; }

    bool TryEnterRequest(out IDisposable? lease);
    Task WaitForDrainAsync(CancellationToken cancellationToken = default);
}