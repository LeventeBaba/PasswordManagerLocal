using PasswordManagerLocal.Common.Backend.Security;
using PasswordManagerLocal.Common.Backend.Models.Encrypted;
using System.Security.Cryptography;

namespace PasswordManagerLocal.Common.Backend.Models;

public sealed class User : IntegrityCheckableBase
{
    public Guid UId { get; set; } = Guid.NewGuid();
    public byte[] UsernameHash { get; set; } = [];
    public byte[] UsernameSalt { get; set; } = [];
    public byte[] PasswordSalt { get; set; } = [];
    public byte[] EncryptedPayload { get; set; } = [];
    public byte[] EncryptedGeneralUserDataPayload { get; set; } = [];
    public byte[] EncryptedUserPasswordsDataPayload { get; set; } = [];
    public byte[] EncryptedUserDevicesDataPayload { get; set; } = [];
    public byte[]? SavedKey { get; set; } = null;
    public long KeyEpoch { get; set; }
    public long MembershipEpoch { get; set; }
    public long GeneralDataVersionPhysicalTimeUnixMilliseconds { get; set; }
    public long GeneralDataVersionLogicalCounter { get; set; }
    public Guid GeneralDataVersionOriginDeviceId { get; set; }
    public Guid GeneralDataVersionOriginInstanceId { get; set; }
    public DateTimeOffset LastModifiedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UserDataLastModifiedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset GeneralUserDataLastModifiedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UserPasswordsDataLastModifiedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UserDevicesDataLastModifiedAt { get; set; } = DateTimeOffset.UtcNow;


    public ICollection<Group> Groups { get; set; } = [];
    public ICollection<UserDevice> UserDevices { get; set; } = [];
    public ICollection<LocalUserDevice> LocalUserDevices { get; set; } = [];





    public override byte[] CalculateIntegrityHash() =>
        Hashing.SHA256Hash(hash =>
        {
            hash.Write(UId);
            hash.WriteBytes(UsernameHash);
            hash.WriteBytes(UsernameSalt);
            hash.WriteBytes(PasswordSalt);
            hash.WriteBytes(EncryptedPayload);
            hash.WriteBytes(EncryptedGeneralUserDataPayload);
            hash.WriteBytes(EncryptedUserPasswordsDataPayload);
            hash.WriteBytes(EncryptedUserDevicesDataPayload);
            hash.Write(KeyEpoch);
            hash.Write(MembershipEpoch);
            hash.Write(GeneralDataVersionPhysicalTimeUnixMilliseconds);
            hash.Write(GeneralDataVersionLogicalCounter);
            hash.Write(GeneralDataVersionOriginDeviceId);
            hash.Write(GeneralDataVersionOriginInstanceId);
            hash.Write(LastModifiedAt);
            hash.Write(UserDataLastModifiedAt);
            hash.Write(GeneralUserDataLastModifiedAt);
            hash.Write(UserPasswordsDataLastModifiedAt);
            hash.Write(UserDevicesDataLastModifiedAt);
        });


    public SyncVersionStamp GetGeneralUserDataVersion() => new()
    {
        PhysicalTimeUnixMilliseconds = GeneralDataVersionPhysicalTimeUnixMilliseconds,
        LogicalCounter = GeneralDataVersionLogicalCounter,
        OriginDeviceId = GeneralDataVersionOriginDeviceId,
        OriginInstanceId = GeneralDataVersionOriginInstanceId
    };

    public void SetGeneralUserDataVersion(SyncVersionStamp version)
    {
        ArgumentNullException.ThrowIfNull(version);
        GeneralDataVersionPhysicalTimeUnixMilliseconds = version.PhysicalTimeUnixMilliseconds;
        GeneralDataVersionLogicalCounter = version.LogicalCounter;
        GeneralDataVersionOriginDeviceId = version.OriginDeviceId;
        GeneralDataVersionOriginInstanceId = version.OriginInstanceId;
    }


    public void ClearEncryptedPayloads()
    {
        CryptographicOperations.ZeroMemory(EncryptedPayload);
        CryptographicOperations.ZeroMemory(EncryptedGeneralUserDataPayload);
        CryptographicOperations.ZeroMemory(EncryptedUserPasswordsDataPayload);
        CryptographicOperations.ZeroMemory(EncryptedUserDevicesDataPayload);
    }
}
