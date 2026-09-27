using PasswordManagerLocal.Common.Backend.Constants;
using PasswordManagerLocal.Common.Backend.Models;
using PasswordManagerLocal.Common.Backend.Utils;
using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace PasswordManagerLocal.Common.Backend.Sync.Enrollment;

public static class DeviceEnrollmentCode
{
    private const string Prefix = "PML";
    private const string DirectPrefix = "PML2";
    private const string CompactDirectPrefix = "PML3";
    private const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
    private const byte DirectPayloadVersion = 4;
    private const byte CompactDirectPayloadVersion = 5;
    private const int SessionByteCount = 5;
    private const int SecretByteCount = 16;
    private const int FingerprintByteCount = 32;
    private const int CompactFingerprintPrefixByteCount = 16;
    private const int MaxDirectHosts = 16;
    private const int MaxCompactDirectHosts = 6;
    private const int MaxHostByteLength = 96;

    public static (string Code, string SessionId, byte[] Secret) Create(DeviceEnrollmentDirectEndpointInfo? directEndpointInfo = null)
    {
        var sessionBytes = RandomNumberGenerator.GetBytes(SessionByteCount);
        var secret = RandomNumberGenerator.GetBytes(SecretByteCount);
        var sessionId = Encode(sessionBytes);

        if (directEndpointInfo is not null && TryBuildCompactDirectCode(sessionBytes, secret, directEndpointInfo, out var compactDirectCode))
            return (compactDirectCode, sessionId, secret);

        if (directEndpointInfo is not null && TryBuildDirectCode(sessionBytes, secret, directEndpointInfo, out var directCode))
            return (directCode, sessionId, secret);

        var secretText = Encode(secret);
        return ($"{Prefix}-{sessionId}-{Group(secretText)}", sessionId, secret);
    }


    public static DeviceEnrollmentParsedCode Parse(string code)
    {
        if (string.IsNullOrWhiteSpace(code))
            throw new InvalidDataException("Device enrollment code is missing.");

        var withoutWhitespace = new string(code.Where(ch => !char.IsWhiteSpace(ch)).ToArray()).ToUpperInvariant();
        var parts = withoutWhitespace.Split('-', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length >= 2 && parts[0] == CompactDirectPrefix)
            return ParseCompactDirect(string.Concat(parts.Skip(1)));

        if (parts.Length >= 2 && parts[0] == DirectPrefix)
            return ParseDirect(string.Concat(parts.Skip(1)));

        if (parts.Length < 3 || parts[0] != Prefix)
            throw new InvalidDataException("Device enrollment code is invalid.");

        var sessionId = parts[1];
        var secretText = string.Concat(parts.Skip(2));
        if (sessionId.Length < 8 || secretText.Length < 20)
            throw new InvalidDataException("Device enrollment code is invalid.");

        return new DeviceEnrollmentParsedCode
        {
            SessionId = sessionId,
            Secret = Decode(secretText)
        };
    }


    public static byte[] BuildEnrollmentInfoProof(string sessionId, byte[] secret) =>
        Hmac(secret, $"info:{sessionId}");


    public static byte[] BuildCompletionProof(
        string sessionId,
        byte[] secret,
        string sourceDeviceId,
        Guid sourceOriginInstanceId,
        byte[] sourceSignPublicKey,
        string sourceTlsFingerprint,
        Guid targetDeviceId,
        Guid targetOriginInstanceId) =>
        Hmac(secret, $"complete:v2:{sessionId}:{sourceDeviceId}:{sourceOriginInstanceId:N}:{Convert.ToHexString(sourceSignPublicKey)}:{NormalizeFingerprint(sourceTlsFingerprint)}:{targetDeviceId:N}:{targetOriginInstanceId:N}");


    public static byte[] BuildSnapshotEncryptionKey(string sessionId, byte[] secret) =>
        Hmac(secret, $"snapshot-key:{sessionId}");


    public static byte[] BuildSnapshotEncryptionAad(
        string sessionId,
        string sourceDeviceId,
        Guid sourceOriginInstanceId,
        byte[] sourceSignPublicKey,
        string sourceTlsFingerprint,
        Guid targetDeviceId,
        Guid targetOriginInstanceId) =>
        Encoding.UTF8.GetBytes($"pml-enrollment-snapshot:v2:{sessionId}:{sourceDeviceId}:{sourceOriginInstanceId:N}:{Convert.ToHexString(sourceSignPublicKey)}:{NormalizeFingerprint(sourceTlsFingerprint)}:{targetDeviceId:N}:{targetOriginInstanceId:N}");


    public static bool FixedTimeEquals(byte[] left, byte[] right)
    {
        if (left.Length != right.Length)
            return false;

        return CryptographicOperations.FixedTimeEquals(left, right);
    }


    public static string Encode(byte[] data)
    {
        if (data.Length == 0)
            return string.Empty;

        var output = new StringBuilder((data.Length * 8 + 4) / 5);
        var buffer = 0;
        var bitsLeft = 0;

        foreach (var value in data)
        {
            buffer = (buffer << 8) | value;
            bitsLeft += 8;

            while (bitsLeft >= 5)
            {
                output.Append(Alphabet[(buffer >> (bitsLeft - 5)) & 31]);
                bitsLeft -= 5;
            }
        }

        if (bitsLeft > 0)
            output.Append(Alphabet[(buffer << (5 - bitsLeft)) & 31]);

        return output.ToString();
    }


    private static bool TryBuildCompactDirectCode(byte[] sessionBytes, byte[] secret, DeviceEnrollmentDirectEndpointInfo endpointInfo, out string code)
    {
        code = string.Empty;

        try
        {
            var hosts = endpointInfo.Hosts
                .Where(host => !string.IsNullOrWhiteSpace(host))
                .Select(host => host.Trim())
                .Select(host => IPAddress.TryParse(host, out var address) ? address : null)
                .Where(address => address is not null && IsCompactHostAddress(address))
                .Select(address => address!)
                .DistinctBy(address => address.ToString())
                .Take(MaxCompactDirectHosts)
                .ToList();

            if (hosts.Count == 0 || endpointInfo.Port is <= 0 or > 65535)
                return false;

            var fingerprint = FingerprintHexToBytes(endpointInfo.TlsCertFingerprint);
            if (fingerprint.Length != FingerprintByteCount)
                return false;

            using var ms = new MemoryStream();
            using var writer = new BinaryWriter(ms, Encoding.UTF8, true);

            writer.Write(CompactDirectPayloadVersion);
            writer.Write(sessionBytes);
            writer.Write(secret);
            writer.Write((ushort)endpointInfo.Port);
            writer.Write(fingerprint.Take(CompactFingerprintPrefixByteCount).ToArray());
            writer.Write((byte)hosts.Count);

            foreach (var host in hosts)
            {
                if (host.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                {
                    writer.Write((byte)4);
                    writer.Write(host.GetAddressBytes());
                }
                else if (host.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
                {
                    writer.Write((byte)6);
                    writer.Write(host.GetAddressBytes());
                }
                else
                {
                    return false;
                }
            }

            writer.Write(RandomNumberGenerator.GetBytes(SyncConstants.EnrollmentCodeNoiseBytes));

            code = $"{CompactDirectPrefix}-{Group(Encode(ms.ToArray()))}";
            return true;
        }
        catch
        {
            code = string.Empty;
            return false;
        }
    }


    private static bool TryBuildDirectCode(byte[] sessionBytes, byte[] secret, DeviceEnrollmentDirectEndpointInfo endpointInfo, out string code)
    {
        code = string.Empty;

        try
        {
            var hosts = endpointInfo.Hosts
                .Where(host => !string.IsNullOrWhiteSpace(host))
                .Select(host => host.Trim())
                .Where(host => host.Length <= MaxHostByteLength && IPAddress.TryParse(host, out _))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(MaxDirectHosts)
                .ToList();

            if (hosts.Count == 0 || endpointInfo.DeviceId == Guid.Empty || endpointInfo.OriginInstanceId == Guid.Empty || endpointInfo.Port is <= 0 or > 65535 || !DeviceTypeDetector.IsValid(endpointInfo.DeviceType))
                return false;

            var fingerprint = FingerprintHexToBytes(endpointInfo.TlsCertFingerprint);
            if (fingerprint.Length != FingerprintByteCount || endpointInfo.SignPublicKey.Length != 32 || endpointInfo.AgreementPublicKey.Length != 32)
                return false;

            using var ms = new MemoryStream();
            using var writer = new BinaryWriter(ms, Encoding.UTF8, true);

            writer.Write(DirectPayloadVersion);
            writer.Write(sessionBytes);
            writer.Write(secret);
            writer.Write(endpointInfo.DeviceId.ToByteArray());
            writer.Write(endpointInfo.OriginInstanceId.ToByteArray());
            writer.Write((byte)endpointInfo.DeviceType);
            writer.Write((ushort)endpointInfo.Port);
            writer.Write(fingerprint);
            writer.Write(endpointInfo.SignPublicKey);
            writer.Write(endpointInfo.AgreementPublicKey);
            writer.Write((byte)hosts.Count);

            foreach (var host in hosts)
            {
                var hostBytes = Encoding.UTF8.GetBytes(host);
                if (hostBytes.Length == 0 || hostBytes.Length > MaxHostByteLength)
                    return false;

                writer.Write((byte)hostBytes.Length);
                writer.Write(hostBytes);
            }

            writer.Write(RandomNumberGenerator.GetBytes(SyncConstants.EnrollmentCodeNoiseBytes));

            code = $"{DirectPrefix}-{Group(Encode(ms.ToArray()))}";
            return true;
        }
        catch
        {
            code = string.Empty;
            return false;
        }
    }


    private static DeviceEnrollmentParsedCode ParseCompactDirect(string payloadText)
    {
        try
        {
            var payload = Decode(payloadText);
            using var ms = new MemoryStream(payload);
            using var reader = new BinaryReader(ms, Encoding.UTF8, true);

            var version = reader.ReadByte();
            if (version != CompactDirectPayloadVersion)
                throw new InvalidDataException("Device enrollment code is invalid.");

            var sessionBytes = reader.ReadBytes(SessionByteCount);
            var secret = reader.ReadBytes(SecretByteCount);
            var port = reader.ReadUInt16();
            var fingerprintPrefix = reader.ReadBytes(CompactFingerprintPrefixByteCount);
            var hostCount = reader.ReadByte();

            if (sessionBytes.Length != SessionByteCount ||
                secret.Length != SecretByteCount ||
                fingerprintPrefix.Length != CompactFingerprintPrefixByteCount ||
                hostCount == 0 ||
                hostCount > MaxCompactDirectHosts ||
                port == 0)
                throw new InvalidDataException("Device enrollment code is invalid.");

            var endpoints = new List<DeviceEnrollmentParsedDirectEndpoint>();
            var fingerprintPrefixText = Convert.ToHexString(fingerprintPrefix);

            for (var i = 0; i < hostCount; i++)
            {
                var family = reader.ReadByte();
                var addressLength = family switch
                {
                    4 => 4,
                    6 => 16,
                    _ => throw new InvalidDataException("Device enrollment code is invalid.")
                };

                var addressBytes = reader.ReadBytes(addressLength);
                if (addressBytes.Length != addressLength)
                    throw new InvalidDataException("Device enrollment code is invalid.");

                var host = new IPAddress(addressBytes).ToString();
                endpoints.Add(new DeviceEnrollmentParsedDirectEndpoint
                {
                    Host = host,
                    Port = port,
                    TlsCertFingerprint = fingerprintPrefixText
                });
            }

            return new DeviceEnrollmentParsedCode
            {
                SessionId = Encode(sessionBytes),
                Secret = secret,
                DirectEndpoints = endpoints
            };
        }
        catch (EndOfStreamException ex)
        {
            throw new InvalidDataException("Device enrollment code is invalid.", ex);
        }
        catch (ArgumentException ex)
        {
            throw new InvalidDataException("Device enrollment code is invalid.", ex);
        }
        catch (FormatException ex)
        {
            throw new InvalidDataException("Device enrollment code is invalid.", ex);
        }
    }


    private static DeviceEnrollmentParsedCode ParseDirect(string payloadText)
    {
        try
        {
            var payload = Decode(payloadText);
            using var ms = new MemoryStream(payload);
            using var reader = new BinaryReader(ms, Encoding.UTF8, true);

            var version = reader.ReadByte();
            if (version != DirectPayloadVersion)
                throw new InvalidDataException("Device enrollment code is invalid.");

            var sessionBytes = reader.ReadBytes(SessionByteCount);
            var secret = reader.ReadBytes(SecretByteCount);
            var deviceIdBytes = reader.ReadBytes(16);
            var originInstanceIdBytes = reader.ReadBytes(16);
            var deviceTypeValue = reader.ReadByte();
            var port = reader.ReadUInt16();
            var fingerprintBytes = reader.ReadBytes(FingerprintByteCount);
            var signPublicKey = reader.ReadBytes(32);
            var agreementPublicKey = reader.ReadBytes(32);
            var hostCount = reader.ReadByte();

            if (sessionBytes.Length != SessionByteCount ||
                secret.Length != SecretByteCount ||
                deviceIdBytes.Length != 16 ||
                originInstanceIdBytes.Length != 16 ||
                !DeviceTypeDetector.IsValid((DeviceType)deviceTypeValue) ||
                fingerprintBytes.Length != FingerprintByteCount ||
                signPublicKey.Length != 32 ||
                agreementPublicKey.Length != 32 ||
                hostCount == 0 ||
                hostCount > MaxDirectHosts)
                throw new InvalidDataException("Device enrollment code is invalid.");

            var endpoints = new List<DeviceEnrollmentParsedDirectEndpoint>();
            var deviceId = new Guid(deviceIdBytes);
            var originInstanceId = new Guid(originInstanceIdBytes);
            if (originInstanceId == Guid.Empty)
                throw new InvalidDataException("Device enrollment code is invalid.");
            var fingerprint = Convert.ToHexString(fingerprintBytes);

            for (var i = 0; i < hostCount; i++)
            {
                var hostLength = reader.ReadByte();
                if (hostLength == 0 || hostLength > MaxHostByteLength)
                    throw new InvalidDataException("Device enrollment code is invalid.");

                var hostBytes = reader.ReadBytes(hostLength);
                if (hostBytes.Length != hostLength)
                    throw new InvalidDataException("Device enrollment code is invalid.");

                var host = Encoding.UTF8.GetString(hostBytes);
                if (!IPAddress.TryParse(host, out _))
                    continue;

                endpoints.Add(new DeviceEnrollmentParsedDirectEndpoint
                {
                    Host = host,
                    Port = port,
                    DeviceId = deviceId,
                    OriginInstanceId = originInstanceId,
                    DeviceType = (DeviceType)deviceTypeValue,
                    TlsCertFingerprint = fingerprint,
                    SignPublicKey = signPublicKey.ToArray(),
                    AgreementPublicKey = agreementPublicKey.ToArray()
                });
            }

            if (endpoints.Count == 0)
                throw new InvalidDataException("Device enrollment code is invalid.");

            return new DeviceEnrollmentParsedCode
            {
                SessionId = Encode(sessionBytes),
                Secret = secret,
                DirectEndpoints = endpoints
            };
        }
        catch (EndOfStreamException ex)
        {
            throw new InvalidDataException("Device enrollment code is invalid.", ex);
        }
        catch (ArgumentException ex)
        {
            throw new InvalidDataException("Device enrollment code is invalid.", ex);
        }
        catch (FormatException ex)
        {
            throw new InvalidDataException("Device enrollment code is invalid.", ex);
        }
    }


    private static byte[] Decode(string value)
    {
        var normalized = new string(value.Where(ch => !char.IsWhiteSpace(ch) && ch != '-').ToArray()).ToUpperInvariant();
        var output = new List<byte>();
        var buffer = 0;
        var bitsLeft = 0;

        foreach (var ch in normalized)
        {
            var index = Alphabet.IndexOf(ch);
            if (index < 0)
                throw new InvalidDataException("Device enrollment code contains an invalid character.");

            buffer = (buffer << 5) | index;
            bitsLeft += 5;

            if (bitsLeft >= 8)
            {
                output.Add((byte)((buffer >> (bitsLeft - 8)) & 255));
                bitsLeft -= 8;
            }
        }

        return output.ToArray();
    }


    private static byte[] Hmac(byte[] key, string value) =>
        HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(value));


    private static string Group(string value) =>
        string.Join("-", Enumerable.Range(0, (value.Length + 4) / 5).Select(i => value.Substring(i * 5, Math.Min(5, value.Length - i * 5))));


    private static bool IsCompactHostAddress(IPAddress? address) =>
        address is not null &&
        (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ||
         address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6);


    private static byte[] FingerprintHexToBytes(string fingerprint)
    {
        var normalized = NormalizeFingerprint(fingerprint);
        return Convert.FromHexString(normalized);
    }


    private static string NormalizeFingerprint(string fingerprint) =>
        fingerprint.Replace(":", string.Empty).Replace(" ", string.Empty).Trim().ToUpperInvariant();
}
