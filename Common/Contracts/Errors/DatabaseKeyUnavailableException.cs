namespace PasswordManagerLocal.Common.Contracts.Errors;

public sealed class DatabaseKeyUnavailableException : DatabaseRecoveryRequiredException
{
    public DatabaseKeyUnavailableException(Exception? innerException = null)
        : base("The protected local database key could not be recovered with the current installation or operating-system account.",
            innerException)
    {
    }
}
