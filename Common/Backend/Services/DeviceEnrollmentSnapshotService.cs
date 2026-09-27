using Microsoft.Extensions.DependencyInjection;
using PasswordManagerLocal.Common.Backend.Abstractions.Repositories;
using PasswordManagerLocal.Common.Backend.Abstractions.Services;
using PasswordManagerLocal.Common.Backend.Constants;
using PasswordManagerLocal.Common.Backend.Exceptions;
using PasswordManagerLocal.Common.Backend.Models;
using PasswordManagerLocal.Common.Backend.Models.Encrypted;
using PasswordManagerLocal.Common.Backend.Security;
using PasswordManagerLocal.Common.Backend.Sync.Enrollment;
using PasswordManagerLocal.Common.Backend.Utils;
using System.Security.Cryptography;
using System.Text.Json;
using Google.Protobuf;
using PasswordManagerLocal.Common.Backend.Abstractions.Persistence;
using PasswordManagerLocal.Common.Contracts.Responses;
using PasswordManagerLocal.Common.Backend.Sync;
using System.Net;
using System.Net.Sockets;
using static PasswordManagerLocal.Common.Backend.Constants.SyncConstants;
using PasswordManagerLocal.Common.Backend.State;

namespace PasswordManagerLocal.Common.Backend.Services;

public sealed class DeviceEnrollmentSnapshotService : IDeviceEnrollmentSnapshotService
{
    private readonly IDeviceIdentityService _identity;

    private static readonly string[] SensitiveLocalOnlySnapshotPropertyNames =
    [
        "SavedKey",
        "LocalDeviceIdentity",
        "LocalUserDevice",
        "LocalUserDevices",
        "DeviceIdentity",
        "AgreementPrivateKeyBlob",
        "SignPrivateKeyBlob",
        "PFXCertificate",
        "PrivateKey",
        "PrivateKeyBlob"
    ];

    public DeviceEnrollmentSnapshotService(IDeviceIdentityService identity)
    {
        _identity = identity;
    }

    public DeviceEnrollmentSnapshot DecryptAndDeserialize(
        string sessionId,
        byte[] secret,
        byte[] ciphertext,
        string sourceDeviceId,
        Guid sourceOriginInstanceId,
        byte[] sourceSignPublicKey,
        string sourceTlsFingerprint,
        Guid targetDeviceId,
        Guid targetOriginInstanceId,
        int encryptionVersion,
        byte[] nonce,
        byte[] tag)
    {
        var plaintextSnapshotBytes = DecryptEnrollmentSnapshot(
            sessionId,
            secret,
            ciphertext,
            sourceDeviceId,
            sourceOriginInstanceId,
            sourceSignPublicKey,
            sourceTlsFingerprint,
            targetDeviceId,
            targetOriginInstanceId,
            encryptionVersion,
            nonce,
            tag);

        try
        {
            RejectSensitiveLocalOnlySnapshotPayload(plaintextSnapshotBytes);
            var snapshot = JsonSerializer.Deserialize(
                plaintextSnapshotBytes,
                BackendJsonSerializerContext.Default.DeviceEnrollmentSnapshot);
            if (snapshot is null || snapshot.PrimaryUserId == Guid.Empty)
                throw new InvalidDataException("The received profile data is empty.");
            if (snapshot.PayloadVersion != SyncConstants.DeviceEnrollmentPayloadVersion)
                throw new InvalidDataException("The enrollment payload version is invalid.");

            return UtcDateTimeUtil.NormalizeObjectGraph(snapshot);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"The received profile data is invalid: {ex.Message}", ex);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintextSnapshotBytes);
        }
    }

    public Task<DeviceEnrollmentSnapshot> BuildAsync(
        IServiceProvider services,
        Guid userId,
        EnrollmentEndpoint target,
        Guid authorizingAdditionOperationId,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        var lifecycle = services.GetRequiredService<IUserLifecycleCoordinator>();
        return lifecycle.ExecuteAsync(
            userId,
            token => BuildCoreAsync(services, userId, target, authorizingAdditionOperationId, token),
            ct);
    }

    private async Task<DeviceEnrollmentSnapshot> BuildCoreAsync(
        IServiceProvider services,
        Guid userId,
        EnrollmentEndpoint target,
        Guid authorizingAdditionOperationId,
        CancellationToken ct)
    {
        var users = services.GetRequiredService<IUserRepository>();
        var userDevicesRepository = services.GetRequiredService<IUserDeviceRepository>();
        var groupsRepository = services.GetRequiredService<IGroupRepository>();
        var devicesRepository = services.GetRequiredService<IDeviceRepository>();
        var authorizationsRepository = services.GetRequiredService<IUserMembershipAuthorizationRepository>();
        var cutoffsRepository = services.GetRequiredService<IUserOriginRemovalCutoffRepository>();
        var operationsRepository = services.GetRequiredService<IUserControlOperationRepository>();
        var statesRepository = services.GetRequiredService<IUserControlStateRepository>();
        var knowledgeRepository = services.GetRequiredService<IUserRevisionKnowledgeRepository>();
        var snapshotsRepository = services.GetRequiredService<IUserSyncSnapshotRepository>();

        // Enrollment exports a canonical baseline. While the user key is available, first true-merge
        // every valid pending snapshot and compact only causally stable tombstones under the same
        // re-entrant per-user lifecycle lock.
        var mergeCoordinator = services.GetRequiredService<IUserSnapshotMergeCoordinator>();
        var keyResolver = services.GetRequiredService<IUserSyncKeyResolverService>();
        var garbageCollector = services.GetRequiredService<IUserTombstoneGarbageCollector>();
        var trackedUser = await users.GetByIdWithRelationsAsync(userId, ct) ?? throw new UserNotFoundException();
        if (!keyResolver.TryResolve(trackedUser, out var activeKey) || activeKey is null)
            throw new InvalidOperationException("Enrollment export requires an available user key so login projection metadata can be verified against decrypted canonical data.");

        using (activeKey)
        {
            await mergeCoordinator.TryMergePendingUnderLifecycleAsync(userId, activeKey, ct);
            await garbageCollector.CollectAsync(userId, activeKey, ct);

            var verifiedUser = await users.GetByIdAsync(userId, ct) ?? throw new UserNotFoundException();
            using var verifiedBundle = await services.GetRequiredService<IUserDataReaderService>()
                .GetAndVerifyUserDataBundleAsync(verifiedUser, activeKey, ct);
            UserLoginIdentityMetadataUtil.Verify(verifiedUser, verifiedBundle.GeneralUserData);
            var canonicalHealth = await services.GetRequiredService<IUserCanonicalHealthService>()
                .VerifyAsync(verifiedUser, activeKey, UserSyncKeyConfidence.ExplicitlyTrusted, recordFault: true, ct: ct);
            if (!canonicalHealth.FullyVerified)
                throw new InvalidDataException("Enrollment export requires a fully verified signed canonical checkpoint.");
        }

        var user = await users.GetByIdAsNoTrackingWithRelationsAsync(userId, ct) ?? throw new UserNotFoundException();
        if (user.KeyEpoch <= 0 || user.MembershipEpoch <= 0)
            throw new InvalidDataException("The canonical enrollment epochs are invalid.");
        user.VerifyIntegrity();

        var authorizations = await authorizationsRepository.ListForUserAsync(userId, ct);
        var targetAuthorization = authorizations.SingleOrDefault(row => row.IsActive && row.DeviceId == target.DeviceId && row.OriginInstanceId == target.OriginInstanceId)
            ?? throw new InvalidDataException("The target installation is not authoritatively authorized.");
        if (targetAuthorization.AdditionOperationId != authorizingAdditionOperationId || targetAuthorization.AdditionOperationHash is null)
            throw new InvalidDataException("The target authorization does not reference the committed addition operation.");

        var operations = await operationsRepository.ListForUserAsync(userId, ct);
        var addition = operations.SingleOrDefault(operation => operation.OperationId == authorizingAdditionOperationId)
            ?? throw new InvalidDataException("The committed device-addition operation is missing.");
        if (addition.Status != UserControlOperationStatus.Applied || addition.OperationType != UserControlOperationType.DeviceAddition ||
            !Hashing.Verify(addition.OperationHash, targetAuthorization.AdditionOperationHash))
            throw new InvalidDataException("The target device-addition operation is not applied or does not match authorization history.");

        var groups = await groupsRepository.ListByUserWithUserIdsAsNoTrackingAsync(userId, ct);
        foreach (var group in groups)
        {
            var integritySource = new Group { Id = group.Id, EncryptedPayload = group.EncryptedPayload, LastModifiedAt = group.LastModifiedAt };
            integritySource.GenerateIntegrityHash();
            group.IntegrityHash = integritySource.IntegrityHash;
        }

        var currentDeviceIds = authorizations.Where(row => row.IsActive).Select(row => row.DeviceId).Distinct().ToArray();
        var currentDevices = await devicesRepository.ListByIdsAsync(currentDeviceIds.Where(id => id != _identity.LocalDeviceId).ToArray(), ct);
        var deviceSnapshots = currentDevices.Select(ToDeviceSnapshot).ToList();
        if (currentDeviceIds.Contains(_identity.LocalDeviceId))
        {
            var local = new Device
            {
                Id = _identity.LocalDeviceId,
                PublicKey = _identity.AgreementPublicKey,
                SignPublicKey = _identity.SignPublicKey,
                TlsCertFingerprint = _identity.FingerprintHex,
                DeviceType = _identity.DeviceType,
                LastSync = DateTime.MinValue,
                LastSeen = DateTime.MinValue,
                IsTrusted = true,
                IsBlocked = false,
                LastModifiedAt = _identity.CreatedAt
            };
            local.GenerateIntegrityHash();
            deviceSnapshots.Add(ToDeviceSnapshot(local));
        }

        var currentLinks = await userDevicesRepository.ListByUserAsync(userId, ct);
        var activeByDevice = authorizations.Where(row => row.IsActive).GroupBy(row => row.DeviceId).ToDictionary(group => group.Key);
        var relationshipSnapshots = new List<DeviceEnrollmentUserDeviceSnapshot>();
        foreach (var deviceId in activeByDevice.Keys.OrderBy(id => id))
        {
            var existing = currentLinks.FirstOrDefault(link => link.DeviceId == deviceId);
            var source = new UserDevice
            {
                UserId = userId,
                DeviceId = deviceId,
                IsSyncOn = existing?.IsSyncOn ?? true,
                IsDeleted = false,
                DeletedAt = null,
                LastModifiedAt = existing?.LastModifiedAt ?? activeByDevice[deviceId].Min(row => row.CreatedAtUtc)
            };
            source.GenerateIntegrityHash();
            relationshipSnapshots.Add(new DeviceEnrollmentUserDeviceSnapshot
            {
                UserId = source.UserId,
                DeviceId = source.DeviceId,
                IsSyncOn = source.IsSyncOn,
                IsDeleted = false,
                DeletedAt = null,
                LastModifiedAt = UtcDateTimeUtil.ToUtc(source.LastModifiedAt),
                IntegrityHash = source.IntegrityHash.ToArray()
            });
        }

        var state = await statesRepository.GetAsync(userId, ct)
            ?? throw new InvalidDataException("The authoritative control state is missing.");
        if (state.AppliedKeyEpoch != user.KeyEpoch || state.AppliedMembershipEpoch != user.MembershipEpoch)
            throw new InvalidDataException("The control state does not match canonical epochs.");

        var knowledge = await knowledgeRepository.ListForUserAsync(userId, ct);
        var cutoffs = await cutoffsRepository.ListForUserAsync(userId, ct);
        var retainedSnapshots = (await snapshotsRepository.ListForUserAsync(userId, ct))
            .Where(row => row.Status is
                UserSyncSnapshotStatus.Pending or
                UserSyncSnapshotStatus.LocalPublished or
                UserSyncSnapshotStatus.MergedReceipt or
                UserSyncSnapshotStatus.IsolatedFork)
            .ToList();
        if (authorizations.Count > TombstoneConstants.MaxMembershipHistoryRowsPerUser ||
            cutoffs.Count > TombstoneConstants.MaxRemovalCutoffRowsPerUser ||
            knowledge.Count > TombstoneConstants.MaxRevisionKnowledgeRowsPerUser ||
            retainedSnapshots.Count > TombstoneConstants.MaxCausalSnapshotEvidenceRowsPerUser)
        {
            throw new InvalidDataException("The enrollment causal-evidence baseline exceeds safe limits.");
        }

        return new DeviceEnrollmentSnapshot
        {
            PrimaryUserId = user.UId,
            TargetDeviceId = target.DeviceId,
            TargetOriginInstanceId = target.OriginInstanceId,
            TargetSignPublicKeyHash = Hashing.SHA256Hash(target.SignPublicKey),
            TargetAgreementPublicKeyHash = Hashing.SHA256Hash(target.AgreementPublicKey),
            TargetTlsCertFingerprint = SyncIdentityUtil.NormalizeFingerprint(target.TlsCertFingerprint),
            TargetDeviceType = target.DeviceType,
            AuthorizingAdditionOperationId = addition.OperationId,
            AuthorizingAdditionOperationHash = addition.OperationHash.ToArray(),
            Users =
            [
                new DeviceEnrollmentUserSnapshot
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
                    KeyEpoch = user.KeyEpoch,
                    MembershipEpoch = user.MembershipEpoch,
                    LastModifiedAt = UtcDateTimeUtil.ToUtc(user.LastModifiedAt),
                    UserDataLastModifiedAt = UtcDateTimeUtil.ToUtc(user.UserDataLastModifiedAt),
                    GeneralUserDataLastModifiedAt = UtcDateTimeUtil.ToUtc(user.GeneralUserDataLastModifiedAt),
                    UserPasswordsDataLastModifiedAt = UtcDateTimeUtil.ToUtc(user.UserPasswordsDataLastModifiedAt),
                    UserDevicesDataLastModifiedAt = UtcDateTimeUtil.ToUtc(user.UserDevicesDataLastModifiedAt),
                    IntegrityHash = user.IntegrityHash.ToArray(),
                    GroupIds = user.Groups.Select(group => group.Id).Distinct().ToList()
                }
            ],
            Groups = groups.Select(group => new DeviceEnrollmentGroupSnapshot
            {
                Id = group.Id,
                EncryptedPayload = group.EncryptedPayload.ToArray(),
                LastModifiedAt = UtcDateTimeUtil.ToUtc(group.LastModifiedAt),
                IntegrityHash = group.IntegrityHash.ToArray(),
                UserIds = group.UserIds.ToList()
            }).ToList(),
            Devices = deviceSnapshots,
            UserDevices = relationshipSnapshots,
            MembershipAuthorizations = authorizations.Select(row => new DeviceEnrollmentMembershipAuthorizationSnapshot
            {
                AuthorizationId = row.AuthorizationId,
                UserId = row.UserId,
                DeviceId = row.DeviceId,
                OriginInstanceId = row.OriginInstanceId,
                SignPublicKey = row.SignPublicKey.ToArray(),
                SignPublicKeyHash = row.SignPublicKeyHash.ToArray(),
                AgreementPublicKeyHash = row.AgreementPublicKeyHash.ToArray(),
                TlsCertFingerprint = row.TlsCertFingerprint,
                DeviceType = row.DeviceType,
                StartedMembershipEpoch = row.StartedMembershipEpoch,
                EndedMembershipEpoch = row.EndedMembershipEpoch,
                MinimumKeyEpoch = row.MinimumKeyEpoch,
                MaximumKeyEpoch = row.MaximumKeyEpoch,
                IsActive = row.IsActive,
                IsGenesis = row.IsGenesis,
                AdditionOperationId = row.AdditionOperationId,
                AdditionOperationHash = row.AdditionOperationHash?.ToArray(),
                RemovalOperationId = row.RemovalOperationId,
                RemovalOperationHash = row.RemovalOperationHash?.ToArray(),
                CreatedAtUtc = row.CreatedAtUtc,
                EndedAtUtc = row.EndedAtUtc
            }).ToList(),
            RemovalCutoffs = cutoffs.Select(row => new DeviceEnrollmentRemovalCutoffSnapshot
            {
                CutoffId = row.CutoffId, UserId = row.UserId, DeviceId = row.DeviceId, OriginInstanceId = row.OriginInstanceId,
                UserKeyEpoch = row.UserKeyEpoch, HighestAcceptedSnapshotRevision = row.HighestAcceptedSnapshotRevision,
                HighestAcceptedControlSequence = row.HighestAcceptedControlSequence, ResultingMembershipEpoch = row.ResultingMembershipEpoch,
                AuthorizationId = row.AuthorizationId, RemovalOperationId = row.RemovalOperationId,
                RemovalOperationHash = row.RemovalOperationHash.ToArray(), CreatedAtUtc = row.CreatedAtUtc
            }).ToList(),
            ControlOperations = operations.Select(row => new DeviceEnrollmentControlOperationSnapshot
            {
                EnvelopePayload = row.EnvelopePayload.ToArray(), Status = row.Status, StatusReason = row.StatusReason,
                ConflictingOperationHash = row.ConflictingOperationHash?.ToArray(), ReceivedAtUtc = row.ReceivedAtUtc, AppliedAtUtc = row.AppliedAtUtc
            }).ToList(),
            ControlStates =
            [
                new DeviceEnrollmentControlStateSnapshot
                {
                    UserId = state.UserId, AppliedKeyEpoch = state.AppliedKeyEpoch, AppliedMembershipEpoch = state.AppliedMembershipEpoch,
                    HasConflict = state.HasConflict, ConflictReason = state.ConflictReason, ConflictingOperationId = state.ConflictingOperationId,
                    ConflictingOperationHash = state.ConflictingOperationHash?.ToArray()
                }
            ],
            RevisionKnowledge = knowledge.Select(row => new DeviceEnrollmentRevisionKnowledgeSnapshot
            {
                UserId = row.UserId, OriginDeviceId = row.OriginDeviceId, OriginInstanceId = row.OriginInstanceId,
                UserKeyEpoch = row.UserKeyEpoch, HighestStoredRevision = row.HighestStoredRevision,
                HighestStoredSnapshotHash = row.HighestStoredSnapshotHash.ToArray(), HighestMergedRevision = row.HighestMergedRevision,
                LastUpdatedAtUtc = row.LastUpdatedAtUtc
            }).ToList(),
            PendingSnapshots = retainedSnapshots.Select(ToPendingSnapshot).ToList()
        };
    }

    private static DeviceEnrollmentPendingSnapshot ToPendingSnapshot(UserSyncSnapshot row)
    {
        var isQuarantined = row.Status == UserSyncSnapshotStatus.IsolatedFork;
        return new DeviceEnrollmentPendingSnapshot
        {
            EnvelopePayload = row.EnvelopePayload.ToArray(),
            Status = row.Status,
            // Empty diagnostic values are an absence representation used by some persistence/
            // serialization paths. Export only meaningful quarantine metadata, and never attach
            // stale diagnostics to healthy retained evidence.
            QuarantineReason = isQuarantined && !string.IsNullOrWhiteSpace(row.QuarantineReason)
                ? row.QuarantineReason
                : null,
            ConflictingSnapshotHash = isQuarantined && row.ConflictingSnapshotHash is { Length: > 0 }
                ? row.ConflictingSnapshotHash.ToArray()
                : null,
            ReceivedAtUtc = row.ReceivedAtUtc
        };
    }

    private DeviceEnrollmentDeviceSnapshot ToDeviceSnapshot(Device device)
    {
        device.GenerateIntegrityHash();
        return new DeviceEnrollmentDeviceSnapshot
        {
            Id = device.Id,
            PublicKey = device.PublicKey.ToArray(),
            SignPublicKey = device.SignPublicKey.ToArray(),
            TlsCertFingerprint = device.TlsCertFingerprint,
            DeviceType = device.DeviceType,
            LastKnownHash = device.LastKnownHash.ToArray(),
            LastSync = UtcDateTimeUtil.ToUtc(device.LastSync),
            LastSeen = UtcDateTimeUtil.ToUtc(device.LastSeen),
            IsTrusted = device.IsTrusted,
            IsBlocked = device.IsBlocked,
            BlockedReason = device.BlockedReason,
            BlockedAt = UtcDateTimeUtil.ToUtc(device.BlockedAt),
            InvalidSyncAttemptCount = device.InvalidSyncAttemptCount,
            LastInvalidSyncAttemptAt = UtcDateTimeUtil.ToUtc(device.LastInvalidSyncAttemptAt),
            LastModifiedAt = UtcDateTimeUtil.ToUtc(device.LastModifiedAt),
            IntegrityHash = device.IntegrityHash.ToArray(),
            UserIds = []
        };
    }


    public async Task EnsureEncryptedDeviceDataAsync(
        IUserDataReaderService reader,
        IUserDataWriterService writer,
        User user,
        Guid token,
        Guid deviceId,
        SyncVersionStamp version,
        CancellationToken ct = default)
    {
        SyncVersionStampComparer.Validate(version);

        using var bundle = await reader.GetAndVerifyUserDataBundleAsync(user, token, ct);
        if (bundle.UserDevicesData.Devices.Any(device => device.Id == deviceId))
            return;

        var baseName = DeviceNameUtil.BuildDefaultDeviceName(deviceId);
        var name = BuildUniqueEncryptedDeviceName(bundle.UserDevicesData, baseName, deviceId);
        var deviceData = new UserDeviceData
        {
            Id = deviceId,
            Name = name,
            LinkedAt = DateTimeOffset.UtcNow,
            LastUpdatedAt = DateTimeOffset.UtcNow,
            Version = version
        };
        deviceData.GenerateIntegrityHash();
        bundle.UserDevicesData.DeletedDevices.RemoveAll(deleted => deleted.Id == deviceData.Id);
        bundle.UserDevicesData.Devices.Add(deviceData);
        await writer.UpdateUserDataBundleAsync(bundle, token, UserDataBlobKind.Devices, false, ct);
    }


    private string BuildUniqueEncryptedDeviceName(UserDevicesData userDevicesData, string requestedName, Guid deviceId)
    {
        var baseName = string.IsNullOrWhiteSpace(requestedName)
            ? DeviceNameUtil.BuildDefaultDeviceName(deviceId)
            : requestedName.Trim();

        bool IsTaken(string value) => userDevicesData.Devices.Any(device =>
            device.Id != deviceId && string.Equals(device.Name, value, StringComparison.OrdinalIgnoreCase));

        if (!IsTaken(baseName))
            return baseName;

        for (var i = 2; i < 100; i++)
        {
            var suffix = $"-{i}";
            var prefixLength = Math.Min(baseName.Length, 64 - suffix.Length);
            var candidate = baseName[..prefixLength] + suffix;
            if (!IsTaken(candidate))
                return candidate;
        }

        throw new InvalidInputException();
    }


    public (byte[] Ciphertext, byte[] Nonce, byte[] Tag) Encrypt(
        string sessionId,
        byte[] secret,
        byte[] plaintext,
        string sourceDeviceId,
        Guid sourceOriginInstanceId,
        byte[] sourceSignPublicKey,
        string sourceTlsFingerprint,
        Guid targetDeviceId,
        Guid targetOriginInstanceId)
    {
        var key = DeviceEnrollmentCode.BuildSnapshotEncryptionKey(sessionId, secret);
        var nonce = RandomNumberGenerator.GetBytes(SyncConstants.EnrollmentSnapshotEncryptionNonceBytes);
        var tag = new byte[SyncConstants.EnrollmentSnapshotEncryptionTagBytes];
        var ciphertext = new byte[plaintext.Length];
        var aad = DeviceEnrollmentCode.BuildSnapshotEncryptionAad(sessionId, sourceDeviceId, sourceOriginInstanceId, sourceSignPublicKey, sourceTlsFingerprint, targetDeviceId, targetOriginInstanceId);

        using var aes = new AesGcm(key, SyncConstants.EnrollmentSnapshotEncryptionTagBytes);
        aes.Encrypt(nonce, plaintext, ciphertext, tag, aad);

        CryptographicOperations.ZeroMemory(key);
        return (ciphertext, nonce, tag);
    }


    private byte[] DecryptEnrollmentSnapshot(
        string sessionId,
        byte[] secret,
        byte[] ciphertext,
        string sourceDeviceId,
        Guid sourceOriginInstanceId,
        byte[] sourceSignPublicKey,
        string sourceTlsFingerprint,
        Guid targetDeviceId,
        Guid targetOriginInstanceId,
        int encryptionVersion,
        byte[] nonce,
        byte[] tag)
    {
        if (encryptionVersion != SyncConstants.EnrollmentSnapshotEncryptionVersion)
            throw new InvalidDataException("The enrollment snapshot encryption version is invalid.");

        if (nonce.Length != SyncConstants.EnrollmentSnapshotEncryptionNonceBytes)
            throw new InvalidDataException("The enrollment snapshot encryption nonce is invalid.");

        if (tag.Length != SyncConstants.EnrollmentSnapshotEncryptionTagBytes)
            throw new InvalidDataException("The enrollment snapshot authentication tag is invalid.");

        var key = DeviceEnrollmentCode.BuildSnapshotEncryptionKey(sessionId, secret);
        var plaintext = new byte[ciphertext.Length];
        var aad = DeviceEnrollmentCode.BuildSnapshotEncryptionAad(sessionId, sourceDeviceId, sourceOriginInstanceId, sourceSignPublicKey, sourceTlsFingerprint, targetDeviceId, targetOriginInstanceId);

        try
        {
            using var aes = new AesGcm(key, SyncConstants.EnrollmentSnapshotEncryptionTagBytes);
            aes.Decrypt(nonce, ciphertext, tag, plaintext, aad);
            return plaintext;
        }
        catch
        {
            CryptographicOperations.ZeroMemory(plaintext);
            throw;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }


    private void RejectSensitiveLocalOnlySnapshotPayload(byte[] payload)
    {
        try
        {
            using var doc = JsonDocument.Parse(payload);
            foreach (var propertyName in SensitiveLocalOnlySnapshotPropertyNames)
            {
                if (ContainsProperty(doc.RootElement, propertyName))
                    throw new InvalidDataException("Enrollment snapshot contains local-only device or key material.");
            }
        }
        catch (JsonException)
        {
            throw;
        }
    }


    private bool ContainsProperty(JsonElement element, string propertyName)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase))
                    return true;

                if (ContainsProperty(property.Value, propertyName))
                    return true;
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                if (ContainsProperty(item, propertyName))
                    return true;
            }
        }

        return false;
    }
}
