using PasswordManagerLocal.Common.Contracts.Runtime;
using PasswordManagerLocal.Common.Contracts.BackgroundSync;

namespace PasswordManagerLocal.Common.Tests.Fakes;

public sealed class FakeBackgroundSyncSettingsStore : IBackgroundSyncSettingsStore
{
    public FakeBackgroundSyncSettingsStore(bool isEnabled = false)
    {
        Current = new BackgroundSyncSettings(isEnabled);
    }

    public BackgroundSyncSettings Current { get; private set; }
    public Exception? ReadFailure { get; set; }
    public Exception? WriteFailure { get; set; }
    public bool CommitBeforeWriteFailure { get; set; }
    public int ReadCalls { get; private set; }
    public int WriteCalls { get; private set; }

    public Task<BackgroundSyncSettings> ReadAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ReadCalls++;
        if (ReadFailure is not null)
            throw ReadFailure;
        return Task.FromResult(Current);
    }

    public Task WriteAsync(
        BackgroundSyncSettings settings,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        cancellationToken.ThrowIfCancellationRequested();
        WriteCalls++;
        if (WriteFailure is not null)
        {
            if (CommitBeforeWriteFailure)
                Current = settings;
            throw WriteFailure;
        }

        Current = settings;
        return Task.CompletedTask;
    }
}
