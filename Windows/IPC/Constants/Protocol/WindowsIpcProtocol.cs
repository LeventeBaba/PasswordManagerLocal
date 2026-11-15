namespace PasswordManagerLocal.Windows.Ipc.Constants.Protocol;

public static class WindowsIpcProtocol
{
    public const uint Magic = 0x504D4C49;
    // v6 adds strict idle/starting/retrying/restart-required background status values.
    public const int CurrentVersion = 6;
    public const int HeaderSize = 24;
    public const int MaximumPayloadSize = 3 * 1024 * 1024;

    // Header fields are encoded in network byte order: magic, version, kind,
    // correlation ID, then payload length.
}
