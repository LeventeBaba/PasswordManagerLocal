using PasswordManagerLocal.Common.Backend.Abstractions.Persistence;
using PasswordManagerLocal.Common.Backend.Abstractions.Repositories;
using PasswordManagerLocal.Common.Backend.Abstractions.Services;
using PasswordManagerLocal.Common.Backend.Exceptions;
using PasswordManagerLocal.Common.Backend.Models;
using PasswordManagerLocal.Common.Backend.Models.Encrypted;
using PasswordManagerLocal.Common.Contracts.Requests;
using PasswordManagerLocal.Common.Backend.Security;
using PasswordManagerLocal.Common.Backend.Utils;
using System.Security.Cryptography;
using System.Text;
using static PasswordManagerLocal.Common.Backend.Utils.DataCodec;

namespace PasswordManagerLocal.Common.Backend.Services;

public sealed class UserRegistrationService : IUserRegistrationService
{
    private readonly IUserLookupService _userLookup;
    private readonly IUserDataWriterService _userDataWriter;
    private readonly IRememberMeService _rememberMe;
    private readonly IDeviceIdentityService _identity;
    private readonly ILocalUserDeviceRepository _localUserDevices;
    private readonly ISyncRuntimeService _syncRuntime;
    private readonly IUserDataBundleIntegrityService _integrity;
    private readonly IUnitOfWork _uow;
    private readonly IUserMembershipAuthorizationService _membershipAuthorization;
    private readonly IUserControlStateRepository _controlStates;
    private readonly IUserSyncStateRepository _syncStates;
    private readonly ISyncVersionClockService _versionClock;
    private readonly IUserLoginIdentityProjectionService _loginIdentities;
    private readonly IAuthenticatedSessionIssuer _sessionIssuer;

    public UserRegistrationService(
        IUserLookupService userLookup,
        IUserDataWriterService userDataWriter,
        IRememberMeService rememberMe,
        IDeviceIdentityService identity,
        ILocalUserDeviceRepository localUserDevices,
        ISyncRuntimeService syncRuntime,
        IUserDataBundleIntegrityService integrity,
        IUnitOfWork uow,
        IUserMembershipAuthorizationService membershipAuthorization,
        IUserControlStateRepository controlStates,
        IUserSyncStateRepository syncStates,
        ISyncVersionClockService versionClock,
        IUserLoginIdentityProjectionService loginIdentities,
        IAuthenticatedSessionIssuer sessionIssuer)
    {
        _userLookup = userLookup;
        _userDataWriter = userDataWriter;
        _rememberMe = rememberMe;
        _identity = identity;
        _localUserDevices = localUserDevices;
        _syncRuntime = syncRuntime;
        _integrity = integrity;
        _uow = uow;
        _membershipAuthorization = membershipAuthorization;
        _controlStates = controlStates;
        _syncStates = syncStates;
        _versionClock = versionClock;
        _loginIdentities = loginIdentities;
        _sessionIssuer = sessionIssuer;
    }

    public async Task<Guid> RegisterAsync(RegistrationRequest request, CancellationToken ct = default)
    {
        if (!request.Validate(out var errors))
            throw new InvalidInputException(errors);

        var usernameBytes = Encoding.UTF8.GetBytes(request.Username);
        await ThrowIfUsernameExistsAsync(usernameBytes, ct);

        var now = DateTime.UtcNow;
        var linkedAt = DateTimeOffset.UtcNow;
        var bundle = CreateInitialUserDataBundle(request, now, linkedAt);

        var passwordSalt = Hashing.GenerateSalt();
        using var key = EncryptionKey.FromPassword(request.Password, passwordSalt);
        var user = await CreateEncryptedUserForRegistrationAsync(bundle, usernameBytes, passwordSalt, key, linkedAt, ct);

        await SaveRegisteredUserAsync(user, request.RememberMe, key, ct);
        try
        {
            await _syncRuntime.RefreshSyncEnabledAsync(ct);
            return _sessionIssuer.IssueAuthenticatedSession(user.UId, key, bundle);
        }
        catch (Exception ex)
        {
            throw new MutationPartiallyCommittedException(
                "The account registration was committed, but the local runtime session could not be completed.",
                innerException: ex);
        }
    }


    private async Task ThrowIfUsernameExistsAsync(byte[] usernameBytes, CancellationToken ct)
    {
        var resolution = await _userLookup.ResolveUsernameAsync(usernameBytes, ct);
        if (resolution.State != UserLoginIdentityMatchState.NotFound)
            throw new InvalidInputException();
    }


    private UserDataBundle CreateInitialUserDataBundle(
        RegistrationRequest request,
        DateTime now,
        DateTimeOffset linkedAt)
    {
        var userData = new UserData { UId = Guid.NewGuid() };
        UserDataKeyUtil.InitializeUserDataKeys(userData);

        var bundle = new UserDataBundle
        {
            UserData = userData,
            GeneralUserData = CreateInitialGeneralUserData(request, now),
            UserPasswordsData = CreateInitialUserPasswordsData(),
            UserDevicesData = CreateInitialUserDevicesData(now, linkedAt)
        };
        _integrity.RebuildInitialIntegrity(bundle);
        return bundle;
    }


    private GeneralUserData CreateInitialGeneralUserData(RegistrationRequest request, DateTime now) =>
        new()
        {
            Username = request.Username,
            FirstName = request.FirstName,
            LastName = request.LastName,
            Email = request.Email,
            RegistrationDate = now,
            RegistrationTimeZoneId = TimeZoneInfo.Local.Id,
            RegistrationDeviceType = _identity.DeviceType,
            LastUpdatedAt = now,
            Version = _versionClock.Next()
        };


    private UserPasswordsData CreateInitialUserPasswordsData()
    {
        var userPasswordsData = new UserPasswordsData();
        using var passwordsKey = EncryptionKey.Create();
        userPasswordsData.PasswordKey = passwordsKey.ExportCopy();
        return userPasswordsData;
    }


    private UserDevicesData CreateInitialUserDevicesData(DateTime now, DateTimeOffset linkedAt)
    {
        var version = _versionClock.Next();
        var localDeviceData = new UserDeviceData
        {
            Id = _identity.LocalDeviceId,
            Name = DeviceNameUtil.BuildDefaultDeviceName(_identity.LocalDeviceId),
            LinkedAt = linkedAt,
            LastLoginDate = now,
            PreviousLoginDate = null,
            LastUpdatedAt = linkedAt,
            Version = version
        };
        localDeviceData.GenerateIntegrityHash();

        var userDevicesData = new UserDevicesData();
        userDevicesData.Devices.Add(localDeviceData);
        return userDevicesData;
    }


    private async Task<User> CreateEncryptedUserForRegistrationAsync(
        UserDataBundle bundle,
        byte[] usernameBytes,
        byte[] passwordSalt,
        EncryptionKey key,
        DateTimeOffset linkedAt,
        CancellationToken ct)
    {
        var usernameSalt = Hashing.GenerateSalt();
        var usernameHash = Hashing.SHA256Hash(usernameBytes, usernameSalt);
        var userData = bundle.UserData;

        var userDataTask = SerializeCompressEncryptAsync(userData, key, BackendJsonSerializerContext.Default.UserData, ct: ct);
        var generalTask = EncryptGeneralUserDataAsync(bundle.GeneralUserData, userData.GeneralUserDataKey, ct);
        var passwordsTask = EncryptUserPasswordsDataAsync(bundle.UserPasswordsData, userData.UserPasswordsDataKey, ct);
        var devicesTask = EncryptUserDevicesDataAsync(bundle.UserDevicesData, userData.UserDevicesDataKey, ct);
        var encryptionTasks = new[] { userDataTask, generalTask, passwordsTask, devicesTask };

        try
        {
            await Task.WhenAll(encryptionTasks);

            return new User
            {
                UId = userData.UId,
                UsernameSalt = usernameSalt,
                UsernameHash = usernameHash,
                PasswordSalt = passwordSalt,
                EncryptedPayload = await userDataTask,
                EncryptedGeneralUserDataPayload = await generalTask,
                EncryptedUserPasswordsDataPayload = await passwordsTask,
                EncryptedUserDevicesDataPayload = await devicesTask,
                UserDataLastModifiedAt = linkedAt,
                GeneralUserDataLastModifiedAt = linkedAt,
                UserPasswordsDataLastModifiedAt = linkedAt,
                UserDevicesDataLastModifiedAt = linkedAt,
                GeneralDataVersionPhysicalTimeUnixMilliseconds = bundle.GeneralUserData.Version.PhysicalTimeUnixMilliseconds,
                GeneralDataVersionLogicalCounter = bundle.GeneralUserData.Version.LogicalCounter,
                GeneralDataVersionOriginDeviceId = bundle.GeneralUserData.Version.OriginDeviceId,
                GeneralDataVersionOriginInstanceId = bundle.GeneralUserData.Version.OriginInstanceId
            };
        }
        catch
        {
            CryptographicOperations.ZeroMemory(usernameSalt);
            CryptographicOperations.ZeroMemory(usernameHash);
            foreach (var task in encryptionTasks)
                ZeroCompletedEncryptionTask(task);
            throw;
        }
    }


    private async Task SaveRegisteredUserAsync(
        User user,
        bool rememberMe,
        EncryptionKey key,
        CancellationToken ct)
    {
        await using var transaction = await _uow.BeginTransactionAsync(ct);
        try
        {
            user.KeyEpoch = 1;
            user.MembershipEpoch = 1;
            user.GenerateIntegrityHash();
            _rememberMe.SetRememberMe(user, rememberMe, key);
            await _userDataWriter.AddNewUserAsync(user, ct);
            await _loginIdentities.SetCanonicalAsync(user, user.GetGeneralUserDataVersion(), ct);
            await AddLocalUserDeviceLinkAsync(user.UId, ct);
            await _membershipAuthorization.CreateGenesisAsync(user, ct);
            await _controlStates.AddAsync(new UserControlState
            {
                UserId = user.UId,
                LocalOriginInstanceId = _identity.OriginInstanceId,
                NextOriginSequence = 1,
                AppliedKeyEpoch = user.KeyEpoch,
                AppliedMembershipEpoch = user.MembershipEpoch,
                LastUpdatedAtUtc = DateTimeOffset.UtcNow
            }, ct);
            await _syncStates.AddAsync(new UserSyncState
            {
                UserId = user.UId,
                LocalOriginInstanceId = _identity.OriginInstanceId,
                NextOriginRevision = 1,
                LastPublishedContentHash = [],
                LastUpdatedAtUtc = DateTimeOffset.UtcNow
            }, ct);
            await _uow.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            _uow.ClearTrackedChanges();
            throw;
        }
    }


    private Task AddLocalUserDeviceLinkAsync(Guid userId, CancellationToken ct) =>
        _localUserDevices.AddAsync(new LocalUserDevice
        {
            UserId = userId,
            LocalDeviceIdentityId = _identity.LocalDeviceId,
            IsSyncOn = true
        }, ct);

    private async Task<byte[]> EncryptGeneralUserDataAsync(GeneralUserData data, byte[] rawKey, CancellationToken ct)
    {
        using var key = EncryptionKey.FromRaw(rawKey);
        return await SerializeCompressEncryptAsync(data, key, BackendJsonSerializerContext.Default.GeneralUserData, ct: ct);
    }

    private async Task<byte[]> EncryptUserPasswordsDataAsync(UserPasswordsData data, byte[] rawKey, CancellationToken ct)
    {
        using var key = EncryptionKey.FromRaw(rawKey);
        return await SerializeCompressEncryptAsync(data, key, BackendJsonSerializerContext.Default.UserPasswordsData, ct: ct);
    }

    private async Task<byte[]> EncryptUserDevicesDataAsync(UserDevicesData data, byte[] rawKey, CancellationToken ct)
    {
        using var key = EncryptionKey.FromRaw(rawKey);
        return await SerializeCompressEncryptAsync(data, key, BackendJsonSerializerContext.Default.UserDevicesData, ct: ct);
    }


    private void ZeroCompletedEncryptionTask(Task<byte[]> task)
    {
        if (task.Status == TaskStatus.RanToCompletion)
            CryptographicOperations.ZeroMemory(task.Result);
    }
}
