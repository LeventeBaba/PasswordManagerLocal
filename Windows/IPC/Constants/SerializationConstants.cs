namespace PasswordManagerLocal.Windows.Ipc.Constants;

public static class SerializationConstants
{
    public const int IpcFramePayloadCodecOperationIdPrefixSize = sizeof(int);
    public const int IpcFramePayloadCodecResponseKindPrefixSize = sizeof(byte);
}
