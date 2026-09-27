using PasswordManagerLocal.Common.Frontend.Services;

namespace PasswordManagerLocal.Windows.Tests.IPC.Infrastructure;

internal sealed class FakeApplicationPreferencesChangeNotifier : IApplicationPreferencesChangeNotifier
{
    public int NotifyCount { get; private set; }
    public Exception? Failure { get; set; }
    public ICollection<string>? OperationLog { get; set; }

    public Task NotifyLanguagePersistedAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        NotifyCount++;
        OperationLog?.Add("notify");
        return Failure is null ? Task.CompletedTask : Task.FromException(Failure);
    }
}
