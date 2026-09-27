using PasswordManagerLocal.Windows.Agent.Ui;

namespace PasswordManagerLocal.Windows.Tests.IPC.Infrastructure;

internal sealed class FakeWindowsUiCloseService : IWindowsUiCloseService
{
    public WindowsUiCloseResult Result { get; set; } = new(
        WindowsUiCloseResultKind.Acknowledged,
        "Acknowledged.");
    public int RequestCount { get; private set; }
    public Exception? Failure { get; set; }
    public TaskCompletionSource<WindowsUiCloseResult>? Completion { get; set; }
    public ICollection<string>? OperationLog { get; set; }

    public Task<WindowsUiCloseResult> RequestIntentionalShutdownAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        RequestCount++;
        OperationLog?.Add("ui-close-request");
        if (Failure is not null)
            return Task.FromException<WindowsUiCloseResult>(Failure);
        return Completion is null
            ? Task.FromResult(Result)
            : Completion.Task.WaitAsync(cancellationToken);
    }
}
