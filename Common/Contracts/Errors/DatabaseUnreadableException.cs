namespace PasswordManagerLocal.Common.Contracts.Errors;

public sealed class DatabaseUnreadableException : DatabaseRecoveryRequiredException
{
    public DatabaseUnreadableException(Exception? innerException = null)
        : base("The local database cannot be read with its configured encryption key. The key may not match, the data may be damaged, or the encryption format may not be recognized.",
            innerException)
    {
    }
}
