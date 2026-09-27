using PasswordManagerLocal.Common.Backend.Abstractions.Repositories;
using PasswordManagerLocal.Common.Backend.Abstractions.Services;
using PasswordManagerLocal.Common.Backend.Exceptions;
using PasswordManagerLocal.Common.Backend.Models;
using PasswordManagerLocal.Common.Backend.Models.Encrypted;
using PasswordManagerLocal.Common.Backend.Security;
using PasswordManagerLocal.Common.Backend.Sync;

namespace PasswordManagerLocal.Common.Backend.Services;

public sealed class UserCanonicalHealthService : IUserCanonicalHealthService
{
    private readonly IUserCanonicalCheckpointRepository _checkpoints;
    private readonly IUserDataBundleVerificationService _verification;
    private readonly IUserSyncFaultService _faults;
    private readonly IDeviceIdentityService _identity;
    private readonly IDeletedUserBarrierRepository? _deletionBarriers;

    public UserCanonicalHealthService(
        IUserCanonicalCheckpointRepository checkpoints,
        IUserDataBundleVerificationService verification,
        IUserSyncFaultService faults,
        IDeviceIdentityService identity,
        IDeletedUserBarrierRepository? deletionBarriers = null)
    {
        _checkpoints = checkpoints;
        _verification = verification;
        _faults = faults;
        _identity = identity;
        _deletionBarriers = deletionBarriers;
    }

    public async Task<CanonicalHealthResult> VerifyAsync(
        User user,
        EncryptionKey? key,
        UserSyncKeyConfidence keyConfidence,
        bool recordFault,
        CancellationToken ct = default)
    {
        if (_deletionBarriers is not null && await _deletionBarriers.ExistsAsync(user.UId, ct))
        {
            return new CanonicalHealthResult(
                UserDataVerificationState.DeletedAccount,
                UserDataBlobKind.All,
                keyConfidence,
                "account-deleted");
        }

        try
        {
            user.VerifyIntegrity();
        }
        catch (InvalidDataIntegrityException)
        {
            if (recordFault)
                await RecordCanonicalFaultAsync(user, UserSyncFaultKind.CanonicalIntegrityMismatch, "canonical-row-integrity-mismatch", UserDataBlobKind.All, ct);
            return new CanonicalHealthResult(
                UserDataVerificationState.RowIntegrityFailure,
                UserDataBlobKind.All,
                keyConfidence,
                "canonical-row-integrity-mismatch");
        }

        var checkpoint = await _checkpoints.GetAsync(user.UId, ct);
        if (checkpoint is null)
        {
            if (recordFault)
                await RecordCanonicalFaultAsync(user, UserSyncFaultKind.CanonicalCheckpointMissing, "canonical-checkpoint-missing", UserDataBlobKind.All, ct);
            return new CanonicalHealthResult(
                UserDataVerificationState.CheckpointMissing,
                UserDataBlobKind.All,
                keyConfidence,
                "canonical-checkpoint-missing")
            {
                RowIntegrityVerified = true
            };
        }

        try
        {
            UserCanonicalCheckpointUtil.Verify(user, checkpoint, _identity);
        }
        catch (InvalidDataException ex)
        {
            var kind = ex.Message.Contains("signature", StringComparison.OrdinalIgnoreCase) ||
                       ex.Message.Contains("different local identity", StringComparison.OrdinalIgnoreCase)
                ? UserSyncFaultKind.CanonicalCheckpointSignatureFailure
                : UserSyncFaultKind.CanonicalCheckpointMismatch;
            var code = kind == UserSyncFaultKind.CanonicalCheckpointSignatureFailure
                ? "canonical-checkpoint-signature-failed"
                : "canonical-checkpoint-mismatch";
            if (recordFault)
                await RecordCanonicalFaultAsync(user, kind, code, UserDataBlobKind.All, ct);
            return new CanonicalHealthResult(
                UserDataVerificationState.CheckpointFailure,
                UserDataBlobKind.All,
                keyConfidence,
                code)
            {
                RowIntegrityVerified = true
            };
        }

        if (key is null)
        {
            return new CanonicalHealthResult(
                UserDataVerificationState.KeyNotConfirmed,
                UserDataBlobKind.None,
                keyConfidence,
                "checkpoint-verified-key-unavailable")
            {
                RowIntegrityVerified = true,
                CheckpointVerified = true
            };
        }

        using var verification = await _verification.VerifyCanonicalAsync(user, key, keyConfidence, ct);
        if (verification.IsHealthy)
        {
            return new CanonicalHealthResult(
                UserDataVerificationState.Healthy,
                UserDataBlobKind.None,
                keyConfidence,
                "canonical-fully-verified")
            {
                RowIntegrityVerified = true,
                CheckpointVerified = true,
                FullyVerified = true
            };
        }

        if (recordFault && IsIndependentlyTrusted(keyConfidence))
        {
            var kind = MapCanonicalFaultKind(verification.State);
            await RecordCanonicalFaultAsync(user, kind, verification.DiagnosticCode, verification.FailedBlobs, ct);
        }

        return new CanonicalHealthResult(
            verification.State,
            verification.FailedBlobs,
            keyConfidence,
            verification.DiagnosticCode)
        {
            RowIntegrityVerified = true,
            CheckpointVerified = true
        };
    }

    public async Task UpdateCheckpointAsync(User user, CancellationToken ct = default)
    {
        if (!_identity.IsInitialized)
            throw new InvalidOperationException("The local device signing identity must be initialized before updating a canonical checkpoint.");
        user.VerifyIntegrity();

        var existing = await _checkpoints.GetAsync(user.UId, ct);
        var replacement = UserCanonicalCheckpointUtil.Create(
            user,
            existing is null ? 1 : checked(existing.CheckpointSequence + 1),
            DateTimeOffset.UtcNow,
            _identity);

        if (existing is null)
        {
            await _checkpoints.AddAsync(replacement, ct);
            return;
        }

        existing.CheckpointSequence = replacement.CheckpointSequence;
        existing.LocalDeviceId = replacement.LocalDeviceId;
        existing.LocalOriginInstanceId = replacement.LocalOriginInstanceId;
        existing.KeyEpoch = replacement.KeyEpoch;
        existing.MembershipEpoch = replacement.MembershipEpoch;
        existing.CanonicalContentHash = replacement.CanonicalContentHash;
        existing.UserIntegrityHash = replacement.UserIntegrityHash;
        existing.SignPublicKey = replacement.SignPublicKey;
        existing.Signature = replacement.Signature;
        existing.CreatedAtUtc = replacement.CreatedAtUtc;
        _checkpoints.Update(existing);
    }

    public async Task DeleteCheckpointAsync(Guid userId, CancellationToken ct = default)
    {
        var checkpoint = await _checkpoints.GetAsync(userId, ct);
        if (checkpoint is not null)
            _checkpoints.Delete(checkpoint);
    }

    private Task RecordCanonicalFaultAsync(
        User user,
        UserSyncFaultKind kind,
        string code,
        UserDataBlobKind blobs,
        CancellationToken ct) =>
        _faults.RecordAsync(new UserSyncFaultDescriptor(
            user.UId,
            UserSyncFaultScope.LocalCanonical,
            kind,
            UserSyncHealthStatus.AwaitingEvidence,
            code)
        {
            AffectedComponent = blobs.ToString(),
            KeyEpoch = user.KeyEpoch,
            MembershipEpoch = user.MembershipEpoch,
            ExpectedHash = user.IntegrityHash,
            BlocksPublishing = true,
            // Incoming authenticated evidence must remain receivable and classifiable even when
            // the local canonical copy is unhealthy.
            BlocksMerge = false,
            BlocksLogin = true,
            BlocksGarbageCollection = true
        }, ct);

    private bool IsIndependentlyTrusted(UserSyncKeyConfidence confidence) =>
        confidence != UserSyncKeyConfidence.UnconfirmedPassword;

    private UserSyncFaultKind MapCanonicalFaultKind(UserDataVerificationState state) => state switch
    {
        UserDataVerificationState.RootDecryptFailure => UserSyncFaultKind.CanonicalRootDecryptFailure,
        UserDataVerificationState.RootIntegrityFailure => UserSyncFaultKind.CanonicalRootIntegrityFailure,
        UserDataVerificationState.GeneralBlobFailure => UserSyncFaultKind.CanonicalGeneralBlobFailure,
        UserDataVerificationState.PasswordsBlobFailure => UserSyncFaultKind.CanonicalPasswordsBlobFailure,
        UserDataVerificationState.DevicesBlobFailure => UserSyncFaultKind.CanonicalDevicesBlobFailure,
        UserDataVerificationState.BundleLinkFailure => UserSyncFaultKind.CanonicalBundleLinkFailure,
        UserDataVerificationState.LoginMetadataFailure => UserSyncFaultKind.CanonicalGeneralBlobFailure,
        _ => UserSyncFaultKind.CanonicalIntegrityMismatch
    };
}
