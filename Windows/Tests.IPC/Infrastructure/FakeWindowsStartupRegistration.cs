using PasswordManagerLocal.Windows.Agent.Background;

namespace PasswordManagerLocal.Windows.Tests.IPC.Infrastructure;

internal sealed class FakeWindowsStartupRegistration : IWindowsStartupRegistration
{
    public WindowsStartupRegistrationSnapshot Snapshot { get; set; } = new(false, false, null);
    public Exception? ReadFailure { get; set; }
    public Exception? RegisterFailure { get; set; }
    public Exception? UnregisterFailure { get; set; }
    public Exception? RestoreFailure { get; set; }
    public int ReadCount { get; private set; }
    public int RegisterCount { get; private set; }
    public int UnregisterCount { get; private set; }
    public int RestoreCount { get; private set; }
    public TaskCompletionSource? RegisterEntered { get; set; }
    public Task? RegisterRelease { get; set; }

    public Task<WindowsStartupRegistrationSnapshot> ReadAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ReadCount++;
        return ReadFailure is null
            ? Task.FromResult(Snapshot)
            : Task.FromException<WindowsStartupRegistrationSnapshot>(ReadFailure);
    }

    public async Task RegisterAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        RegisterCount++;
        RegisterEntered?.TrySetResult();
        if (RegisterRelease is not null)
            await RegisterRelease.WaitAsync(cancellationToken);
        if (RegisterFailure is not null)
            throw RegisterFailure;
        Snapshot = new WindowsStartupRegistrationSnapshot(
            EntryExists: true,
            IsRegistered: true,
            Command: "agent --background");
    }

    public Task UnregisterAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        UnregisterCount++;
        if (UnregisterFailure is not null)
            return Task.FromException(UnregisterFailure);
        Snapshot = new WindowsStartupRegistrationSnapshot(false, false, null);
        return Task.CompletedTask;
    }

    public Task RestoreAsync(
        WindowsStartupRegistrationSnapshot snapshot,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        cancellationToken.ThrowIfCancellationRequested();
        RestoreCount++;
        if (RestoreFailure is not null)
            return Task.FromException(RestoreFailure);
        Snapshot = snapshot;
        return Task.CompletedTask;
    }
}
