using PasswordManagerLocal.Windows.EndpointRpc.Client;
using PasswordManagerLocal.Windows.EndpointRpc.Contracts;

namespace PasswordManagerLocal.Windows.Tests.EndpointRpc.Infrastructure;

public sealed class DelayedCompletionEndpointRpcTransport : IEndpointRpcTransport
{
    private readonly TaskCompletionSource _completion = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _disposed;

    public bool IsConnected => !_disposed && !_completion.Task.IsCompleted;
    public bool IsDisposed => _disposed;
    public Task Completion => _completion.Task;

    public Task<EndpointRpcTransportResponse> SendAsync(
        EndpointOperationId operationId,
        byte[] requestPayload,
        EndpointOperationCancellationClassification cancellationClassification,
        CancellationToken cancellationToken = default) =>
        IsConnected
            ? Task.FromResult(EndpointRpcTransportResponse.Inline(Array.Empty<byte>()))
            : Task.FromException<EndpointRpcTransportResponse>(new EndpointRpcDisconnectedException());

    public Task<byte[]> GetLargeResultChunkAsync(
        byte[] requestPayload,
        CancellationToken cancellationToken = default) =>
        Task.FromException<byte[]>(new NotSupportedException());

    public Task ReleaseLargeResultAsync(
        byte[] requestPayload,
        CancellationToken cancellationToken = default) => Task.CompletedTask;

    public void Complete(Exception? failure = null)
    {
        if (failure is null)
            _completion.TrySetResult();
        else
            _completion.TrySetException(failure);
    }

    public ValueTask DisposeAsync()
    {
        _disposed = true;
        return ValueTask.CompletedTask;
    }
}
