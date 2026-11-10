using PasswordManagerLocal.Common.Backend.Abstractions.Notifications;
using PasswordManagerLocal.Common.Backend.Models;
using PasswordManagerLocal.Common.Contracts.Notifications;

namespace PasswordManagerLocal.Common.Backend.Persistence;

/// <summary>Combines the domain scopes changed by one durable unit of work.</summary>
internal sealed class BackendChangeSet
{
    private readonly Dictionary<Guid, FrontendChangeScope> _userChanges = [];
    private FrontendChangeScope _globalScope;

    public bool IsEmpty => _globalScope == FrontendChangeScope.None && _userChanges.Count == 0;

    public void Add(Guid? userId, FrontendChangeScope scope)
    {
        if (scope == FrontendChangeScope.None)
            return;
        if (userId is null || userId == Guid.Empty)
        {
            _globalScope |= scope;
            return;
        }

        _userChanges[userId.Value] = _userChanges.TryGetValue(userId.Value, out var current)
            ? current | scope
            : scope;
    }

    public void Merge(BackendChangeSet other)
    {
        Add(null, other._globalScope);
        foreach (var change in other._userChanges)
            Add(change.Key, change.Value);
    }

    public void Publish(IBackendChangePublisher? publisher)
    {
        if (publisher is null)
            return;

        if (_globalScope != FrontendChangeScope.None)
            publisher.Publish(_globalScope);

        foreach (var change in _userChanges.OrderBy(pair => pair.Key))
        {
            publisher.Publish(change.Value, change.Key);
        }
    }

    public void AddEntityMutation(object entity, bool addedOrDeleted, Func<string, bool> changed)
    {
        switch (entity)
        {
            case User user:
                CaptureUser(addedOrDeleted, changed, user);
                break;
            case UserLoginIdentityState loginIdentity:
                CaptureLoginIdentity(addedOrDeleted, changed, loginIdentity);
                break;
            case UserDevice userDevice:
                CaptureUserDevice(addedOrDeleted, changed, userDevice);
                break;
            case LocalUserDevice localUserDevice:
                CaptureLocalUserDevice(addedOrDeleted, changed, localUserDevice);
                break;
            case Device:
                CaptureDevice(addedOrDeleted, changed);
                break;
            case LocalDeviceIdentity:
                CaptureLocalDeviceIdentity(addedOrDeleted, changed);
                break;
            case DeviceEnrollmentCommit commit:
                CaptureEnrollmentCommit(addedOrDeleted, changed, commit);
                break;
            case UserMembershipAuthorization authorization:
                CaptureMembershipAuthorization(addedOrDeleted, changed, authorization);
                break;
            case DeletedUserBarrier barrier:
                Add(barrier.UserId, FrontendChangeScope.Everything);
                break;
        }
    }

    private void CaptureUser(bool addedOrDeleted, Func<string, bool> changed, User user)
    {
        if (addedOrDeleted)
        {
            Add(user.UId, FrontendChangeScope.UserProfile | FrontendChangeScope.Passwords | FrontendChangeScope.Devices);
            return;
        }

        var scope = FrontendChangeScope.None;
        if (changed(nameof(User.EncryptedGeneralUserDataPayload)))
            scope |= FrontendChangeScope.UserProfile;
        if (changed(nameof(User.EncryptedUserPasswordsDataPayload)))
            scope |= FrontendChangeScope.Passwords;
        if (changed(nameof(User.EncryptedUserDevicesDataPayload)))
            scope |= FrontendChangeScope.Devices;
        if (changed(nameof(User.UsernameHash)) || changed(nameof(User.UsernameSalt)))
            scope |= FrontendChangeScope.UserProfile | FrontendChangeScope.AuthenticationSessions;
        if (changed(nameof(User.KeyEpoch)) || changed(nameof(User.MembershipEpoch)))
            scope |= FrontendChangeScope.AllAuthenticatedData | FrontendChangeScope.AuthenticationSessions;
        else if (changed(nameof(User.EncryptedPayload)))
            scope |= FrontendChangeScope.AuthenticationSessions;

        Add(user.UId, scope);
    }

    private void CaptureLoginIdentity(bool addedOrDeleted, Func<string, bool> changed, UserLoginIdentityState identity)
    {
        if (addedOrDeleted ||
            changed(nameof(UserLoginIdentityState.UsernameHash)) ||
            changed(nameof(UserLoginIdentityState.UsernameSalt)) ||
            changed(nameof(UserLoginIdentityState.Status)))
        {
            Add(
                identity.UserId,
                FrontendChangeScope.UserProfile | FrontendChangeScope.AuthenticationSessions);
        }
    }

    private void CaptureUserDevice(bool addedOrDeleted, Func<string, bool> changed, UserDevice link)
    {
        if (addedOrDeleted ||
            changed(nameof(UserDevice.IsSyncOn)) ||
            changed(nameof(UserDevice.IsDeleted)) ||
            changed(nameof(UserDevice.DeletedAt)))
        {
            Add(link.UserId, FrontendChangeScope.Devices);
        }
    }

    private void CaptureLocalUserDevice(bool addedOrDeleted, Func<string, bool> changed, LocalUserDevice link)
    {
        if (addedOrDeleted || changed(nameof(LocalUserDevice.IsSyncOn)))
        {
            Add(
                link.UserId,
                FrontendChangeScope.LocalDevice | FrontendChangeScope.Devices | FrontendChangeScope.BackgroundSync);
        }
    }

    private void CaptureDevice(bool addedOrDeleted, Func<string, bool> changed)
    {
        // Heartbeat timestamps are deliberately excluded: authenticated contact can update them
        // frequently, while presence transitions have their own edge-triggered notification.
        if (addedOrDeleted ||
            changed(nameof(Device.DeviceType)) ||
            changed(nameof(Device.IsTrusted)) ||
            changed(nameof(Device.IsBlocked)) ||
            changed(nameof(Device.BlockedReason)) ||
            changed(nameof(Device.BlockedAt)) ||
            changed(nameof(Device.InvalidSyncAttemptCount)) ||
            changed(nameof(Device.PublicKey)) ||
            changed(nameof(Device.SignPublicKey)) ||
            changed(nameof(Device.TlsCertFingerprint)))
        {
            // Device rows can be shared by several accounts. A global metadata-only invalidation
            // avoids doing database work in this post-save path to discover every owner.
            Add(null, FrontendChangeScope.Devices);
        }
    }

    private void CaptureLocalDeviceIdentity(bool addedOrDeleted, Func<string, bool> changed)
    {
        if (addedOrDeleted ||
            changed(nameof(LocalDeviceIdentity.IsSyncOn)) ||
            changed(nameof(LocalDeviceIdentity.DeviceType)) ||
            changed(nameof(LocalDeviceIdentity.OriginInstanceId)) ||
            changed(nameof(LocalDeviceIdentity.AgreementPrivateKeyBlob)) ||
            changed(nameof(LocalDeviceIdentity.SignPrivateKeyBlob)) ||
            changed(nameof(LocalDeviceIdentity.PFXCertificate)))
        {
            Add(null, FrontendChangeScope.LocalDevice | FrontendChangeScope.BackgroundSync);
        }
    }

    private void CaptureEnrollmentCommit(bool addedOrDeleted, Func<string, bool> changed,
        DeviceEnrollmentCommit commit)
    {
        if (addedOrDeleted ||
            changed(nameof(DeviceEnrollmentCommit.Status)))
        {
            Add(commit.UserId, FrontendChangeScope.Enrollment | FrontendChangeScope.Devices);
        }
    }

    private void CaptureMembershipAuthorization(bool addedOrDeleted, Func<string, bool> changed,
        UserMembershipAuthorization authorization)
    {
        if (addedOrDeleted ||
            changed(nameof(UserMembershipAuthorization.IsActive)) ||
            changed(nameof(UserMembershipAuthorization.EndedMembershipEpoch)))
        {
            Add(
                authorization.UserId,
                FrontendChangeScope.Devices | FrontendChangeScope.AuthenticationSessions);
        }
    }
}
