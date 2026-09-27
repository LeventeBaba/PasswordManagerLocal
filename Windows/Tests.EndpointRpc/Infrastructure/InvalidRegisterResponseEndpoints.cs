using PasswordManagerLocal.Common.Contracts.Requests;

namespace PasswordManagerLocal.Windows.Tests.EndpointRpc.Infrastructure;

public sealed class InvalidRegisterResponseEndpoints : ThrowingRecordingEndpoints
{
    public int InvocationCount { get; private set; }

    public override Task<Guid> RegisterAsync(
        RegistrationRequest request,
        CancellationToken ct = default)
    {
        InvocationCount++;
        return Task.FromResult(Guid.Empty);
    }
}
