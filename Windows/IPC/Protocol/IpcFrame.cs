namespace PasswordManagerLocal.Windows.Ipc.Protocol;

public sealed record IpcFrame(
    IpcFrameHeader Header,
    byte[] Payload);
