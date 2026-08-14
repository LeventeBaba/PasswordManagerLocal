using PasswordManagerLocal.Common.Contracts.Constants;

namespace PasswordManagerLocal.Windows.Agent.Preferences;

public sealed class PhysicalApplicationPreferencesFileStampProvider : IApplicationPreferencesFileStampProvider
{
    private readonly string _path;

    public PhysicalApplicationPreferencesFileStampProvider(string applicationDataDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(applicationDataDirectory);
        _path = Path.Combine(
            Path.GetFullPath(applicationDataDirectory),
            ApplicationFileNames.ApplicationPreferencesFileName);
    }

    public ApplicationPreferencesFileStamp GetStamp()
    {
        var info = new FileInfo(_path);
        return info.Exists
            ? new(true, info.Length, info.LastWriteTimeUtc)
            : new(false, 0, DateTime.MinValue);
    }
}
