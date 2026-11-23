using PasswordManagerLocal.Common.Backend.Models;
using PasswordManagerLocal.Common.Backend.Sync;

namespace PasswordManagerLocal.Common.Backend.Abstractions.Persistence;

/// <summary>Durable encrypted-vault ingress and a conservative access fence; not an application receipt.</summary>
public interface ICriticalControlIngressStore
{
    void Initialize();
    bool IsSuspended(Guid userId);
    bool HasPendingOrUncertain { get; }
    void ThrowIfSuspended(Guid userId);
    IReadOnlyList<Guid> SuspendedUserIds { get; }
    Task<byte[]> RetainAsync(UserControlOperationEnvelope envelope, Guid transportPeerDeviceId, CancellationToken ct = default);
    Task DiscardAsync(byte[] envelopeHash, CancellationToken ct = default);
    Task CompleteAsync(Guid userId, Guid operationId, byte[] operationHash, CancellationToken ct = default);
    Task<IReadOnlyList<byte[]>> ListKeysAsync(CancellationToken ct = default);
    Task<CriticalControlIngressEntry?> ReadAsync(byte[] envelopeHash, CancellationToken ct = default);
    Task<bool> CanApplyPredecessorAsync(UserControlOperationEnvelope envelope, CancellationToken ct = default);
}
