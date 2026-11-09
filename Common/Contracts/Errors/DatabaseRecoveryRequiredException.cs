namespace PasswordManagerLocal.Common.Contracts.Errors;

/// <summary>A local database startup failure for which an explicitly confirmed reset is available.</summary>
public abstract class DatabaseRecoveryRequiredException : InvalidOperationException
{
    protected DatabaseRecoveryRequiredException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}
