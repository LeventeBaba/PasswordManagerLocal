namespace PasswordManagerLocal.Windows.Ipc.Protocol.Framing;

public sealed record IpcFrame(
    IpcFrameHeader Header,
    byte[] Payload);
