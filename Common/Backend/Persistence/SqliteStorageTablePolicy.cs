namespace PasswordManagerLocal.Common.Backend.Persistence;

internal sealed record SqliteStorageTablePolicy(string TableName, string Category, long MaxRowBytes, int? MaxRows = null);
