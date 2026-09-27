namespace PasswordManagerLocal.Common.Backend.Sync;

public sealed class AccountDeletionPayload
{
    public Guid UserId { get; set; }
    public Guid DeletionGeneration { get; set; }
    public long KeyEpoch { get; set; }
    public long MembershipEpoch { get; set; }
    public List<AccountDeletionKnownMember> KnownMembers { get; set; } = [];
    public byte[] MembershipHistoryHash { get; set; } = [];
    public byte[] IntegrityHash { get; set; } = [];
}
