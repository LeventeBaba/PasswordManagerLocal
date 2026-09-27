namespace PasswordManagerLocal.Windows.Agent.Preferences;

public readonly record struct ApplicationPreferencesFileStamp(
    bool Exists,
    long Length,
    DateTime LastWriteTimeUtc)
{
    public static ApplicationPreferencesFileStamp Unknown { get; } =
        new(false, -1, DateTime.MinValue);
}
