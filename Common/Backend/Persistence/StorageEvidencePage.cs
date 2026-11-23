namespace PasswordManagerLocal.Common.Backend.Persistence;

internal sealed record StorageEvidencePage(IReadOnlyList<StorageEvidenceRetentionDecision> Records, StorageEvidenceCursor? Next);
