using PasswordManagerLocal.Windows.EndpointRpc.Authorization;

namespace PasswordManagerLocal.Windows.Tests.EndpointRpc.Infrastructure;

public sealed class FakeEndpointUiRegistrationResolver : IEndpointUiRegistrationResolver
{
    public bool IsRegisteredResult { get; set; } = true;
    public long RegistrationGeneration { get; set; } = 1;
    public int? ExpectedProcessId { get; set; }
    public int? ExpectedWindowsSessionId { get; set; }
    public Guid? ExpectedInstanceId { get; set; }

    public bool TryResolve(
        int processId,
        int windowsSessionId,
        Guid instanceId,
        out long registrationGeneration)
    {
        var matches = IsRegisteredResult && Matches(processId, windowsSessionId, instanceId);
        registrationGeneration = matches ? RegistrationGeneration : 0;
        return matches;
    }

    public bool IsCurrent(
        int processId,
        int windowsSessionId,
        Guid instanceId,
        long registrationGeneration) =>
        IsRegisteredResult &&
        registrationGeneration == RegistrationGeneration &&
        Matches(processId, windowsSessionId, instanceId);

    private bool Matches(int processId, int windowsSessionId, Guid instanceId) =>
        (!ExpectedProcessId.HasValue || ExpectedProcessId.Value == processId) &&
        (!ExpectedWindowsSessionId.HasValue || ExpectedWindowsSessionId.Value == windowsSessionId) &&
        (!ExpectedInstanceId.HasValue || ExpectedInstanceId.Value == instanceId);
}
