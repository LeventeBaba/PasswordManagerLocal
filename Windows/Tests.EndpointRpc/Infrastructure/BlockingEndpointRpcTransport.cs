using PasswordManagerLocal.Windows.EndpointRpc.Client;
using PasswordManagerLocal.Windows.EndpointRpc.Contracts;

namespace PasswordManagerLocal.Windows.Tests.EndpointRpc.Infrastructure;

public sealed class BlockingEndpointRpcTransport : IEndpointRpcTransport
{
    private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _firstSendStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _sendCount;
    private int _disposed;

    public bool IsConnected => Volatile.Read(ref _disposed) == 0;
    public Task Completion => _completion.Task;
    public Task FirstSendStarted => _firstSendStarted.Task;
    public int SendCount => Volatile.Read(ref _sendCount);

    public async Task<EndpointRpcTransportResponse> SendAsync(
        EndpointOperationId operationId,
        byte[] requestPayload,
        EndpointOperationCancellationClassification cancellationClassification,
        CancellationToken cancellationToken = default)
    {
        if (!IsConnected)
        {
            throw new EndpointRpcTransportException(
                1,
                EndpointRpcTransmissionState.DefinitelyNotSent,
                new EndpointRpcDisconnectedException());
        }

        if (Interlocked.Increment(ref _sendCount) == 1)
            _firstSendStarted.TrySetResult();

        try
        {
            await _release.Task.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException exception)
        {
            throw new EndpointRpcTransportException(
                SendCount,
                EndpointRpcTransmissionState.Sent,
                exception);
        }

        return EndpointRpcTransportResponse.Inline("{}"u8.ToArray());
    }

    public Task<byte[]> GetLargeResultChunkAsync(
        byte[] requestPayload,
        CancellationToken cancellationToken = default) =>
        Task.FromException<byte[]>(new NotSupportedException());

    public Task ReleaseLargeResultAsync(
        byte[] requestPayload,
        CancellationToken cancellationToken = default) => Task.CompletedTask;

    public void Release() => _release.TrySetResult();

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _release.TrySetResult();
            _completion.TrySetResult();
        }
        return ValueTask.CompletedTask;
    }
}
