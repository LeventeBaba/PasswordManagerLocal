using PasswordManagerLocal.Common.Backend.Abstractions.Repositories;
using PasswordManagerLocal.Common.Backend.Abstractions.Services;
using PasswordManagerLocal.Common.Backend.Models;
using PasswordManagerLocal.Common.Backend.Sync;

namespace PasswordManagerLocal.Common.Backend.Services;

public sealed class UserSyncFaultService : IUserSyncFaultService
{
    private readonly IUserSyncFaultRepository _faults;

    public UserSyncFaultService(IUserSyncFaultRepository faults) => _faults = faults;

    public async Task<UserSyncFault> RecordAsync(UserSyncFaultDescriptor descriptor, CancellationToken ct = default)
    {
        var fault = await _faults.GetActiveAsync(
            descriptor.UserId,
            descriptor.Scope,
            descriptor.Kind,
            descriptor.OriginDeviceId,
            descriptor.OriginInstanceId,
            descriptor.KeyEpoch,
            ct);
        // A repeated observation cannot replace the original fork identities or make a
        // terminal fault retryable. Keep the first evidence and avoid replay-only writes.
        if (fault is not null && UserSyncFaultPolicy.IsTerminal(fault))
            return fault;

        var terminal = UserSyncFaultPolicy.IsTerminal(descriptor.Scope, descriptor.Kind, descriptor.Status);
        var now = DateTimeOffset.UtcNow;
        bool isNew;
        if (fault is null)
        {
            isNew = true;
            fault = new UserSyncFault
            {
                UserId = descriptor.UserId,
                Scope = descriptor.Scope,
                FirstDetectedAtUtc = now
            };
        }
        else
        {
            isNew = false;
        }

        fault.Kind = descriptor.Kind;
        fault.Status = terminal ? UserSyncHealthStatus.TerminalConflict : descriptor.Status;
        fault.AffectedComponent = Limit(descriptor.AffectedComponent, 128);
        fault.OriginDeviceId = descriptor.OriginDeviceId;
        fault.OriginInstanceId = descriptor.OriginInstanceId;
        fault.KeyEpoch = descriptor.KeyEpoch;
        fault.MembershipEpoch = descriptor.MembershipEpoch;
        fault.OriginRevision = descriptor.OriginRevision;
        fault.ExpectedHash = CopyHash(descriptor.ExpectedHash);
        fault.ObservedHash = CopyHash(descriptor.ObservedHash);
        fault.ConflictingHash = CopyHash(descriptor.ConflictingHash);
        fault.DiagnosticCode = Limit(descriptor.DiagnosticCode, 128);
        fault.BlocksPublishing = descriptor.BlocksPublishing;
        fault.BlocksMerge = descriptor.BlocksMerge;
        fault.BlocksLogin = descriptor.BlocksLogin;
        fault.BlocksGarbageCollection = descriptor.BlocksGarbageCollection;
        fault.BlocksLifecycle = descriptor.BlocksLifecycle;
        fault.LastDetectedAtUtc = now;
        fault.RecoveredAtUtc = null;
        fault.SupersedingRevision = null;
        if (terminal)
            fault.NextRecoveryAttemptAtUtc = null;
        if (isNew)
            await _faults.AddAsync(fault, ct);
        else
            _faults.Update(fault);
        return fault;
    }

    public async Task MarkOriginRecoveredAsync(
        Guid userId,
        Guid originDeviceId,
        Guid originInstanceId,
        long keyEpoch,
        long supersedingRevision,
        CancellationToken ct = default)
    {
        var rows = await _faults.ListActiveForUserAsync(userId, ct);
        var now = DateTimeOffset.UtcNow;
        foreach (var fault in rows.Where(fault =>
                     fault.OriginDeviceId == originDeviceId &&
                     fault.OriginInstanceId == originInstanceId &&
                     fault.KeyEpoch == keyEpoch &&
                     UserSyncFaultPolicy.IsRecoverableOrigin(fault) &&
                     fault.OriginRevision is long badRevision && supersedingRevision > badRevision))
        {
            fault.Status = UserSyncHealthStatus.Superseded;
            fault.SupersedingRevision = supersedingRevision;
            fault.RecoveredAtUtc = now;
            fault.NextRecoveryAttemptAtUtc = null;
            fault.BlocksPublishing = false;
            fault.BlocksMerge = false;
            fault.BlocksLogin = false;
            fault.BlocksGarbageCollection = false;
            fault.BlocksLifecycle = false;
            _faults.Update(fault);
        }
    }

    public async Task MarkLocalCanonicalRecoveredAsync(Guid userId, CancellationToken ct = default)
    {
        var rows = await _faults.ListActiveForUserAsync(userId, ct);
        var now = DateTimeOffset.UtcNow;
        foreach (var fault in rows.Where(UserSyncFaultPolicy.IsRecoverableCanonical))
        {
            fault.Status = UserSyncHealthStatus.Recovered;
            fault.RecoveredAtUtc = now;
            fault.NextRecoveryAttemptAtUtc = null;
            fault.BlocksPublishing = false;
            fault.BlocksMerge = false;
            fault.BlocksLogin = false;
            fault.BlocksGarbageCollection = false;
            fault.BlocksLifecycle = false;
            _faults.Update(fault);
        }
    }

    public async Task<bool> HasTerminalFaultAsync(Guid userId, CancellationToken ct = default) =>
        (await _faults.ListActiveForUserAsync(userId, ct)).Any(UserSyncFaultPolicy.IsTerminal);

    public Task<bool> IsPublishingBlockedAsync(Guid userId, CancellationToken ct = default) =>
        _faults.HasBlockingPublishingFaultAsync(userId, ct);

    public Task<bool> HasTerminalOriginFaultAsync(
        Guid userId,
        Guid originDeviceId,
        Guid originInstanceId,
        long keyEpoch,
        CancellationToken ct = default) =>
        _faults.HasTerminalOriginFaultAsync(userId, originDeviceId, originInstanceId, keyEpoch, ct);

    private byte[] CopyHash(byte[] value) => value.Length == 0 ? [] : value.ToArray();
    private string Limit(string value, int length) => value.Length <= length ? value : value[..length];
}
