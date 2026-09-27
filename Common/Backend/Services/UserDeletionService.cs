using PasswordManagerLocal.Common.Backend.Exceptions;
using PasswordManagerLocal.Common.Backend.Abstractions.Persistence;
using PasswordManagerLocal.Common.Backend.Abstractions.Repositories;
using PasswordManagerLocal.Common.Backend.Abstractions.Services;
using PasswordManagerLocal.Common.Backend.Models;
using PasswordManagerLocal.Common.Backend.Sync;

namespace PasswordManagerLocal.Common.Backend.Services;

/// <summary>
/// Creates the one authoritative signed account-deletion operation and atomically installs the
/// permanent deletion barrier before removing mutable account state. The legacy generic user-delta
/// deletion flag is intentionally ignored; protocol v6 has exactly one remote deletion mechanism.
/// </summary>
public sealed class UserDeletionService : IUserDeletionService
{
    private readonly IUserLookupService _lookup;
    private readonly IUserRepository _users;
    private readonly IUserLifecycleCoordinator _lifecycle;
    private readonly IUserControlOperationWriterService _controlWriter;
    private readonly IUserControlStateRepository _controlStates;
    private readonly IDeletedUserBarrierRepository _barriers;
    private readonly IUserAccountDeletionCleanupService _cleanup;
    private readonly IAuthSessionService _auth;
    private readonly ISyncRuntimeService _syncRuntime;
    private readonly IUnitOfWork _uow;
    private readonly IDeviceEnrollmentService? _enrollment;

    public UserDeletionService(
        IUserLookupService lookup,
        IUserRepository users,
        IUserLifecycleCoordinator lifecycle,
        IUserControlOperationWriterService controlWriter,
        IUserControlStateRepository controlStates,
        IDeletedUserBarrierRepository barriers,
        IUserAccountDeletionCleanupService cleanup,
        IAuthSessionService auth,
        ISyncRuntimeService syncRuntime,
        IUnitOfWork uow,
        IDeviceEnrollmentService? enrollment = null)
    {
        _lookup = lookup;
        _users = users;
        _lifecycle = lifecycle;
        _controlWriter = controlWriter;
        _controlStates = controlStates;
        _barriers = barriers;
        _cleanup = cleanup;
        _auth = auth;
        _syncRuntime = syncRuntime;
        _uow = uow;
        _enrollment = enrollment;
    }

    public Task DeleteUserAsync(User user, CancellationToken ct = default) =>
        DeleteUserAsync(user, true, ct);

    public Task DeleteUserAsync(User user, bool enqueueSync, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(user);
        return DeleteAuthoritativelyAsync(user.UId, ct);
    }

    public Task DeleteUserAsync(Guid uid, CancellationToken ct = default) =>
        DeleteUserAsync(uid, true, ct);

    public Task DeleteUserAsync(Guid uid, bool enqueueSync, CancellationToken ct = default) =>
        DeleteAuthoritativelyAsync(uid, ct);

    public async Task DeleteUserByTokenAsync(Guid token, CancellationToken ct = default) =>
        await DeleteUserByTokenAsync(token, true, ct);

    public async Task DeleteUserByTokenAsync(Guid token, bool enqueueSync, CancellationToken ct = default)
    {
        var user = await _lookup.GetAndVerifyUserAsync(token, ct);
        await DeleteAuthoritativelyAsync(user.UId, ct);
    }

    private async Task DeleteAuthoritativelyAsync(Guid userId, CancellationToken ct)
    {
        if (userId == Guid.Empty)
            throw new ArgumentException("The account identity is invalid.", nameof(userId));

        await _lifecycle.ExecuteAsync(
            userId,
            async token =>
            {
                await using var transaction = await _uow.BeginTransactionAsync(token);
                try
                {
                    var existingBarrier = await _barriers.GetAsync(userId, token);
                    if (existingBarrier is not null)
                    {
                        // A previous committed deletion may have crashed before finishing an older
                        // cleanup implementation. Re-running cleanup is deliberately idempotent.
                        await _cleanup.DeleteCanonicalAndPendingStateAsync(userId, token);
                        await _uow.SaveChangesAsync(token);
                        await transaction.CommitAsync(token);
                    }
                    else
                    {
                        var user = await _users.GetByIdAsync(userId, token)
                            ?? throw new InvalidOperationException("The account no longer exists locally.");
                        var controlState = await _controlStates.GetAsync(userId, token);
                        if (controlState?.HasConflict == true)
                        {
                            throw new InvalidOperationException(
                                "Account deletion is fail-closed while authoritative lifecycle state is quarantined.");
                        }

                        // Ordinary pending snapshots do not need to be merged before deletion. Their
                        // contents cannot affect deletion authority and are removed in this transaction.
                        var envelope = await _controlWriter.CreateAppliedAccountDeletionUnderLifecycleAsync(user, token);
                        var payload = UserControlOperationEnvelopeUtil.DeserializeAccountDeletionPayload(envelope.OperationPayload);
                        var barrier = DeletedUserBarrierUtil.Create(envelope, payload, DateTimeOffset.UtcNow);
                        await _barriers.AddAsync(barrier, token);

                        await _cleanup.DeleteCanonicalAndPendingStateAsync(userId, token);
                        await _uow.SaveChangesAsync(token);
                        await transaction.CommitAsync(token);
                    }
                }
                catch
                {
                    await transaction.RollbackAsync(token);
                    _uow.ClearTrackedChanges();
                    throw;
                }
            },
            ct);

        // These process-local effects intentionally happen only after the durable transaction. A
        // runtime wake-up failure cannot roll back deletion evidence; anti-entropy will retry it.
        Exception? postCommitFailure = null;
        try
        {
            _auth.LogoutUser(userId, AuthSessionInvalidationReason.ProfileRemoved);
        }
        catch (Exception ex)
        {
            postCommitFailure = ex;
        }

        try
        {
            if (_enrollment is not null)
                await _enrollment.CancelEnrollmentAsync(CancellationToken.None);
            await _syncRuntime.RefreshSyncEnabledAsync(CancellationToken.None);
        }
        catch
        {
            // Durable control-operation inventory remains retryable after restart/next refresh.
        }

        if (postCommitFailure is not null)
        {
            throw new MutationPartiallyCommittedException(
                "The account deletion was committed, but the local authenticated session could not be cleared.",
                innerException: postCommitFailure);
        }
    }
}
