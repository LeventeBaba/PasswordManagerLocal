using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
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

    public static BackendChangeSet Capture(ChangeTracker changeTracker)
    {
        var result = new BackendChangeSet();
        foreach (var entry in changeTracker.Entries())
        {
            if (entry.State is EntityState.Unchanged or EntityState.Detached)
                continue;

            switch (entry.Entity)
            {
                case User user:
                    CaptureUser(result, entry, user);
                    break;
                case UserLoginIdentityState loginIdentity:
                    CaptureLoginIdentity(result, entry, loginIdentity);
                    break;
                case UserDevice userDevice:
                    CaptureUserDevice(result, entry, userDevice);
                    break;
                case LocalUserDevice localUserDevice:
                    CaptureLocalUserDevice(result, entry, localUserDevice);
                    break;
                case Device device:
                    CaptureDevice(result, entry, device);
                    break;
                case Group group:
                    CaptureGroup(result, entry, group);
                    break;
                case LocalDeviceIdentity identity:
                    CaptureLocalDeviceIdentity(result, entry, identity);
                    break;
                case DeviceEnrollmentCommit commit:
                    CaptureEnrollmentCommit(result, entry, commit);
                    break;
                case UserMembershipAuthorization authorization:
                    CaptureMembershipAuthorization(result, entry, authorization);
                    break;
                case DeletedUserBarrier barrier:
                    result.Add(barrier.UserId, FrontendChangeScope.Everything);
                    break;
                default:
                    CaptureGroupMembership(result, entry);
                    break;
            }
        }

        return result;
    }

    private static void CaptureUser(BackendChangeSet changes, EntityEntry entry, User user)
    {
        if (entry.State is EntityState.Added or EntityState.Deleted)
        {
            changes.Add(user.UId, FrontendChangeScope.UserProfile | FrontendChangeScope.Passwords | FrontendChangeScope.Devices);
            return;
        }

        var scope = FrontendChangeScope.None;
        if (Changed(entry, nameof(User.EncryptedGeneralUserDataPayload)))
            scope |= FrontendChangeScope.UserProfile;
        if (Changed(entry, nameof(User.EncryptedUserPasswordsDataPayload)))
            scope |= FrontendChangeScope.Passwords;
        if (Changed(entry, nameof(User.EncryptedUserDevicesDataPayload)))
            scope |= FrontendChangeScope.Devices;
        if (Changed(entry, nameof(User.UsernameHash)) || Changed(entry, nameof(User.UsernameSalt)))
            scope |= FrontendChangeScope.UserProfile | FrontendChangeScope.AuthenticationSessions;
        if (Changed(entry, nameof(User.KeyEpoch)) || Changed(entry, nameof(User.MembershipEpoch)))
            scope |= FrontendChangeScope.AllAuthenticatedData | FrontendChangeScope.AuthenticationSessions;
        else if (Changed(entry, nameof(User.EncryptedPayload)))
            scope |= FrontendChangeScope.AuthenticationSessions;

        changes.Add(user.UId, scope);
    }

    private static void CaptureLoginIdentity(BackendChangeSet changes, EntityEntry entry, UserLoginIdentityState identity)
    {
        if (entry.State is EntityState.Added or EntityState.Deleted ||
            Changed(entry, nameof(UserLoginIdentityState.UsernameHash)) ||
            Changed(entry, nameof(UserLoginIdentityState.UsernameSalt)) ||
            Changed(entry, nameof(UserLoginIdentityState.Status)))
        {
            changes.Add(
                identity.UserId,
                FrontendChangeScope.UserProfile | FrontendChangeScope.AuthenticationSessions);
        }
    }

    private static void CaptureUserDevice(BackendChangeSet changes, EntityEntry entry, UserDevice link)
    {
        if (entry.State is EntityState.Added or EntityState.Deleted ||
            Changed(entry, nameof(UserDevice.IsSyncOn)) ||
            Changed(entry, nameof(UserDevice.IsDeleted)) ||
            Changed(entry, nameof(UserDevice.DeletedAt)))
        {
            changes.Add(link.UserId, FrontendChangeScope.Devices);
        }
    }

    private static void CaptureLocalUserDevice(BackendChangeSet changes, EntityEntry entry, LocalUserDevice link)
    {
        if (entry.State is EntityState.Added or EntityState.Deleted || Changed(entry, nameof(LocalUserDevice.IsSyncOn)))
        {
            changes.Add(
                link.UserId,
                FrontendChangeScope.LocalDevice | FrontendChangeScope.Devices | FrontendChangeScope.BackgroundSync);
        }
    }

    private static void CaptureDevice(BackendChangeSet changes, EntityEntry entry, Device device)
    {
        // Heartbeat timestamps are deliberately excluded: authenticated contact can update them
        // frequently, while presence transitions have their own edge-triggered notification.
        if (entry.State is EntityState.Added or EntityState.Deleted ||
            Changed(entry, nameof(Device.DeviceType)) ||
            Changed(entry, nameof(Device.IsTrusted)) ||
            Changed(entry, nameof(Device.IsBlocked)) ||
            Changed(entry, nameof(Device.BlockedReason)) ||
            Changed(entry, nameof(Device.BlockedAt)) ||
            Changed(entry, nameof(Device.InvalidSyncAttemptCount)) ||
            Changed(entry, nameof(Device.PublicKey)) ||
            Changed(entry, nameof(Device.SignPublicKey)) ||
            Changed(entry, nameof(Device.TlsCertFingerprint)))
        {
            // Device rows can be shared by several accounts. A global metadata-only invalidation
            // avoids doing database work in this post-save path to discover every owner.
            changes.Add(null, FrontendChangeScope.Devices);
        }
    }

    private static void CaptureLocalDeviceIdentity(
        BackendChangeSet changes,
        EntityEntry entry,
        LocalDeviceIdentity identity)
    {
        if (entry.State is EntityState.Added or EntityState.Deleted ||
            Changed(entry, nameof(LocalDeviceIdentity.IsSyncOn)) ||
            Changed(entry, nameof(LocalDeviceIdentity.DeviceType)) ||
            Changed(entry, nameof(LocalDeviceIdentity.OriginInstanceId)) ||
            Changed(entry, nameof(LocalDeviceIdentity.AgreementPrivateKeyBlob)) ||
            Changed(entry, nameof(LocalDeviceIdentity.SignPrivateKeyBlob)) ||
            Changed(entry, nameof(LocalDeviceIdentity.PFXCertificate)))
        {
            changes.Add(null, FrontendChangeScope.LocalDevice | FrontendChangeScope.BackgroundSync);
        }
    }

    private static void CaptureGroup(BackendChangeSet changes, EntityEntry entry, Group group)
    {
        if (entry.State is not (EntityState.Added or EntityState.Deleted) &&
            !Changed(entry, nameof(Group.EncryptedPayload)))
        {
            return;
        }

        if (group.Users.Count == 0)
            changes.Add(null, FrontendChangeScope.Passwords);
        else
            foreach (var user in group.Users)
                changes.Add(user.UId, FrontendChangeScope.Passwords);
    }

    private static void CaptureEnrollmentCommit(
        BackendChangeSet changes,
        EntityEntry entry,
        DeviceEnrollmentCommit commit)
    {
        if (entry.State is EntityState.Added or EntityState.Deleted ||
            Changed(entry, nameof(DeviceEnrollmentCommit.Status)))
        {
            changes.Add(commit.UserId, FrontendChangeScope.Enrollment | FrontendChangeScope.Devices);
        }
    }

    private static void CaptureMembershipAuthorization(
        BackendChangeSet changes,
        EntityEntry entry,
        UserMembershipAuthorization authorization)
    {
        if (entry.State is EntityState.Added or EntityState.Deleted ||
            Changed(entry, nameof(UserMembershipAuthorization.IsActive)) ||
            Changed(entry, nameof(UserMembershipAuthorization.EndedMembershipEpoch)))
        {
            changes.Add(
                authorization.UserId,
                FrontendChangeScope.Devices | FrontendChangeScope.AuthenticationSessions);
        }
    }

    private static void CaptureGroupMembership(BackendChangeSet changes, EntityEntry entry)
    {
        if (!string.Equals(entry.Metadata.GetTableName(), "GroupMembers", StringComparison.Ordinal))
            return;

        Guid? userId = null;
        foreach (var property in entry.Properties)
        {
            if (!property.Metadata.Name.Contains("User", StringComparison.OrdinalIgnoreCase))
                continue;

            var value = property.CurrentValue ?? property.OriginalValue;
            if (value is Guid candidate && candidate != Guid.Empty)
            {
                userId = candidate;
                break;
            }
        }

        changes.Add(userId, FrontendChangeScope.Passwords);
    }

    private static bool Changed(EntityEntry entry, string propertyName)
    {
        var property = entry.Property(propertyName);
        return property.IsModified && !ValuesEqual(property.OriginalValue, property.CurrentValue);
    }

    private static bool ValuesEqual(object? original, object? current) =>
        original is byte[] originalBytes && current is byte[] currentBytes
            ? originalBytes.AsSpan().SequenceEqual(currentBytes)
            : Equals(original, current);
}
