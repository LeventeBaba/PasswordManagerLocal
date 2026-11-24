namespace PasswordManagerLocal.Common.Backend.Persistence;

internal sealed record EvidenceCompactionBatch(long ReclaimedBytes, EvidenceCompactionCursor? Next);
