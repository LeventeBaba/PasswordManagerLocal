namespace PasswordManagerLocal.Windows.Ipc.Protocol;

public sealed record IpcFrameHeader(
    int ProtocolVersion,
    IpcMessageKind MessageKind,
    long CorrelationId,
    int PayloadLength);
