using Google.Protobuf;

namespace PasswordManagerLocal.Common.Backend.Sync.Tcp;

public sealed class SyncTcpFrame
{
    public SyncTcpFrame(SyncTcpMessageType type, byte[] payload)
    {
        Type = type;
        Payload = payload;
    }

    public SyncTcpMessageType Type { get; }
    public byte[] Payload { get; }

    public T Parse<T>(MessageParser<T> parser) where T : IMessage<T> =>
        parser.ParseFrom(Payload);
}
