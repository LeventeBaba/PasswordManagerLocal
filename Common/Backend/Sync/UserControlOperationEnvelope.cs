using PasswordManagerLocal.Common.Backend.Models;

namespace PasswordManagerLocal.Common.Backend.Sync;

public sealed class UserControlOperationEnvelope
{
    public Guid OperationId { get; set; }
    public Guid UserId { get; set; }
    public UserControlOperationType OperationType { get; set; }
    public Guid OriginDeviceId { get; set; }
    public Guid OriginInstanceId { get; set; }
    public long OriginSequence { get; set; }
    public long PreviousKeyEpoch { get; set; }
    public long ResultingKeyEpoch { get; set; }
    public long PreviousMembershipEpoch { get; set; }
    public long ResultingMembershipEpoch { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    // Auxiliary independently signed proof envelopes. Their exact hashes are bound by RevocationMembers.
    // Nested auxiliary lists are forbidden; relays may add valid proofs without changing the operation identity.
    public List<UserControlOperationEnvelope> RevocationHistoryProofs { get; set; } = [];
    public List<AccountDeletionKnownMember> RevocationMembers { get; set; } = [];
    public byte[] OperationPayload { get; set; } = [];
    public byte[] PayloadHash { get; set; } = [];
    public byte[] OperationHash { get; set; } = [];
    public byte[] OriginSignPublicKey { get; set; } = [];
    public byte[] OriginSignature { get; set; } = [];
}
