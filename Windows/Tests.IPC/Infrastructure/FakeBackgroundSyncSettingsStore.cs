using PasswordManagerLocal.Common.Contracts.Runtime;
using PasswordManagerLocal.Common.Contracts.BackgroundSync;

namespace PasswordManagerLocal.Windows.Tests.IPC.Infrastructure;

internal sealed class FakeBackgroundSyncSettingsStore : IBackgroundSyncSettingsStore
{
    public BackgroundSyncSettings Settings { get; set; } = new(false);
    public Exception? ReadFailure { get; set; }
    public Exception? WriteFailure { get; set; }
    public int ReadCount { get; private set; }
    public int WriteCount { get; private set; }
    public bool? LastWrittenValue { get; private set; }
    public TaskCompletionSource? WriteEntered { get; set; }
    public Task? WriteRelease { get; set; }

    public Task<BackgroundSyncSettings> ReadAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ReadCount++;
        return ReadFailure is null
            ? Task.FromResult(Settings)
            : Task.FromException<BackgroundSyncSettings>(ReadFailure);
    }

    public async Task WriteAsync(
        BackgroundSyncSettings settings,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        cancellationToken.ThrowIfCancellationRequested();
        WriteCount++;
        LastWrittenValue = settings.IsEnabled;
        WriteEntered?.TrySetResult();
        if (WriteRelease is not null)
            await WriteRelease.WaitAsync(cancellationToken);
        if (WriteFailure is not null)
            throw WriteFailure;
        Settings = settings;
    }
}
