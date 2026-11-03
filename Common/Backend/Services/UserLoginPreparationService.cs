using System.Security.Cryptography;
using System.Text;
using PasswordManagerLocal.Common.Backend.Abstractions.Repositories;
using PasswordManagerLocal.Common.Backend.Abstractions.Services;
using PasswordManagerLocal.Common.Backend.Exceptions;
using PasswordManagerLocal.Common.Backend.Models;
using PasswordManagerLocal.Common.Backend.Models.Encrypted;
using PasswordManagerLocal.Common.Backend.Security;
using PasswordManagerLocal.Common.Backend.Sync;
using PasswordManagerLocal.Common.Backend.Utils;

namespace PasswordManagerLocal.Common.Backend.Services;

/// <summary>Admits verified local login without treating ordinary pending snapshots as prerequisites.</summary>
public sealed class UserLoginPreparationService(
    IUserRepository users,
    IUserLookupService lookup,
    IUserDataReaderService reader,
    IUserCanonicalHealthService health,
    IUserControlStateRepository controlStates,
    IUserControlOperationRepository operations,
    IUserSyncFaultRepository faults,
    IDeletedUserBarrierRepository deletions,
    IUserMembershipAuthorizationRepository memberships,
    IDeviceIdentityService identity,
    IUserControlOperationInboxService controlInbox) : IUserLoginPreparationService
{
    public async Task ReconcilePendingSecurityOperationsAsync(Guid userId, CancellationToken ct = default)
    {
        // StoredPending describes a retained receipt, not necessarily an unapplied transition.
        // Enrollment/catch-up may already have installed the exact signed authorization. Let
        // the control plane prove that, or apply its prerequisites, rather than ignoring the row.
        ct.ThrowIfCancellationRequested();
        var pending = await ListPendingAsync(userId, ct);
        if (pending.Count == 0) return;
        await EnsureControlAuthorizationAsync(userId, ct);
        var maximumPasses = pending.Count;
        for (var pass = 0; pending.Count != 0 && pass < maximumPasses; pass++)
        {
            foreach (var operation in pending.OrderBy(row => row.PreviousKeyEpoch)
                         .ThenBy(row => row.PreviousMembershipEpoch)
                         .ThenBy(row => row.OriginSequence).ThenBy(row => row.OperationId))
            {
                ct.ThrowIfCancellationRequested();
                UserControlOperationReceiptResult receipt;
                try { receipt = await controlInbox.TryApplyStoredAsync(operation.OperationId, ct); }
                catch (Exception ex) when (ex is InvalidDataException or InvalidDataIntegrityException or
                                               CryptographicException or UnauthorizedAccessException)
                {
                    throw new UnauthorizedAccessException("A pending account security operation could not be verified.", ex);
                }
                if (receipt.State == UserControlOperationReceiptState.Quarantined)
                    throw new UnauthorizedAccessException("The account control plane contains a conflicting security operation.");
            }

            await EnsureControlAuthorizationAsync(userId, ct);
            var remaining = await ListPendingAsync(userId, ct);
            // Re-read durable status; a receipt alone must never authorize session admission.
            if (remaining.Count == 0) return;
            var remainingIds = remaining.Select(row => row.OperationId).ToHashSet();
            if (!pending.Any(row => !remainingIds.Contains(row.OperationId)))
                break; // No progress: missing predecessors require further authenticated evidence.
            pending = remaining;
        }
        if (pending.Count != 0)
            throw PendingSecurityOperations(pending);
    }

    private async Task<IReadOnlyList<UserControlOperation>> ListPendingAsync(Guid userId, CancellationToken ct) =>
        (await operations.ListForUserAsync(userId, ct))
            .Where(row => row.Status == UserControlOperationStatus.StoredPending).ToArray();

    private async Task EnsureControlAuthorizationAsync(Guid userId, CancellationToken ct)
    {
        if (await deletions.ExistsAsync(userId, ct) ||
            await memberships.GetActiveAsync(userId, identity.LocalDeviceId, identity.OriginInstanceId, ct) is null)
            throw new UnauthorizedAccessException("This device is no longer authorized for the account.");
        if ((await controlStates.GetAsync(userId, ct))?.HasConflict == true)
            throw new UnauthorizedAccessException("The account control plane contains a conflicting security operation.");
        if ((await faults.ListActiveForUserAsync(userId, ct)).Any(fault =>
                fault.Status == UserSyncHealthStatus.TerminalConflict && (fault.BlocksLogin || fault.BlocksLifecycle)))
            throw new UnauthorizedAccessException("A terminal synchronization conflict prevents signing in.");
    }

    private static UnauthorizedAccessException PendingSecurityOperations(IReadOnlyList<UserControlOperation> pending) =>
        new($"Account security operations must be resolved before signing in. Pending count: {pending.Count}. Operations: " +
            string.Join(", ", pending.Take(8).Select(row => $"{row.OperationId:N} ({row.OperationType})")));

    public async Task EnsureAuthorizedAsync(Guid userId, CancellationToken ct = default, bool requireCanonicalGeneration = true)
    {
        await EnsureControlAuthorizationAsync(userId, ct);
        var state = await controlStates.GetAsync(userId, ct);
        if (requireCanonicalGeneration)
        {
            var current = await users.GetByIdAsNoTrackingAsync(userId, ct)
                ?? throw new UnauthorizedAccessException("The account is no longer available.");
            current.VerifyIntegrity();
            if (state is not null &&
                ((state.AppliedKeyEpoch > 0 && state.AppliedKeyEpoch != current.KeyEpoch) ||
                 (state.AppliedMembershipEpoch > 0 && state.AppliedMembershipEpoch != current.MembershipEpoch)))
                throw new UnauthorizedAccessException("Account security epochs could not be reconciled.");
        }
        var pending = await ListPendingAsync(userId, ct);
        if (pending.Count != 0) throw PendingSecurityOperations(pending);
    }

    public async Task<UserDataBundle?> TryPrepareLocalAsync(
        User user, EncryptionKey key, UserSyncKeyConfidence confidence, CancellationToken ct = default)
    {
        // Check integrity before comparing epochs: a damaged row may still be recoverable
        // from the independently authenticated control state and signed evidence.
        var checkpoint = await health.VerifyAsync(user, null, confidence, recordFault: false, ct: ct);
        if (!checkpoint.IsPublishable)
            return null;
        await EnsureAuthorizedAsync(user.UId, ct);
        var state = await controlStates.GetAsync(user.UId, ct);
        if (state is null || state.AppliedKeyEpoch != user.KeyEpoch ||
            state.AppliedMembershipEpoch != user.MembershipEpoch)
            return null;
        var activeFaults = await faults.ListActiveForUserAsync(user.UId, ct);
        if (activeFaults.Any(f => f.BlocksLogin || f.BlocksLifecycle || f.BlocksMerge ||
                                f.Scope is UserSyncFaultScope.LocalCanonical or UserSyncFaultScope.LoginIdentity))
            return null;

        // Authenticate the committed encrypted generation without decrypting it twice.
        // Missing/invalid checkpoints retain the established synchronous recovery path.
        var projection = await users.GetLoginIdentityStateAsync(user.UId, ct);
        if (projection is null || projection.Status != UserLoginIdentityStatus.Active ||
            projection.KeyEpoch != user.KeyEpoch || projection.MembershipEpoch != user.MembershipEpoch ||
            !SyncVersionStampComparer.Instance.Equals(projection.Version, user.GetGeneralUserDataVersion()) ||
            !projection.UsernameHash.AsSpan().SequenceEqual(user.UsernameHash) ||
            !projection.UsernameSalt.AsSpan().SequenceEqual(user.UsernameSalt))
            return null;

        UserDataBundle? bundle = null;
        try
        {
            bundle = await reader.GetAndVerifyUserDataBundleAsync(user, key, ct);
            UserLoginIdentityMetadataUtil.Verify(user, bundle.GeneralUserData);
            var username = Encoding.UTF8.GetBytes(bundle.GeneralUserData.Username);
            try
            {
                var resolution = await lookup.ResolveUsernameAsync(username, ct);
                if (resolution.State != UserLoginIdentityMatchState.Matched || resolution.UserId != user.UId)
                {
                    bundle.Dispose();
                    return null;
                }
            }
            finally { CryptographicOperations.ZeroMemory(username); }
            return bundle;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or CryptographicException or
                                       InvalidDataIntegrityException or InvalidDataException)
        {
            bundle?.Dispose();
            return null;
        }
        catch
        {
            bundle?.Dispose();
            throw;
        }
    }
}
