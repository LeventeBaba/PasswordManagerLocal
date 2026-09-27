using PasswordManagerLocal.Common.Backend.Abstractions.Persistence;
using PasswordManagerLocal.Common.Backend.Abstractions.Repositories;
using PasswordManagerLocal.Common.Backend.Abstractions.Services;
using PasswordManagerLocal.Common.Backend.Exceptions;
using PasswordManagerLocal.Common.Backend.Models;
using PasswordManagerLocal.Common.Backend.Models.Encrypted;
using PasswordManagerLocal.Common.Contracts.Requests;
using PasswordManagerLocal.Common.Contracts.Responses;
using PasswordManagerLocal.Common.Backend.Security;
using System.Security.Cryptography;
using System.Text;
using static PasswordManagerLocal.Common.Backend.Utils.DataCodec;
using PasswordManagerLocal.Common.Backend.Utils;

namespace PasswordManagerLocal.Common.Backend.Internal.Authentication;

internal sealed class CanonicalUserState
{
    private readonly byte[] _usernameHash;
    private readonly byte[] _usernameSalt;
    private readonly byte[] _passwordSalt;
    private readonly byte[] _encryptedPayload;
    private readonly byte[] _encryptedGeneralPayload;
    private readonly byte[] _encryptedPasswordsPayload;
    private readonly byte[] _encryptedDevicesPayload;
    private readonly byte[]? _savedKey;
    private readonly byte[] _integrityHash;
    private readonly long _keyEpoch;
    private readonly long _membershipEpoch;
    private readonly long _generalVersionPhysicalTimeUnixMilliseconds;
    private readonly long _generalVersionLogicalCounter;
    private readonly Guid _generalVersionOriginDeviceId;
    private readonly Guid _generalVersionOriginInstanceId;
    private readonly DateTimeOffset _lastModifiedAt;
    private readonly DateTimeOffset _userDataLastModifiedAt;
    private readonly DateTimeOffset _generalLastModifiedAt;
    private readonly DateTimeOffset _passwordsLastModifiedAt;
    private readonly DateTimeOffset _devicesLastModifiedAt;

    private CanonicalUserState(User user)
    {
        _usernameHash = user.UsernameHash.ToArray();
        _usernameSalt = user.UsernameSalt.ToArray();
        _passwordSalt = user.PasswordSalt.ToArray();
        _encryptedPayload = user.EncryptedPayload.ToArray();
        _encryptedGeneralPayload = user.EncryptedGeneralUserDataPayload.ToArray();
        _encryptedPasswordsPayload = user.EncryptedUserPasswordsDataPayload.ToArray();
        _encryptedDevicesPayload = user.EncryptedUserDevicesDataPayload.ToArray();
        _savedKey = user.SavedKey?.ToArray();
        _integrityHash = user.IntegrityHash.ToArray();
        _keyEpoch = user.KeyEpoch;
        _membershipEpoch = user.MembershipEpoch;
        _generalVersionPhysicalTimeUnixMilliseconds = user.GeneralDataVersionPhysicalTimeUnixMilliseconds;
        _generalVersionLogicalCounter = user.GeneralDataVersionLogicalCounter;
        _generalVersionOriginDeviceId = user.GeneralDataVersionOriginDeviceId;
        _generalVersionOriginInstanceId = user.GeneralDataVersionOriginInstanceId;
        _lastModifiedAt = user.LastModifiedAt;
        _userDataLastModifiedAt = user.UserDataLastModifiedAt;
        _generalLastModifiedAt = user.GeneralUserDataLastModifiedAt;
        _passwordsLastModifiedAt = user.UserPasswordsDataLastModifiedAt;
        _devicesLastModifiedAt = user.UserDevicesDataLastModifiedAt;
    }

    public static CanonicalUserState Capture(User user) => new(user);

    public void Restore(User user)
    {
        ZeroIfDifferent(user.UsernameHash, _usernameHash);
        ZeroIfDifferent(user.UsernameSalt, _usernameSalt);
        ZeroIfDifferent(user.PasswordSalt, _passwordSalt);
        ZeroIfDifferent(user.EncryptedPayload, _encryptedPayload);
        ZeroIfDifferent(user.EncryptedGeneralUserDataPayload, _encryptedGeneralPayload);
        ZeroIfDifferent(user.EncryptedUserPasswordsDataPayload, _encryptedPasswordsPayload);
        ZeroIfDifferent(user.EncryptedUserDevicesDataPayload, _encryptedDevicesPayload);
        if (user.SavedKey is not null && !ReferenceEquals(user.SavedKey, _savedKey))
            CryptographicOperations.ZeroMemory(user.SavedKey);

        user.UsernameHash = _usernameHash.ToArray();
        user.UsernameSalt = _usernameSalt.ToArray();
        user.PasswordSalt = _passwordSalt.ToArray();
        user.EncryptedPayload = _encryptedPayload.ToArray();
        user.EncryptedGeneralUserDataPayload = _encryptedGeneralPayload.ToArray();
        user.EncryptedUserPasswordsDataPayload = _encryptedPasswordsPayload.ToArray();
        user.EncryptedUserDevicesDataPayload = _encryptedDevicesPayload.ToArray();
        user.SavedKey = _savedKey?.ToArray();
        user.IntegrityHash = _integrityHash.ToArray();
        user.KeyEpoch = _keyEpoch;
        user.MembershipEpoch = _membershipEpoch;
        user.GeneralDataVersionPhysicalTimeUnixMilliseconds = _generalVersionPhysicalTimeUnixMilliseconds;
        user.GeneralDataVersionLogicalCounter = _generalVersionLogicalCounter;
        user.GeneralDataVersionOriginDeviceId = _generalVersionOriginDeviceId;
        user.GeneralDataVersionOriginInstanceId = _generalVersionOriginInstanceId;
        user.LastModifiedAt = _lastModifiedAt;
        user.UserDataLastModifiedAt = _userDataLastModifiedAt;
        user.GeneralUserDataLastModifiedAt = _generalLastModifiedAt;
        user.UserPasswordsDataLastModifiedAt = _passwordsLastModifiedAt;
        user.UserDevicesDataLastModifiedAt = _devicesLastModifiedAt;
    }

    public void ZeroCopies()
    {
        CryptographicOperations.ZeroMemory(_usernameHash);
        CryptographicOperations.ZeroMemory(_usernameSalt);
        CryptographicOperations.ZeroMemory(_passwordSalt);
        CryptographicOperations.ZeroMemory(_encryptedPayload);
        CryptographicOperations.ZeroMemory(_encryptedGeneralPayload);
        CryptographicOperations.ZeroMemory(_encryptedPasswordsPayload);
        CryptographicOperations.ZeroMemory(_encryptedDevicesPayload);
        CryptographicOperations.ZeroMemory(_integrityHash);
        if (_savedKey is not null)
            CryptographicOperations.ZeroMemory(_savedKey);
    }

    private static void ZeroIfDifferent(byte[] current, byte[] backup)
    {
        if (!ReferenceEquals(current, backup))
            CryptographicOperations.ZeroMemory(current);
    }
}
