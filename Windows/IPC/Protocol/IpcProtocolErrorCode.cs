namespace PasswordManagerLocal.Windows.Ipc.Protocol;

public enum IpcProtocolErrorCode
{
    InvalidMagic = 1,
    UnsupportedVersion = 2,
    UnknownMessageKind = 3,
    InvalidFlags = 4, // Reserved from protocol v4; protocol v5 carries no flags field.
    InvalidCorrelationId = 5,
    InvalidPayloadLength = 6,
    TruncatedHeader = 7,
    TruncatedPayload = 8,
    InvalidEnvelope = 9,
    UnexpectedMessageKind = 10
}
