using PasswordManagerLocal.Common.Backend.Abstractions.Persistence;

namespace PasswordManagerLocal.Common.Tests.Fakes;

internal sealed class FakeUnitOfWorkTransaction : IUnitOfWorkTransaction
{
    public Task CommitAsync(CancellationToken ct = default) => Task.CompletedTask;
    public Task RollbackAsync(CancellationToken ct = default) => Task.CompletedTask;
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
