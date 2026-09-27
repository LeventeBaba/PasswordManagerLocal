using PasswordManagerLocal.Common.Backend.Constants;
using PasswordManagerLocal.Common.Backend.Models;
using PasswordManagerLocal.Common.Backend.Utils;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace PasswordManagerLocal.Common.Backend.Sync.Discovery.Protocol;

internal static class LocalDiscoveryPacketCodec
{
    private static readonly byte[] Magic = "PMLD"u8.ToArray();
    private const int MaxSessionIdBytes = 32;

    public static byte[] BuildSyncQueryAuthenticatedBytes(long unixTimeSeconds, byte[] nonce, Guid requesterDeviceId)
    {
        ValidateNonce(nonce);
        return BuildPacketPrefix(LocalDiscoveryMessageType.SyncQuery, writer =>
        {
            writer.Write(unixTimeSeconds);
            writer.Write(nonce);
            writer.Write(requesterDeviceId.ToByteArray());
        });
    }


    public static byte[] BuildSyncResponseAuthenticatedBytes(
        long unixTimeSeconds,
        byte[] queryNonce,
        Guid requesterDeviceId,
        Guid responderDeviceId,
        byte[] tlsFingerprint,
        IPAddress responderAddress)
    {
        ValidateNonce(queryNonce);
        ValidateFingerprint(tlsFingerprint);
        ValidateResponderAddress(responderAddress);

        return BuildPacketPrefix(LocalDiscoveryMessageType.SyncResponse, writer =>
        {
            writer.Write(unixTimeSeconds);
            writer.Write(queryNonce);
            writer.Write(requesterDeviceId.ToByteArray());
            writer.Write(responderDeviceId.ToByteArray());
            writer.Write(tlsFingerprint);
            writer.Write(responderAddress.GetAddressBytes());
        });
    }


    public static byte[] BuildEnrollmentQueryAuthenticatedBytes(long unixTimeSeconds, byte[] nonce, string sessionId)
    {
        ValidateNonce(nonce);
        return BuildPacketPrefix(LocalDiscoveryMessageType.EnrollmentQuery, writer =>
        {
            writer.Write(unixTimeSeconds);
            writer.Write(nonce);
            WriteSessionId(writer, sessionId);
        });
    }


    public static byte[] BuildEnrollmentResponseAuthenticatedBytes(
        long unixTimeSeconds,
        byte[] queryNonce,
        string sessionId,
        Guid deviceId,
        Guid originInstanceId,
        DeviceType deviceType,
        byte[] tlsFingerprint,
        byte[] signPublicKey,
        byte[] agreementPublicKey,
        IPAddress responderAddress)
    {
        ValidateNonce(queryNonce);
        ValidateFingerprint(tlsFingerprint);
        ValidateResponderAddress(responderAddress);

        if (deviceId == Guid.Empty || originInstanceId == Guid.Empty || !DeviceTypeDetector.IsValid(deviceType))
            throw new ArgumentException("The enrollment installation identity is invalid.");

        if (signPublicKey.Length != SyncConstants.SyncDeltaEd25519PublicKeyBytes)
            throw new ArgumentException("The signing public key has an invalid length.", nameof(signPublicKey));

        if (agreementPublicKey.Length != SyncConstants.SyncDeltaX25519PublicKeyBytes)
            throw new ArgumentException("The agreement public key has an invalid length.", nameof(agreementPublicKey));

        return BuildPacketPrefix(LocalDiscoveryMessageType.EnrollmentResponse, writer =>
        {
            writer.Write(unixTimeSeconds);
            writer.Write(queryNonce);
            WriteSessionId(writer, sessionId);
            writer.Write(deviceId.ToByteArray());
            writer.Write(originInstanceId.ToByteArray());
            writer.Write((byte)deviceType);
            writer.Write(tlsFingerprint);
            writer.Write(signPublicKey);
            writer.Write(agreementPublicKey);
            writer.Write(responderAddress.GetAddressBytes());
        });
    }


    public static byte[] AppendAuthenticator(byte[] authenticatedBytes, byte[] authenticator, int expectedLength)
    {
        if (authenticatedBytes.Length == 0)
            throw new ArgumentException("Authenticated packet bytes are missing.", nameof(authenticatedBytes));

        if (authenticator.Length != expectedLength)
            throw new ArgumentException("The packet authenticator has an invalid length.", nameof(authenticator));

        if (authenticatedBytes.Length + authenticator.Length > SyncConstants.LocalDiscoveryMaxPacketBytes)
            throw new InvalidDataException("The local discovery packet is too large.");

        var packet = new byte[authenticatedBytes.Length + authenticator.Length];
        Buffer.BlockCopy(authenticatedBytes, 0, packet, 0, authenticatedBytes.Length);
        Buffer.BlockCopy(authenticator, 0, packet, authenticatedBytes.Length, authenticator.Length);
        return packet;
    }


    public static bool TryReadMessageType(ReadOnlySpan<byte> payload, out LocalDiscoveryMessageType messageType)
    {
        messageType = default;

        if (payload.Length < Magic.Length + 2 || payload.Length > SyncConstants.LocalDiscoveryMaxPacketBytes)
            return false;

        if (!payload[..Magic.Length].SequenceEqual(Magic) || payload[Magic.Length] != SyncConstants.LocalDiscoveryProtocolVersion)
            return false;

        var rawType = payload[Magic.Length + 1];
        if (!Enum.IsDefined(typeof(LocalDiscoveryMessageType), rawType))
            return false;

        messageType = (LocalDiscoveryMessageType)rawType;
        return true;
    }


    public static bool TryDecodeSyncQuery(byte[] payload, out SyncDiscoveryQueryPacket packet)
    {
        packet = new SyncDiscoveryQueryPacket();
        if (!TryPrepareReader(payload, LocalDiscoveryMessageType.SyncQuery, SyncConstants.LocalDiscoverySignatureBytes, out var reader, out var authenticatedLength))
            return false;

        using (reader)
        {
            try
            {
                var unixTimeSeconds = reader.ReadInt64();
                var nonce = ReadExact(reader, SyncConstants.LocalDiscoveryNonceBytes);
                var requesterDeviceId = new Guid(ReadExact(reader, 16));

                if (reader.BaseStream.Position != authenticatedLength)
                    return false;

                var signature = ReadExact(reader, SyncConstants.LocalDiscoverySignatureBytes);
                if (reader.BaseStream.Position != reader.BaseStream.Length)
                    return false;

                packet = new SyncDiscoveryQueryPacket
                {
                    UnixTimeSeconds = unixTimeSeconds,
                    Nonce = nonce,
                    RequesterDeviceId = requesterDeviceId,
                    AuthenticatedBytes = payload[..authenticatedLength],
                    Signature = signature
                };
                return true;
            }
            catch
            {
                return false;
            }
        }
    }


    public static bool TryDecodeSyncResponse(byte[] payload, out SyncDiscoveryResponsePacket packet)
    {
        packet = new SyncDiscoveryResponsePacket();
        if (!TryPrepareReader(payload, LocalDiscoveryMessageType.SyncResponse, SyncConstants.LocalDiscoverySignatureBytes, out var reader, out var authenticatedLength))
            return false;

        using (reader)
        {
            try
            {
                var unixTimeSeconds = reader.ReadInt64();
                var queryNonce = ReadExact(reader, SyncConstants.LocalDiscoveryNonceBytes);
                var requesterDeviceId = new Guid(ReadExact(reader, 16));
                var responderDeviceId = new Guid(ReadExact(reader, 16));
                var tlsFingerprint = ReadExact(reader, 32);
                var responderAddress = new IPAddress(ReadExact(reader, 4));

                if (reader.BaseStream.Position != authenticatedLength)
                    return false;

                var signature = ReadExact(reader, SyncConstants.LocalDiscoverySignatureBytes);
                if (reader.BaseStream.Position != reader.BaseStream.Length)
                    return false;

                packet = new SyncDiscoveryResponsePacket
                {
                    UnixTimeSeconds = unixTimeSeconds,
                    QueryNonce = queryNonce,
                    RequesterDeviceId = requesterDeviceId,
                    ResponderDeviceId = responderDeviceId,
                    TlsFingerprint = tlsFingerprint,
                    ResponderAddress = responderAddress,
                    AuthenticatedBytes = payload[..authenticatedLength],
                    Signature = signature
                };
                return true;
            }
            catch
            {
                return false;
            }
        }
    }


    public static bool TryDecodeEnrollmentQuery(byte[] payload, out EnrollmentDiscoveryQueryPacket packet)
    {
        packet = new EnrollmentDiscoveryQueryPacket();
        if (!TryPrepareReader(payload, LocalDiscoveryMessageType.EnrollmentQuery, SyncConstants.LocalDiscoveryMacBytes, out var reader, out var authenticatedLength))
            return false;

        using (reader)
        {
            try
            {
                var unixTimeSeconds = reader.ReadInt64();
                var nonce = ReadExact(reader, SyncConstants.LocalDiscoveryNonceBytes);
                var sessionId = ReadSessionId(reader);

                if (reader.BaseStream.Position != authenticatedLength)
                    return false;

                var mac = ReadExact(reader, SyncConstants.LocalDiscoveryMacBytes);
                if (reader.BaseStream.Position != reader.BaseStream.Length)
                    return false;

                packet = new EnrollmentDiscoveryQueryPacket
                {
                    UnixTimeSeconds = unixTimeSeconds,
                    Nonce = nonce,
                    SessionId = sessionId,
                    AuthenticatedBytes = payload[..authenticatedLength],
                    Mac = mac
                };
                return true;
            }
            catch
            {
                return false;
            }
        }
    }


    public static bool TryDecodeEnrollmentResponse(byte[] payload, out EnrollmentDiscoveryResponsePacket packet)
    {
        packet = new EnrollmentDiscoveryResponsePacket();
        if (!TryPrepareReader(payload, LocalDiscoveryMessageType.EnrollmentResponse, SyncConstants.LocalDiscoveryMacBytes, out var reader, out var authenticatedLength))
            return false;

        using (reader)
        {
            try
            {
                var unixTimeSeconds = reader.ReadInt64();
                var queryNonce = ReadExact(reader, SyncConstants.LocalDiscoveryNonceBytes);
                var sessionId = ReadSessionId(reader);
                var deviceId = new Guid(ReadExact(reader, 16));
                var originInstanceId = new Guid(ReadExact(reader, 16));
                var deviceType = (DeviceType)reader.ReadByte();
                if (deviceId == Guid.Empty || originInstanceId == Guid.Empty || !DeviceTypeDetector.IsValid(deviceType))
                    return false;
                var tlsFingerprint = ReadExact(reader, 32);
                var signPublicKey = ReadExact(reader, SyncConstants.SyncDeltaEd25519PublicKeyBytes);
                var agreementPublicKey = ReadExact(reader, SyncConstants.SyncDeltaX25519PublicKeyBytes);
                var responderAddress = new IPAddress(ReadExact(reader, 4));

                if (reader.BaseStream.Position != authenticatedLength)
                    return false;

                var mac = ReadExact(reader, SyncConstants.LocalDiscoveryMacBytes);
                if (reader.BaseStream.Position != reader.BaseStream.Length)
                    return false;

                packet = new EnrollmentDiscoveryResponsePacket
                {
                    UnixTimeSeconds = unixTimeSeconds,
                    QueryNonce = queryNonce,
                    SessionId = sessionId,
                    DeviceId = deviceId,
                    OriginInstanceId = originInstanceId,
                    DeviceType = deviceType,
                    TlsFingerprint = tlsFingerprint,
                    SignPublicKey = signPublicKey,
                    AgreementPublicKey = agreementPublicKey,
                    ResponderAddress = responderAddress,
                    AuthenticatedBytes = payload[..authenticatedLength],
                    Mac = mac
                };
                return true;
            }
            catch
            {
                return false;
            }
        }
    }


    private static byte[] BuildPacketPrefix(LocalDiscoveryMessageType messageType, Action<BinaryWriter> writePayload)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.UTF8, true);

        writer.Write(Magic);
        writer.Write((byte)SyncConstants.LocalDiscoveryProtocolVersion);
        writer.Write((byte)messageType);
        writePayload(writer);
        writer.Flush();

        if (stream.Length > SyncConstants.LocalDiscoveryMaxPacketBytes)
            throw new InvalidDataException("The local discovery packet is too large.");

        return stream.ToArray();
    }


    private static bool TryPrepareReader(
        byte[] payload,
        LocalDiscoveryMessageType expectedType,
        int authenticatorLength,
        out BinaryReader reader,
        out int authenticatedLength)
    {
        reader = null!;
        authenticatedLength = 0;

        if (!TryReadMessageType(payload, out var messageType) || messageType != expectedType)
            return false;

        authenticatedLength = payload.Length - authenticatorLength;
        if (authenticatedLength <= Magic.Length + 2)
            return false;

        var stream = new MemoryStream(payload, writable: false);
        reader = new BinaryReader(stream, Encoding.UTF8, false);

        try
        {
            if (!ReadExact(reader, Magic.Length).SequenceEqual(Magic))
            {
                reader.Dispose();
                reader = null!;
                return false;
            }

            if (reader.ReadByte() != SyncConstants.LocalDiscoveryProtocolVersion ||
                reader.ReadByte() != (byte)expectedType)
            {
                reader.Dispose();
                reader = null!;
                return false;
            }

            return true;
        }
        catch
        {
            reader.Dispose();
            reader = null!;
            return false;
        }
    }


    private static void WriteSessionId(BinaryWriter writer, string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
            throw new ArgumentException("The enrollment session id is missing.", nameof(sessionId));

        var normalized = sessionId.Trim();
        if (normalized.Any(character => !IsSessionIdCharacter(character)))
            throw new ArgumentException("The enrollment session id contains invalid characters.", nameof(sessionId));

        var bytes = Encoding.ASCII.GetBytes(normalized);
        if (bytes.Length == 0 || bytes.Length > MaxSessionIdBytes)
            throw new ArgumentException("The enrollment session id has an invalid length.", nameof(sessionId));

        writer.Write((byte)bytes.Length);
        writer.Write(bytes);
    }


    private static string ReadSessionId(BinaryReader reader)
    {
        var length = reader.ReadByte();
        if (length == 0 || length > MaxSessionIdBytes)
            throw new InvalidDataException("The enrollment session id has an invalid length.");

        var bytes = ReadExact(reader, length);
        var sessionId = Encoding.ASCII.GetString(bytes);
        if (sessionId.Any(character => !IsSessionIdCharacter(character)))
            throw new InvalidDataException("The enrollment session id contains invalid characters.");

        return sessionId;
    }


    private static bool IsSessionIdCharacter(char character) =>
        (character >= 'A' && character <= 'Z') ||
        (character >= '2' && character <= '7');


    private static byte[] ReadExact(BinaryReader reader, int count)
    {
        var bytes = reader.ReadBytes(count);
        if (bytes.Length != count)
            throw new EndOfStreamException();

        return bytes;
    }


    private static void ValidateNonce(byte[] nonce)
    {
        if (nonce.Length != SyncConstants.LocalDiscoveryNonceBytes)
            throw new ArgumentException("The local discovery nonce has an invalid length.", nameof(nonce));
    }


    private static void ValidateFingerprint(byte[] tlsFingerprint)
    {
        if (tlsFingerprint.Length != 32)
            throw new ArgumentException("The TLS certificate fingerprint has an invalid length.", nameof(tlsFingerprint));
    }


    private static void ValidateResponderAddress(IPAddress responderAddress)
    {
        ArgumentNullException.ThrowIfNull(responderAddress);

        if (responderAddress.AddressFamily != AddressFamily.InterNetwork ||
            IPAddress.IsLoopback(responderAddress) ||
            responderAddress.Equals(IPAddress.Any) ||
            responderAddress.Equals(IPAddress.Broadcast))
            throw new ArgumentException("The discovery responder address must be a usable IPv4 address.", nameof(responderAddress));
    }
}
