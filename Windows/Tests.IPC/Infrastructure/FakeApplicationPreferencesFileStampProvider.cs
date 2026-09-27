using PasswordManagerLocal.Windows.Agent.Preferences;

namespace PasswordManagerLocal.Windows.Tests.IPC.Infrastructure;

internal sealed class FakeApplicationPreferencesFileStampProvider : IApplicationPreferencesFileStampProvider
{
    public ApplicationPreferencesFileStamp Stamp { get; set; }
    public Exception? Failure { get; set; }
    public int ReadCount { get; private set; }

    public ApplicationPreferencesFileStamp GetStamp()
    {
        ReadCount++;
        if (Failure is not null)
            throw Failure;
        return Stamp;
    }
}
