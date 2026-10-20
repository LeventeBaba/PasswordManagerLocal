namespace PasswordManagerLocal.Windows.Ipc.Protocol.Framing;

public sealed record IpcFrameHeader(
    int ProtocolVersion,
    IpcMessageKind MessageKind,
    long CorrelationId,
    int PayloadLength);
