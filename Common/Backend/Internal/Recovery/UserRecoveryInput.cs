using PasswordManagerLocal.Common.Backend.Models;
using System.Security.Cryptography;

namespace PasswordManagerLocal.Common.Backend.Internal.Recovery;

/// <summary>
/// Owns a detached, bounded read view. Repository reads allocate these rows and buffers afresh;
/// none is exposed to another use case. The commitment is retained separately from working data.
/// </summary>
public sealed class UserRecoveryInput : IDisposable
{
    internal UserRecoveryInput(Guid userId, User? user, UserCanonicalCheckpoint? checkpoint,
        UserControlState? control, bool deleted, IReadOnlyList<UserSyncFault> faults,
        IReadOnlyList<UserSyncSnapshot> candidates, bool isolatedFork, bool pendingControl, UserRecoveryAuthority authority,
        byte[] commitment)
    {
        UserId = userId;
        User = user;
        Checkpoint = checkpoint;
        Control = control;
        Deleted = deleted;
        Faults = faults;
        Candidates = candidates;
        IsolatedFork = isolatedFork;
        PendingControl = pendingControl;
        Authority = authority;
        Commitment = commitment;
    }

    internal Guid UserId { get; }
    internal User? User { get; }
    internal UserCanonicalCheckpoint? Checkpoint { get; }
    internal UserControlState? Control { get; }
    internal bool Deleted { get; }
    internal IReadOnlyList<UserSyncFault> Faults { get; }
    internal IReadOnlyList<UserSyncSnapshot> Candidates { get; }
    internal bool IsolatedFork { get; }
    internal bool PendingControl { get; }
    internal UserRecoveryAuthority Authority { get; }
    internal byte[] Commitment { get; }

    public void Dispose()
    {
        User?.ClearEncryptedPayloads();
        if (User?.SavedKey is { } saved) CryptographicOperations.ZeroMemory(saved);
        foreach (var row in Candidates)
        {
            CryptographicOperations.ZeroMemory(row.EnvelopePayload);
            if (row.ConflictingEnvelopePayload is { } conflicting)
                CryptographicOperations.ZeroMemory(conflicting);
        }
        foreach (var row in Authority.Operations)
            CryptographicOperations.ZeroMemory(row.EnvelopePayload);
    }
}
