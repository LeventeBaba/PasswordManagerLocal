namespace PasswordManagerLocal.Common.Backend.Persistence;

internal sealed record StorageEvidenceCursor(string TableName, Guid? UserId, IReadOnlyList<object> Key);
