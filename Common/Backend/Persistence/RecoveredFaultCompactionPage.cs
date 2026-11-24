using PasswordManagerLocal.Common.Backend.Models;

namespace PasswordManagerLocal.Common.Backend.Persistence;

internal sealed record RecoveredFaultCompactionPage(IReadOnlyList<UserSyncFault> Candidates, string? NextKey);
