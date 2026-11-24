using PasswordManagerLocal.Common.Backend.Models;

namespace PasswordManagerLocal.Common.Backend.Internal.Recovery;

/// <summary>Exclusively owned historical authority captured with the recovery read view.</summary>
public sealed class UserRecoveryAuthority
{
    internal UserRecoveryAuthority(IReadOnlyList<UserMembershipAuthorization> authorizations,
        IReadOnlyList<UserOriginRemovalCutoff> cutoffs, IReadOnlyList<UserControlOperation> operations)
    {
        Authorizations = authorizations;
        Cutoffs = cutoffs;
        Operations = operations;
    }

    internal IReadOnlyList<UserMembershipAuthorization> Authorizations { get; }
    internal IReadOnlyList<UserOriginRemovalCutoff> Cutoffs { get; }
    internal IReadOnlyList<UserControlOperation> Operations { get; }
}
