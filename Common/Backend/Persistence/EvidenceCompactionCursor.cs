namespace PasswordManagerLocal.Common.Backend.Persistence;

internal sealed record EvidenceCompactionCursor(string Category, string? AfterKey = null);
