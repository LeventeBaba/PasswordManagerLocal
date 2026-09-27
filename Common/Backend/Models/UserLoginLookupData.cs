namespace PasswordManagerLocal.Common.Backend.Models;

public sealed class UserLoginLookupData
{
    public Guid UId { get; init; }
    public byte[] UsernameSalt { get; init; } = [];
    public byte[] UsernameHash { get; init; } = [];
    public UserLoginIdentityStatus Status { get; init; }
    public long KeyEpoch { get; init; }
    public long MembershipEpoch { get; init; }
}
