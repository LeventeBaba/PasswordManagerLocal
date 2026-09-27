using PasswordManagerLocal.Common.Backend.Abstractions.Services;

namespace PasswordManagerLocal.Common.Tests.Fakes;

public sealed class FakeInteractiveSensitiveStateResetter : IInteractiveSensitiveStateResetter
{
    public int ResetCalls { get; private set; }
    public Exception? ResetFailure { get; set; }

    public Task ResetAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ResetCalls++;
        return ResetFailure is null
            ? Task.CompletedTask
            : Task.FromException(ResetFailure);
    }
}
