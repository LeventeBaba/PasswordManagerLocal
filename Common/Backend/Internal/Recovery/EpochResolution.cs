using PasswordManagerLocal.Common.Backend.Models;

namespace PasswordManagerLocal.Common.Backend.Internal.Recovery;

internal sealed record EpochResolution(
    bool Success,
    long KeyEpoch,
    long MembershipEpoch,
    UserDataRecoveryState State,
    string? DiagnosticCode)
{
    public static EpochResolution Failed(UserDataRecoveryState state, string diagnosticCode) =>
        new(false, 0, 0, state, diagnosticCode);
}
