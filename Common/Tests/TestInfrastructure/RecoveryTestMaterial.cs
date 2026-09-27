using NSec.Cryptography;
using PasswordManagerLocal.Common.Backend.Abstractions.Services;
using PasswordManagerLocal.Common.Backend.Models;
using PasswordManagerLocal.Common.Backend.Models.Encrypted;
using PasswordManagerLocal.Common.Backend.Security;
using PasswordManagerLocal.Common.Backend.Services;
using PasswordManagerLocal.Common.Backend.Sync;
using PasswordManagerLocal.Common.Backend.Utils;
using PasswordManagerLocal.Common.Tests.Fakes;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using static PasswordManagerLocal.Common.Backend.Utils.DataCodec;

namespace PasswordManagerLocal.Common.Tests.TestInfrastructure;

internal sealed class RecoveryTestMaterial : IDisposable
{
    private bool _disposed;

    public RecoveryTestMaterial(Guid? userId = null)
    {
        UserId = userId ?? Guid.NewGuid();
        GeneralKey = CreateRawKey();
        PasswordsKey = CreateRawKey();
        DevicesKey = CreateRawKey();
        PasswordValueKey = CreateRawKey();
        PasswordSalt = Hashing.GenerateSalt();
        UsernameSalt = Hashing.GenerateSalt();
    }

    public Guid UserId { get; }
    public byte[] GeneralKey { get; }
    public byte[] PasswordsKey { get; }
    public byte[] DevicesKey { get; }
    public byte[] PasswordValueKey { get; }
    public byte[] PasswordSalt { get; }
    public byte[] UsernameSalt { get; }

    public UserDataBundle CreateBundle(
        string username,
        SyncVersionStamp generalVersion,
        IEnumerable<SecurePassword>? passwords = null,
        IEnumerable<UserDeviceData>? devices = null)
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(RecoveryTestMaterial));
        var bundle = new UserDataBundle
        {
            UserData = new UserData
            {
                UId = UserId,
                GeneralUserDataKey = GeneralKey.ToArray(),
                UserPasswordsDataKey = PasswordsKey.ToArray(),
                UserDevicesDataKey = DevicesKey.ToArray()
            },
            GeneralUserData = new GeneralUserData
            {
                Username = username,
                FirstName = "Recovery",
                LastName = "Fixture",
                Email = "recovery@example.invalid",
                RegistrationDate = DateTime.UnixEpoch,
                RegistrationTimeZoneId = "UTC",
                RegistrationDeviceType = DeviceType.WindowsPc,
                LastUpdatedAt = DateTimeOffset.FromUnixTimeMilliseconds(generalVersion.PhysicalTimeUnixMilliseconds).UtcDateTime,
                Version = generalVersion
            },
            UserPasswordsData = new UserPasswordsData
            {
                PasswordKey = PasswordValueKey.ToArray(),
                Passwords = passwords?.ToList() ?? []
            },
            UserDevicesData = new UserDevicesData
            {
                Devices = devices?.ToList() ?? []
            }
        };

        new UserDataBundleIntegrityService().RebuildInitialIntegrity(bundle);
        return bundle;
    }

    public async Task<User> EncryptUserAsync(
        UserDataBundle bundle,
        EncryptionKey key,
        DateTimeOffset? timestamp = null,
        long keyEpoch = 1,
        long membershipEpoch = 1)
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(RecoveryTestMaterial));
        var now = timestamp ?? DateTimeOffset.FromUnixTimeMilliseconds(
            Math.Max(1, bundle.GeneralUserData.Version.PhysicalTimeUnixMilliseconds));
        var rootTask = SerializeCompressEncryptAsync(
            bundle.UserData,
            key,
            BackendJsonSerializerContext.Default.UserData);
        using var generalKey = EncryptionKey.FromRaw(bundle.UserData.GeneralUserDataKey);
        using var passwordsKey = EncryptionKey.FromRaw(bundle.UserData.UserPasswordsDataKey);
        using var devicesKey = EncryptionKey.FromRaw(bundle.UserData.UserDevicesDataKey);
        var generalTask = SerializeCompressEncryptAsync(
            bundle.GeneralUserData,
            generalKey,
            BackendJsonSerializerContext.Default.GeneralUserData);
        var passwordsTask = SerializeCompressEncryptAsync(
            bundle.UserPasswordsData,
            passwordsKey,
            BackendJsonSerializerContext.Default.UserPasswordsData);
        var devicesTask = SerializeCompressEncryptAsync(
            bundle.UserDevicesData,
            devicesKey,
            BackendJsonSerializerContext.Default.UserDevicesData);
        await Task.WhenAll(rootTask, generalTask, passwordsTask, devicesTask);

        var usernameBytes = Encoding.UTF8.GetBytes(bundle.GeneralUserData.Username);
        try
        {
            var user = new User
            {
                UId = UserId,
                UsernameSalt = UsernameSalt.ToArray(),
                UsernameHash = Hashing.SHA256Hash(usernameBytes, UsernameSalt),
                PasswordSalt = PasswordSalt.ToArray(),
                EncryptedPayload = await rootTask,
                EncryptedGeneralUserDataPayload = await generalTask,
                EncryptedUserPasswordsDataPayload = await passwordsTask,
                EncryptedUserDevicesDataPayload = await devicesTask,
                KeyEpoch = keyEpoch,
                MembershipEpoch = membershipEpoch,
                LastModifiedAt = now,
                UserDataLastModifiedAt = now,
                GeneralUserDataLastModifiedAt = now,
                UserPasswordsDataLastModifiedAt = now,
                UserDevicesDataLastModifiedAt = now
            };
            user.SetGeneralUserDataVersion(bundle.GeneralUserData.Version);
            user.GenerateIntegrityHash();
            return user;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(usernameBytes);
        }
    }

    public async Task<UserSnapshotEnvelope> CreateEnvelopeAsync(
        UserDataBundle bundle,
        EncryptionKey key,
        FakeDeviceIdentityService origin,
        long revision,
        DateTimeOffset? timestamp = null,
        long keyEpoch = 1,
        long membershipEpoch = 1,
        IReadOnlyList<UserSnapshotCoverageEntry>? coverage = null)
    {
        var user = await EncryptUserAsync(bundle, key, timestamp, keyEpoch, membershipEpoch);
        var createdAt = timestamp ?? user.LastModifiedAt;
        var payload = new UserSyncPayload
        {
            UId = user.UId,
            UsernameHash = user.UsernameHash.ToArray(),
            UsernameSalt = user.UsernameSalt.ToArray(),
            GeneralUserDataVersion = user.GetGeneralUserDataVersion(),
            PasswordSalt = user.PasswordSalt.ToArray(),
            EncryptedPayload = user.EncryptedPayload.ToArray(),
            EncryptedGeneralUserDataPayload = user.EncryptedGeneralUserDataPayload.ToArray(),
            EncryptedUserPasswordsDataPayload = user.EncryptedUserPasswordsDataPayload.ToArray(),
            EncryptedUserDevicesDataPayload = user.EncryptedUserDevicesDataPayload.ToArray(),
            UserDataLastModifiedAt = user.UserDataLastModifiedAt,
            GeneralUserDataLastModifiedAt = user.GeneralUserDataLastModifiedAt,
            UserPasswordsDataLastModifiedAt = user.UserPasswordsDataLastModifiedAt,
            UserDevicesDataLastModifiedAt = user.UserDevicesDataLastModifiedAt,
            GroupIds = [],
            DeviceIds = []
        };
        payload.IntegrityHash = SyncCryptoUtil.CalculateUserHash(payload, createdAt.ToUnixTimeMilliseconds());
        var envelope = new UserSnapshotEnvelope
        {
            UserId = user.UId,
            OriginDeviceId = origin.LocalDeviceId,
            OriginInstanceId = origin.OriginInstanceId,
            OriginRevision = revision,
            UserKeyEpoch = keyEpoch,
            MembershipEpoch = membershipEpoch,
            CreatedAtUtc = createdAt,
            User = payload,
            Coverage = coverage?.ToList() ?? []
        };
        UserSnapshotEnvelopeUtil.FillOriginAuthentication(envelope, origin);
        return envelope;
    }

    public static UserSyncSnapshot CreateSnapshotRow(
        UserSnapshotEnvelope envelope,
        UserSyncSnapshotStatus status = UserSyncSnapshotStatus.RecoveryCandidate)
    {
        var serialized = JsonSerializer.SerializeToUtf8Bytes(
            envelope,
            BackendJsonSerializerContext.Default.UserSnapshotEnvelope);
        return new UserSyncSnapshot
        {
            UserId = envelope.UserId,
            OriginDeviceId = envelope.OriginDeviceId,
            OriginInstanceId = envelope.OriginInstanceId,
            OriginRevision = envelope.OriginRevision,
            UserKeyEpoch = envelope.UserKeyEpoch,
            MembershipEpoch = envelope.MembershipEpoch,
            CreatedAtUtc = envelope.CreatedAtUtc,
            ReceivedAtUtc = envelope.CreatedAtUtc,
            SnapshotHash = envelope.SnapshotHash.ToArray(),
            OriginSignPublicKey = envelope.OriginSignPublicKey.ToArray(),
            OriginSignature = envelope.OriginSignature.ToArray(),
            EnvelopePayload = serialized,
            Status = status
        };
    }

    public static SecurePassword CreatePassword(
        Guid id,
        string name,
        SyncVersionStamp version,
        byte marker)
    {
        var value = new SecurePassword
        {
            Id = id,
            Name = name,
            Description = $"description-{name}",
            Password = [marker, (byte)(marker + 1)],
            CreatedAt = DateTime.UnixEpoch,
            LastUpdatedAt = DateTimeOffset.FromUnixTimeMilliseconds(version.PhysicalTimeUnixMilliseconds).UtcDateTime,
            Version = version
        };
        return value;
    }

    public static UserDeviceData CreateDevice(Guid id, string name, SyncVersionStamp version) => new()
    {
        Id = id,
        Name = name,
        LinkedAt = DateTimeOffset.UnixEpoch,
        LastLoginDate = DateTimeOffset.FromUnixTimeMilliseconds(version.PhysicalTimeUnixMilliseconds).UtcDateTime,
        PreviousLoginDate = null,
        LastUpdatedAt = DateTimeOffset.FromUnixTimeMilliseconds(version.PhysicalTimeUnixMilliseconds),
        Version = version
    };

    public static SyncVersionStamp Version(long physical, Guid deviceId, Guid instanceId, long logical = 0) => new()
    {
        PhysicalTimeUnixMilliseconds = physical,
        LogicalCounter = logical,
        OriginDeviceId = deviceId,
        OriginInstanceId = instanceId
    };

    public static FakeDeviceIdentityService CreateIdentity(
        Guid deviceId,
        Guid instanceId,
        Key signingKey) => new()
    {
        LocalDeviceId = deviceId,
        OriginInstanceId = instanceId,
        SignPublicKey = signingKey.PublicKey.Export(KeyBlobFormat.RawPublicKey),
        SignHandler = bytes => SignatureAlgorithm.Ed25519.Sign(signingKey, bytes)
    };

    private static byte[] CreateRawKey()
    {
        using var key = EncryptionKey.Create();
        return key.ExportCopy();
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        CryptographicOperations.ZeroMemory(GeneralKey);
        CryptographicOperations.ZeroMemory(PasswordsKey);
        CryptographicOperations.ZeroMemory(DevicesKey);
        CryptographicOperations.ZeroMemory(PasswordValueKey);
        CryptographicOperations.ZeroMemory(PasswordSalt);
        CryptographicOperations.ZeroMemory(UsernameSalt);
        _disposed = true;
    }
}
