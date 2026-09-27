using Google.Protobuf;

namespace PasswordManagerLocal.Common.Backend.Sync;

public static class DeltaMapping
{
    public static DeltaChunk ToProto(NetworkDelta d)
    {
        return new DeltaChunk
        {
            Entity = d.Entity,
            Payload = ByteString.CopyFrom(d.Payload ?? []),
            Ts = d.Ts,
            DeviceId = d.DeviceId ?? string.Empty,
            SignPub = ByteString.CopyFrom(d.SignPub ?? []),
            Sig = ByteString.CopyFrom(d.Sig ?? []),
            RecipientDeviceId = d.RecipientDeviceId ?? string.Empty,
            EncryptionVersion = d.EncryptionVersion,
            EphemeralPublicKey = ByteString.CopyFrom(d.EphemeralPublicKey ?? []),
            Nonce = ByteString.CopyFrom(d.Nonce ?? []),
            Tag = ByteString.CopyFrom(d.Tag ?? []),
            PayloadHash = ByteString.CopyFrom(d.PayloadHash ?? [])
        };
    }

    public static NetworkDelta FromProto(DeltaChunk p)
    {
        return new NetworkDelta
        {
            Entity = p.Entity ?? string.Empty,
            Payload = p.Payload?.ToByteArray() ?? [],
            Ts = p.Ts,
            DeviceId = p.DeviceId ?? string.Empty,
            SignPub = p.SignPub?.ToByteArray() ?? [],
            Sig = p.Sig?.ToByteArray() ?? [],
            RecipientDeviceId = p.RecipientDeviceId ?? string.Empty,
            EncryptionVersion = p.EncryptionVersion,
            EphemeralPublicKey = p.EphemeralPublicKey?.ToByteArray() ?? [],
            Nonce = p.Nonce?.ToByteArray() ?? [],
            Tag = p.Tag?.ToByteArray() ?? [],
            PayloadHash = p.PayloadHash?.ToByteArray() ?? []
        };
    }
}
