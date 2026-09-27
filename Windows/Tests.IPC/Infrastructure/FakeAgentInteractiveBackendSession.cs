using PasswordManagerLocal.Common.Contracts.Endpoints;
using PasswordManagerLocal.Common.Backend.Hosting;

namespace PasswordManagerLocal.Windows.Tests.IPC.Infrastructure;

internal sealed class FakeAgentInteractiveBackendSession : IInteractiveBackendSession
{
    private readonly ICollection<string>? _operationLog;
    private readonly TaskCompletionSource? _drainRelease;
    private int _disposed;

    public FakeAgentInteractiveBackendSession(
        IEndpoints endpoints,
        ICollection<string>? operationLog = null,
        TaskCompletionSource? drainRelease = null)
    {
        Endpoints = endpoints ?? throw new ArgumentNullException(nameof(endpoints));
        _operationLog = operationLog;
        _drainRelease = drainRelease;
    }

    public IEndpoints Endpoints { get; }
    public bool AcceptsNewOperations => Volatile.Read(ref _disposed) == 0;
    public bool IsClosing => Volatile.Read(ref _disposed) != 0;
    public int ActiveOperationCount { get; set; }
    public int DisposeCount { get; private set; }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        DisposeCount++;
        _operationLog?.Add("session-close-admission");
        if (_drainRelease is not null)
            await _drainRelease.Task;
        ActiveOperationCount = 0;
        _operationLog?.Add("session-dispose");
    }
}
