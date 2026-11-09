using PasswordManagerLocal.Common.Backend.Models;

namespace PasswordManagerLocal.Common.Backend.Abstractions.Persistence;

/// <summary>Stores the local protected journal and reads flushed evidence used to verify its retirement.</summary>
public interface IUserRecoveryJournalStore
{
    bool HasActiveTransaction { get; }
    void Add(UserRecoveryJournal row);
    void Update(UserRecoveryJournal row);
    void Delete(UserRecoveryJournal row);
    Task<UserRecoveryJournal?> GetForWriteAsync(Guid journalId, CancellationToken ct);
    Task<UserRecoveryJournal?> ReadPersistedAsync(Guid journalId, CancellationToken ct);
    Task<Guid?> ReadUserIdAsync(Guid journalId, CancellationToken ct);
    Task<IReadOnlyList<Guid>> ListIdsAsync(CancellationToken ct);
    Task<bool> HasAppliedControlStateAsync(Guid userId, long keyEpoch, long membershipEpoch, CancellationToken ct);
    Task<LocalUserDevice?> ReadLocalLinkAsync(Guid userId, CancellationToken ct);
    Task<byte[]?> ReadActiveSigningKeyAsync(Guid userId, Guid deviceId, Guid originInstanceId, CancellationToken ct);
    Task<IReadOnlyList<Guid>> ListActiveDeviceIdsAsync(Guid userId, CancellationToken ct);
    Task<bool> HasBlockingFaultsAsync(Guid userId, CancellationToken ct);
    Task<UserRevisionKnowledge?> ReadKnowledgeAsync(Guid userId, Guid deviceId, Guid originInstanceId, long keyEpoch, CancellationToken ct);
    Task<byte[]?> ReadRejectedOperationHashAsync(Guid userId, Guid operationId, CancellationToken ct);
}
