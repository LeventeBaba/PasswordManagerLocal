using PasswordManagerLocal.Windows.Frontend.Activation;
using PasswordManagerLocal.Windows.Ipc.Coordination;

namespace PasswordManagerLocal.Windows.Frontend.Lifecycle;

public sealed class WindowsUiProcessExitController
{
    private readonly IWindowsUiActivationServerLifetime _activationServer;
    private readonly IProcessInstanceLock _processLock;
    private readonly IAsyncDisposable _backendClient;
    private readonly WindowsUiProcessShutdownCoordinator _shutdownCoordinator;
    private readonly Action<int> _terminateProcess;
    private readonly object _gate = new();
    private Task _completion = Task.CompletedTask;
    private int _exitRequested;

    public WindowsUiProcessExitController(
        IWindowsUiActivationServerLifetime activationServer,
        IProcessInstanceLock processLock,
        IAsyncDisposable backendClient,
        WindowsUiProcessShutdownCoordinator? shutdownCoordinator = null,
        Action<int>? terminateProcess = null)
    {
        _activationServer = activationServer ?? throw new ArgumentNullException(nameof(activationServer));
        _processLock = processLock ?? throw new ArgumentNullException(nameof(processLock));
        _backendClient = backendClient ?? throw new ArgumentNullException(nameof(backendClient));
        _shutdownCoordinator = shutdownCoordinator ?? new WindowsUiProcessShutdownCoordinator();
        _terminateProcess = terminateProcess ?? (exitCode => Environment.Exit(exitCode));
    }

    public bool IsExitRequested => Volatile.Read(ref _exitRequested) != 0;

    public Task Completion
    {
        get
        {
            lock (_gate)
                return _completion;
        }
    }

    public void RequestExit(int exitCode = 0)
    {
        if (Interlocked.Exchange(ref _exitRequested, 1) != 0)
            return;

        _shutdownCoordinator.StopAcceptanceAndReleaseLock(
            _activationServer,
            _processLock);

        var completion = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
            _completion = completion.Task;

        try
        {
            var exitThread = new Thread(() => CompleteExit(exitCode, completion))
            {
                IsBackground = false,
                Name = "PasswordManagerLocal.Windows.Exit"
            };
            exitThread.Start();
        }
        catch
        {
            completion.TrySetResult();
            _terminateProcess(exitCode);
        }
    }

    private void CompleteExit(
        int exitCode,
        TaskCompletionSource completion)
    {
        try
        {
            _shutdownCoordinator.CompleteCleanupAsync(
                    _activationServer,
                    _backendClient)
                .GetAwaiter()
                .GetResult();
        }
        catch
        {
        }
        finally
        {
            try
            {
                _terminateProcess(exitCode);
            }
            finally
            {
                completion.TrySetResult();
            }
        }
    }
}
