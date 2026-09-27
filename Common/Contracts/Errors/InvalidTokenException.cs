namespace PasswordManagerLocal.Common.Contracts.Errors;

public class InvalidTokenException : UnauthorizedAccessException
{

    public InvalidTokenException()
    {
    }

    public InvalidTokenException(string message)
        : base(message)
    {
    }

    public InvalidTokenException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
