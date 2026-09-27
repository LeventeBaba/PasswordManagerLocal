namespace PasswordManagerLocal.Common.Backend.Exceptions;

public sealed class UsernameChangedDuringLoginException : UnauthorizedAccessException
{
    public UsernameChangedDuringLoginException()
        : base("The effective username changed while authentication was in progress. Retry login with the current username.")
    {
    }
}
