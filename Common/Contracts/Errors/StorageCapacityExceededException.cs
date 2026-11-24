namespace PasswordManagerLocal.Common.Contracts.Errors;

/// <summary>A finite storage budget or the database's physical capacity was exhausted.</summary>
public sealed class StorageCapacityExceededException(Exception? innerException = null)
    : Exception("More data cannot currently be saved because local storage capacity has been reached. Existing data has been preserved.", innerException);
