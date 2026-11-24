namespace PasswordManagerLocal.Common.Contracts.Errors;

/// <summary>A confirmed local reset was interrupted; ordinary startup must preserve surviving files.</summary>
public sealed class DatabaseResetIncompleteException : DatabaseRecoveryRequiredException
{
    public DatabaseResetIncompleteException(Exception? innerException = null)
        : base("The local database reset did not finish. Surviving storage must not be opened or deleted automatically.", innerException)
    {
    }
}
