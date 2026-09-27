using PasswordManagerLocal.Common.Contracts.Endpoints;
using PasswordManagerLocal.Common.Backend.Hosting;

namespace PasswordManagerLocal.Common.Tests.Fakes;

public sealed class FakeInteractiveBackendSession : IInteractiveBackendSession
{
    private readonly Func<Task>? _dispose;
    private IEndpoints? _endpoints;
    private Task? _disposeTask;

    public FakeInteractiveBackendSession(
        IEndpoints endpoints,
        Func<Task>? dispose = null)
    {
        _endpoints = endpoints ?? throw new ArgumentNullException(nameof(endpoints));
        _dispose = dispose;
    }

    public IEndpoints Endpoints => _endpoints
        ?? throw new ObjectDisposedException(nameof(FakeInteractiveBackendSession));

    public bool IsDisposed => _endpoints is null;
    public bool AcceptsNewOperations => !IsDisposed;
    public bool IsClosing => IsDisposed;
    public int ActiveOperationCount => 0;

    public ValueTask DisposeAsync()
    {
        _disposeTask ??= DisposeCoreAsync();
        return new ValueTask(_disposeTask);
    }

    private async Task DisposeCoreAsync()
    {
        if (_endpoints is null)
            return;

        _endpoints = null;
        if (_dispose is not null)
            await _dispose();
    }
}
