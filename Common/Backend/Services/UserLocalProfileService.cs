using System.Security.Cryptography;
using System.Text;
using PasswordManagerLocal.Common.Backend.Abstractions.Persistence;
using PasswordManagerLocal.Common.Backend.Abstractions.Repositories;
using PasswordManagerLocal.Common.Backend.Abstractions.Services;
using PasswordManagerLocal.Common.Backend.Exceptions;
using PasswordManagerLocal.Common.Backend.Models;
using PasswordManagerLocal.Common.Backend.Security;
using PasswordManagerLocal.Common.Backend.Sync;
using PasswordManagerLocal.Common.Backend.Utils;
using PasswordManagerLocal.Common.Contracts.Requests;

namespace PasswordManagerLocal.Common.Backend.Services;

/// <summary>Explicitly forgets one already disconnected local copy; never deletes an account globally.</summary>
public sealed class UserLocalProfileService(
    IUserRepository users,
    IUserMembershipAuthorizationRepository authorizations,
    IUserControlOperationRepository operations,
    IUserMembershipAuthorizationService membership,
    IUserAccountDeletionStore localState,
    IUserLifecycleCoordinator lifecycle,
    IUnitOfWork uow,
    IInteractiveSessionStateService sessions,
    ISyncRuntimeService runtime,
    IDeviceIdentityService identity) : IUserLocalProfileService
{
    private static readonly byte[] DummySalt = Hashing.GenerateSalt();

    public async Task RemoveAsync(LoginRequest request, bool acknowledgeDataLoss, CancellationToken ct = default)
    {
        if (request is null || !request.Validate() || !acknowledgeDataLoss) throw new InvalidInputException();
        var username = Encoding.UTF8.GetBytes(request.Username);
        try
        {
            // Canonical lookup is deliberate: a revoked/faulted copy cannot create a login
            // session, but can authenticate its own local erase without restoring authority.
            var matches = new List<Guid>();
            foreach (var id in await users.ListUserIdsAsync(ct))
            {
                var candidate = await users.GetByIdAsNoTrackingAsync(id, ct);
                if (candidate is not null && Hashing.Verify(candidate.UsernameHash, Hashing.SHA256Hash(username, candidate.UsernameSalt)))
                    matches.Add(id);
            }
            if (matches.Count != 1)
            {
                using var dummy = EncryptionKey.FromPassword(request.Password, DummySalt);
                throw new InvalidInputException();
            }
            var userId = matches[0];
            await lifecycle.ExecuteAsync(userId, async inner =>
            {
                uow.ClearTrackedChanges();
                await using var transaction = await uow.BeginTransactionAsync(inner);
                var user = await users.GetByIdAsync(userId, inner) ?? throw new InvalidInputException();
                try
                {
                    using var key = EncryptionKey.FromPassword(request.Password, user.PasswordSalt);
                    using var root = await DataCodec.DecryptDecompressDeserializeAsync(user.EncryptedPayload, key,
                        BackendJsonSerializerContext.Default.UserData, ct: inner) ?? throw new InvalidInputException();
                    root.VerifyIntegrity();
                    if (root.UId != userId) throw new InvalidInputException();
                    using var generalKey = EncryptionKey.FromRaw(root.GeneralUserDataKey);
                    using var general = await DataCodec.DecryptDecompressDeserializeAsync(user.EncryptedGeneralUserDataPayload, generalKey,
                        BackendJsonSerializerContext.Default.GeneralUserData, ct: inner) ?? throw new InvalidInputException();
                    general.VerifyIntegrity();
                    if (!Hashing.Verify(general.IntegrityHash, root.GeneralUserDataIntegrityHash) ||
                        !string.Equals(general.Username, request.Username, StringComparison.Ordinal)) throw new InvalidInputException();

                    var local = (await authorizations.ListForUserAsync(userId, inner)).SingleOrDefault(row =>
                        row.DeviceId == identity.LocalDeviceId && row.OriginInstanceId == identity.OriginInstanceId);
                    if (local is null || local.IsActive || local.RemovalOperationId is not Guid removalId || local.RemovalOperationHash is null)
                        throw new InvalidOperationException("Disconnect this installation from an authorized device before removing its local profile.");
                    var stored = await operations.GetByIdAsync(removalId, inner);
                    if (stored is null || stored.Status != UserControlOperationStatus.Applied) throw new InvalidOperationException("The signed disconnection has not been applied locally.");
                    var removal = UserControlOperationEnvelopeUtil.Deserialize(stored.EnvelopePayload);
                    if (removal.UserId != userId || removal.OperationType != UserControlOperationType.DeviceRemoval ||
                        !Hashing.Verify(removal.OperationHash, local.RemovalOperationHash)) throw new InvalidDataException("The local disconnection evidence is inconsistent.");
                    await membership.VerifyControlAuthorAsync(removal, inner);
                    var payload = UserControlOperationEnvelopeUtil.DeserializeDeviceRemovalPayload(removal.OperationPayload);
                    DeviceRemovalEvidenceUtil.ValidateAuthorization(payload, local);
                    // This persistence helper only erases mutable local state. Signed operations,
                    // membership/cutoffs, receipts, fork/fault evidence and deletion barriers survive.
                    await localState.DeleteLocalProfileStateAsync(userId, inner);
                    localState.StageUserDelete(user);
                    await uow.SaveChangesAsync(inner);
                    await transaction.CommitAsync(inner);
                }
                catch (Exception ex) when (ex is CryptographicException or InvalidDataIntegrityException)
                {
                    await transaction.RollbackAsync(CancellationToken.None);
                    uow.ClearTrackedChanges();
                    throw new InvalidInputException();
                }
                catch
                {
                    await transaction.RollbackAsync(CancellationToken.None);
                    uow.ClearTrackedChanges();
                    throw;
                }
                try
                {
                    await sessions.LogoutUserAsync(userId, AuthSessionInvalidationReason.ProfileRemoved, CancellationToken.None);
                    if (user.SavedKey is not null) CryptographicOperations.ZeroMemory(user.SavedKey);
                    user.SavedKey = null;
                    user.ClearEncryptedPayloads();
                    // Refreshing may wait for lifecycle ownership; do it after this action returns.
                }
                catch (Exception ex)
                {
                    throw new MutationPartiallyCommittedException("The local profile was removed, but local access cleanup did not finish.", innerException: ex);
                }
            }, ct);
            try { await runtime.RefreshSyncEnabledAsync(CancellationToken.None); }
            catch (Exception ex) { throw new MutationPartiallyCommittedException("The local profile was removed, but synchronization runtime refresh did not finish.", innerException: ex); }
        }
        finally { CryptographicOperations.ZeroMemory(username); }
    }
}
