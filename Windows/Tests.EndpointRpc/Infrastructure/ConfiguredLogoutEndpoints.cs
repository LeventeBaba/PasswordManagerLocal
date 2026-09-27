using PasswordManagerLocal.Common.Backend.Exceptions;

namespace PasswordManagerLocal.Windows.Tests.EndpointRpc.Infrastructure;

public sealed class ConfiguredLogoutEndpoints : ThrowingRecordingEndpoints
{
    private readonly Exception? _failure;

    public ConfiguredLogoutEndpoints(Exception? failure = null) =>
        _failure = failure;

    public int InvocationCount { get; private set; }

    public override Task LogoutAsync(Guid token, CancellationToken ct = default)
    {
        InvocationCount++;
        return _failure is null
            ? Task.CompletedTask
            : Task.FromException(_failure);
    }
}
