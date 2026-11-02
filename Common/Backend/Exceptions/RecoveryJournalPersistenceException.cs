namespace PasswordManagerLocal.Common.Backend.Exceptions;

public sealed class RecoveryJournalPersistenceException(Exception innerException)
    : Exception("Recovery data could not be stored and verified inside the protected database.", innerException);
