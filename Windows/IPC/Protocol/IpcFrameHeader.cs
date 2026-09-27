namespace PasswordManagerLocal.Windows.Ipc.Protocol;

public sealed record IpcFrameHeader(
    int ProtocolVersion,
    IpcMessageKind MessageKind,
    IpcFrameFlags Flags,
    long CorrelationId,
    int PayloadLength);
