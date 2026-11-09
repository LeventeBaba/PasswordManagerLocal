namespace PasswordManagerLocal.Common.Contracts.Errors;

public sealed class DatabaseConfigurationInvalidException : DatabaseRecoveryRequiredException
{
    public DatabaseConfigurationInvalidException(string? reason = null, Exception? innerException = null)
        : base(string.IsNullOrWhiteSpace(reason)
            ? "The local database configuration is missing or damaged."
            : $"The local database configuration is missing or damaged. {reason}", innerException)
    {
    }
}
