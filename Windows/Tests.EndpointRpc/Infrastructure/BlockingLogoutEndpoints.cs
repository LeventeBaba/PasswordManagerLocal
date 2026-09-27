namespace PasswordManagerLocal.Windows.Tests.EndpointRpc.Infrastructure;

public sealed class BlockingLogoutEndpoints : ThrowingRecordingEndpoints
{
    private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task Started => _started.Task;
    public int InvocationCount { get; private set; }

    public override async Task LogoutAsync(Guid token, CancellationToken ct = default)
    {
        InvocationCount++;
        _started.TrySetResult();
        await _release.Task;
    }

    public void Release() => _release.TrySetResult();
}
