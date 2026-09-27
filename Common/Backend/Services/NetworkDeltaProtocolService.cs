using PasswordManagerLocal.Common.Backend.Abstractions.Repositories;
using PasswordManagerLocal.Common.Backend.Abstractions.Services;
using PasswordManagerLocal.Common.Backend.Exceptions;
using PasswordManagerLocal.Common.Backend.Models;
using PasswordManagerLocal.Common.Backend.Security;
using PasswordManagerLocal.Common.Backend.Sync;
using PasswordManagerLocal.Common.Backend.Utils;
using System.Security.Cryptography;
using System.Text.Json;
using PasswordManagerLocal.Common.Backend.Abstractions.Persistence;
using PasswordManagerLocal.Common.Backend.Abstractions.Security;
using PasswordManagerLocal.Common.Backend.Models.Projections;
using PasswordManagerLocal.Common.Backend.Models.Encrypted;
using static PasswordManagerLocal.Common.Contracts.Constants.DataLengthConstants;
using static PasswordManagerLocal.Common.Backend.Utils.DataCodec;

namespace PasswordManagerLocal.Common.Backend.Services;

public sealed class NetworkDeltaProtocolService : INetworkDeltaProtocolService
{
    private readonly IDeviceRepository _devices;
    private readonly IGroupRepository _groups;
    private readonly IUserDeviceRepository _userDevices;
    private readonly ISyncAuthorizationService _authorization;
    private readonly IDeviceIdentityService _identity;
    private readonly IUserMembershipAuthorizationService _membershipAuthorization;
    private readonly IUserMembershipAuthorizationRepository? _membershipHistory;
    private readonly IDeletedUserBarrierRepository? _deletionBarriers;

    private static readonly string[] SensitiveLocalOnlyPropertyNames =
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

    public NetworkDeltaProtocolService(
        IDeviceRepository devices,
        IGroupRepository groups,
        IUserDeviceRepository userDevices,
        ISyncAuthorizationService authorization,
        IDeviceIdentityService identity,
        IUserMembershipAuthorizationService membershipAuthorization,
        IUserMembershipAuthorizationRepository? membershipHistory = null,
        IDeletedUserBarrierRepository? deletionBarriers = null)
    {
        _devices = devices;
        _groups = groups;
        _userDevices = userDevices;
        _authorization = authorization;
        _identity = identity;
        _membershipAuthorization = membershipAuthorization;
        _membershipHistory = membershipHistory;
        _deletionBarriers = deletionBarriers;
    }

    public async Task<ValidatedNetworkDelta> ValidateAndReadAsync(NetworkDelta delta, CancellationToken ct = default)
    {
        SyncCryptoUtil.ValidateEncryptedEnvelope(delta, _identity.LocalDeviceId);
        VerifyDeltaSignature(delta);
        var sourceDevice = await GetAndValidateSourceDeviceAsync(delta, ct);

        var plaintextPayload = DecryptPayload(delta);
        SyncDeltaPayload payload;
        try
        {
            payload = DeserializePayload(plaintextPayload);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintextPayload);
        }

        ValidateEnvelope(delta, payload);
        SyncCryptoUtil.ValidatePayloadIntegrity(payload, delta.Ts);
        await ValidateUserSnapshotOriginAsync(payload, ct);
        await ValidateDeviceIdentityImmutabilityAsync(sourceDevice, payload, ct);

        return new ValidatedNetworkDelta(sourceDevice, payload);
    }

    public async Task ValidateSourceAuthorizationAsync(Device sourceDevice, SyncDeltaPayload payload, CancellationToken ct = default)
    {
        if (!await _authorization.CanReceiveAsync(payload, sourceDevice.Id, ct))
            throw new SyncRouteDisabledException("Synchronization is disabled for this user and device route.");

        await ValidateSourceCanApplyPayloadAsync(sourceDevice, payload, ct);
    }

    private async Task<Device> GetAndValidateSourceDeviceAsync(NetworkDelta delta, CancellationToken ct)
    {
        var sourceDevice = await _devices.GetBySignPublicKeyAsync(delta.SignPub, ct);
        if (sourceDevice is null)
            throw new UnauthorizedAccessException("Unknown sync source device.");

        if (sourceDevice.IsBlocked || !sourceDevice.IsTrusted)
            throw new UnauthorizedAccessException("Sync source device is not allowed.");

        if (!string.Equals(delta.DeviceId, BuildDeviceId(delta.SignPub), StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("Sync source device id is invalid.");

        return sourceDevice;
    }


    private async Task ValidateUserSnapshotOriginAsync(SyncDeltaPayload payload, CancellationToken ct)
    {
        if (payload.ModelType != SyncModelType.User || payload.ChangeType == SyncChangeType.Deleted)
            return;

        if (payload.UserControlOperation is not null)
        {
            await _membershipAuthorization.VerifyControlAuthorAsync(payload.UserControlOperation, ct);
            return;
        }

        var envelope = payload.UserSnapshot
            ?? throw new InvalidDataException("User snapshot envelope is missing.");
        await _membershipAuthorization.VerifySnapshotAuthorAsync(envelope, ct);
    }


    private async Task ValidateDeviceIdentityImmutabilityAsync(Device sourceDevice, SyncDeltaPayload payload, CancellationToken ct)
    {
        if (payload.ModelType != SyncModelType.Device || payload.Device is null)
            return;

        if (payload.Device.SignPublicKey.Length != PasswordManagerLocal.Common.Backend.Constants.SyncConstants.SyncDeltaEd25519PublicKeyBytes)
            throw new InvalidDataException("Device sync signing key is invalid.");

        if (payload.Device.PublicKey.Length != PasswordManagerLocal.Common.Backend.Constants.SyncConstants.SyncDeltaX25519PublicKeyBytes)
            throw new InvalidDataException("Device sync agreement key is invalid.");

        if (string.IsNullOrWhiteSpace(payload.Device.TlsCertFingerprint))
            throw new InvalidDataException("Device sync TLS fingerprint is missing.");

        if (!DeviceTypeDetector.IsValid(payload.Device.DeviceType))
            throw new InvalidDataException("Device sync type is invalid.");

        if (payload.ModelId == sourceDevice.Id)
        {
            if (!payload.Device.SignPublicKey.SequenceEqual(sourceDevice.SignPublicKey))
                throw new InvalidDataException("Source device signing key cannot be changed by sync.");

            if (!payload.Device.PublicKey.SequenceEqual(sourceDevice.PublicKey))
                throw new InvalidDataException("Source device agreement key cannot be changed by sync.");

            if (!string.Equals(FingerprintUtil.NormalizeOrEmpty(payload.Device.TlsCertFingerprint), FingerprintUtil.NormalizeOrEmpty(sourceDevice.TlsCertFingerprint), StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Source device TLS fingerprint cannot be changed by sync.");

            if (payload.Device.DeviceType != sourceDevice.DeviceType)
                throw new InvalidDataException("Source device type cannot be changed by sync.");
        }

        var existing = await _devices.GetByIdAsync(payload.ModelId, ct);
        if (existing is null)
            return;

        if (!existing.SignPublicKey.SequenceEqual(payload.Device.SignPublicKey))
            throw new InvalidDataException("Existing device signing key cannot be changed by sync.");

        if (!existing.PublicKey.SequenceEqual(payload.Device.PublicKey))
            throw new InvalidDataException("Existing device agreement key cannot be changed by sync.");

        if (!string.Equals(FingerprintUtil.NormalizeOrEmpty(existing.TlsCertFingerprint), FingerprintUtil.NormalizeOrEmpty(payload.Device.TlsCertFingerprint), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Existing device TLS fingerprint cannot be changed by sync.");

        if (existing.DeviceType != payload.Device.DeviceType)
            throw new InvalidDataException("Existing device type cannot be changed by sync.");
    }


    private async Task ValidateSourceCanApplyPayloadAsync(Device sourceDevice, SyncDeltaPayload payload, CancellationToken ct)
    {
        if (payload.ModelType == SyncModelType.User)
        {
            var isDeletionOperation = payload.UserControlOperation?.OperationType == UserControlOperationType.AccountDeletion;
            var isDeletedLocally = _deletionBarriers is not null && await _deletionBarriers.ExistsAsync(payload.ModelId, ct);
            if (isDeletionOperation || isDeletedLocally)
            {
                if (_membershipHistory is null ||
                    !await _membershipHistory.HasHistoricalAuthorizationAsync(payload.ModelId, sourceDevice.Id, ct))
                    throw new UnauthorizedAccessException("The relay device was never authorized for this account identity.");
                return;
            }

            if (!await _userDevices.HasActiveLinkAsync(payload.ModelId, sourceDevice.Id, ct))
                throw new UnauthorizedAccessException("Transport peer cannot synchronize this user.");

            if (payload.ChangeType != SyncChangeType.Deleted)
            {
                var originDeviceId = payload.UserControlOperation?.OriginDeviceId
                    ?? payload.UserSnapshot?.OriginDeviceId
                    ?? throw new InvalidDataException("The immutable user payload origin is missing.");
                if (!await _userDevices.HasActiveLinkAsync(payload.ModelId, originDeviceId, ct))
                    throw new UnauthorizedAccessException("The immutable user payload origin device is not currently authorized for this user.");
            }

            return;
        }

        if (payload.ModelType == SyncModelType.Group)
        {
            IReadOnlyCollection<Guid> userIds = await _groups.ListUserIdsAsync(payload.ModelId, ct);
            if (userIds.Count == 0)
                userIds = payload.Group?.UserIds ?? [];

            if (await _userDevices.HasAnyActiveLinkAsync(userIds, sourceDevice.Id, ct))
                return;

            throw new UnauthorizedAccessException("Source device cannot modify this group.");
        }

        if (payload.ModelType == SyncModelType.Device)
        {
            if (SyncPayloadRules.IsLocalDevicePayload(payload, _identity))
                return;

            if (payload.ModelId == sourceDevice.Id)
                return;

            if (await _userDevices.SharesActiveUserAsync(sourceDevice.Id, payload.ModelId, ct))
                return;

            if (payload.Device is not null &&
                await _userDevices.HasAnyActiveLinkAsync(payload.Device.UserIds, sourceDevice.Id, ct))
                return;

            throw new UnauthorizedAccessException("Source device cannot modify this device.");
        }

        if (payload.ModelType == SyncModelType.UserDevice)
        {
            if (payload.UserDevice is null)
                throw new InvalidDataException("User device sync payload is missing.");

            SyncPayloadRules.ValidateUserDevicePayload(payload);

            if (await _userDevices.HasActiveLinkAsync(payload.UserDevice.UserId, sourceDevice.Id, ct))
                return;

            throw new UnauthorizedAccessException("Source device cannot modify this user-device link.");
        }
    }


    private byte[] DecryptPayload(NetworkDelta delta)
    {
        try
        {
            var associatedData = SyncCryptoUtil.BuildAssociatedData(delta);
            var plaintext = _identity.DecryptFromDevice(
                delta.Payload,
                delta.EphemeralPublicKey,
                delta.Nonce,
                delta.Tag,
                associatedData);

            SyncCryptoUtil.ValidatePlaintextHash(plaintext, delta.PayloadHash);
            return plaintext;
        }
        catch (InvalidDataException)
        {
            throw;
        }
        catch (CryptographicException ex)
        {
            throw new InvalidDataException("Network delta payload decryption failed.", ex);
        }
        catch (FormatException ex)
        {
            throw new InvalidDataException("Network delta encryption key material is invalid.", ex);
        }
    }


    private SyncDeltaPayload DeserializePayload(byte[] plaintextPayload)
    {
        if (plaintextPayload.Length == 0)
            throw new InvalidDataException("Network delta payload is empty.");

        RejectSensitiveLocalOnlyPayload(plaintextPayload);

        var payload = JsonSerializer.Deserialize(
            plaintextPayload,
            BackendJsonSerializerContext.Default.SyncDeltaPayload);
        if (payload is null)
            throw new InvalidDataException("Network delta payload is invalid.");

        return UtcDateTimeUtil.NormalizeObjectGraph(payload);
    }


    private void ValidateEnvelope(NetworkDelta delta, SyncDeltaPayload payload)
    {
        if (delta.Ts <= 0)
            throw new InvalidDataException("Network delta timestamp is invalid.");

        if (payload.ModelId == Guid.Empty)
            throw new InvalidDataException("Network delta model id is invalid.");

        if (!Enum.IsDefined(payload.ModelType))
            throw new InvalidDataException("Network delta model type is invalid.");

        if (!Enum.IsDefined(payload.ChangeType))
            throw new InvalidDataException("Network delta change type is invalid.");

        var expectedEntity = $"{payload.ModelType}:{payload.ChangeType}:{payload.ModelId:N}";
        if (!string.Equals(delta.Entity, expectedEntity, StringComparison.Ordinal))
            throw new InvalidDataException("Network delta entity envelope is invalid.");

        if (payload.UserSnapshot is not null &&
            (payload.ModelType != SyncModelType.User ||
             payload.UserSnapshot.UserId != payload.ModelId ||
             payload.UserSnapshot.User.UId != payload.ModelId))
        {
            throw new InvalidDataException("User snapshot payload envelope is invalid.");
        }

        if (payload.UserControlOperation is not null &&
            (payload.ModelType != SyncModelType.User ||
             payload.ChangeType == SyncChangeType.Deleted ||
             payload.UserControlOperation.UserId != payload.ModelId))
        {
            throw new InvalidDataException("User control-operation payload envelope is invalid.");
        }

        if (payload.Group is not null && (payload.ModelType != SyncModelType.Group || payload.Group.Id != payload.ModelId))
            throw new InvalidDataException("Group sync payload envelope is invalid.");

        if (payload.Device is not null && (payload.ModelType != SyncModelType.Device || payload.Device.Id != payload.ModelId))
            throw new InvalidDataException("Device sync payload envelope is invalid.");

        if (payload.UserDevice is not null)
        {
            if (payload.ModelType != SyncModelType.UserDevice)
                throw new InvalidDataException("User device sync payload envelope is invalid.");

            var expectedModelId = SyncIdentityUtil.BuildUserDeviceModelId(payload.UserDevice.UserId, payload.UserDevice.DeviceId);
            if (payload.ModelId != expectedModelId)
                throw new InvalidDataException("User device sync model id is invalid.");
        }
    }


    private void VerifyDeltaSignature(NetworkDelta delta)
    {
        if (delta.SignPub.Length != PasswordManagerLocal.Common.Backend.Constants.SyncConstants.SyncDeltaEd25519PublicKeyBytes ||
            delta.Sig.Length != PasswordManagerLocal.Common.Backend.Constants.SyncConstants.SyncDeltaEd25519SignatureBytes)
            throw new InvalidDataException("Network delta signature is incomplete.");

        try
        {
            if (!NetDeltaSigner.VerifySignature(delta))
                throw new InvalidDataException("Network delta signature is invalid.");
        }
        catch (InvalidDataException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new InvalidDataException("Network delta signature is invalid.", ex);
        }
    }


    private void RejectSensitiveLocalOnlyPayload(byte[] payload)
    {
        try
        {
            using var doc = JsonDocument.Parse(payload);
            foreach (var propertyName in SensitiveLocalOnlyPropertyNames)
            {
                if (ContainsProperty(doc.RootElement, propertyName))
                    throw new InvalidDataException("Network delta contains local-only device or key material.");
            }
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("Network delta payload is invalid.", ex);
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


    private string BuildDeviceId(byte[] signPublicKey) =>
        Convert.ToHexString(Hashing.SHA256Hash(signPublicKey));
}
