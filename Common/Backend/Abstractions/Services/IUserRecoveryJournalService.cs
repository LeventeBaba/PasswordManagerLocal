namespace PasswordManagerLocal.Common.Backend.Abstractions.Services;

public interface IUserRecoveryJournalService
{
    // The caller owns the reset transaction. Staging and reset commit or roll back together.
    Task<Guid> StageAsync(Guid userId, byte[] snapshot, CancellationToken ct = default);
    // Returns true while a journal/evidence record is still required, false after removal.
    Task<bool> CompleteAsync(Guid journalId, CancellationToken ct = default);
    Task MaintainAsync(CancellationToken ct = default);
}
