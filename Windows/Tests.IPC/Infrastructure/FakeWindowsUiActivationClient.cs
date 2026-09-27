using PasswordManagerLocal.Windows.Ipc.Client;
using PasswordManagerLocal.Windows.Ipc.Contracts;

namespace PasswordManagerLocal.Windows.Tests.IPC.Infrastructure;

internal sealed class FakeWindowsUiActivationClient : IWindowsUiActivationClient
{
    public UiActivationResult Result { get; set; } = new(
        UiActivationResultKind.Activated,
        "accepted");
    public UiActivationRequestDto? LastRequest { get; private set; }
    public int RequestCount { get; private set; }
    public TaskCompletionSource<UiActivationResult>? Completion { get; set; }

    public Task<UiActivationResult> TryActivateAsync(
        UiActivationRequestDto request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        LastRequest = request;
        RequestCount++;
        return Completion is null
            ? Task.FromResult(Result)
            : Completion.Task.WaitAsync(cancellationToken);
    }
}
