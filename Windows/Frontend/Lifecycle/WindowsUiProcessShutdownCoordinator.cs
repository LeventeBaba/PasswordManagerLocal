using PasswordManagerLocal.Windows.Frontend.Activation;
using PasswordManagerLocal.Windows.Ipc.Coordination;

namespace PasswordManagerLocal.Windows.Frontend.Lifecycle;

public sealed class WindowsUiProcessShutdownCoordinator
{
    private readonly TimeSpan _activationShutdownTimeout;
    private readonly TimeSpan _backendShutdownTimeout;

    public WindowsUiProcessShutdownCoordinator(
        TimeSpan? activationShutdownTimeout = null,
        TimeSpan? backendShutdownTimeout = null)
    {
        _activationShutdownTimeout = activationShutdownTimeout ?? TimeSpan.FromSeconds(1);
        _backendShutdownTimeout = backendShutdownTimeout ?? TimeSpan.FromSeconds(2);
        if (_activationShutdownTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(activationShutdownTimeout));
        if (_backendShutdownTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(backendShutdownTimeout));
    }

    public async Task ShutdownAsync(
        IWindowsUiActivationServerLifetime activationServer,
        IProcessInstanceLock processLock,
        IAsyncDisposable backendClient)
    {
        StopAcceptanceAndReleaseLock(activationServer, processLock);
        await CompleteCleanupAsync(activationServer, backendClient);
    }

    public void StopAcceptanceAndReleaseLock(
        IWindowsUiActivationServerLifetime activationServer,
        IProcessInstanceLock processLock)
    {
        ArgumentNullException.ThrowIfNull(activationServer);
        ArgumentNullException.ThrowIfNull(processLock);

        try
        {
            activationServer.RequestStop();
        }
        catch
        {
        }

        try
        {
            processLock.Dispose();
        }
        catch
        {
        }
    }

    public async Task CompleteCleanupAsync(
        IWindowsUiActivationServerLifetime activationServer,
        IAsyncDisposable backendClient)
    {
        ArgumentNullException.ThrowIfNull(activationServer);
        ArgumentNullException.ThrowIfNull(backendClient);

        await CompleteBestEffortAsync(
            () => activationServer.DisposeAsync().AsTask(),
            _activationShutdownTimeout);
        await CompleteBestEffortAsync(
            () => backendClient.DisposeAsync().AsTask(),
            _backendShutdownTimeout);
    }

    private static async Task CompleteBestEffortAsync(
        Func<Task> operationFactory,
        TimeSpan timeout)
    {
        Task operation;
        try
        {
            operation = Task.Factory.StartNew(
                    operationFactory,
                    CancellationToken.None,
                    TaskCreationOptions.DenyChildAttach,
                    TaskScheduler.Default)
                .Unwrap();
        }
        catch
        {
            return;
        }

        try
        {
            if (operation.IsCompleted)
            {
                await operation;
                return;
            }

            if (ReferenceEquals(
                await Task.WhenAny(operation, Task.Delay(timeout)),
                operation))
            {
                await operation;
                return;
            }

            _ = ObserveCompletionAsync(operation);
        }
        catch
        {
        }
    }

    private static async Task ObserveCompletionAsync(Task operation)
    {
        try
        {
            await operation;
        }
        catch
        {
        }
    }
}
