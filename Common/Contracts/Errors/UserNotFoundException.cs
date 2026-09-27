namespace PasswordManagerLocal.Common.Contracts.Errors;

public class UserNotFoundException : Exception
{
    public Guid? UserId { get; }

    public UserNotFoundException()
    {
    }

    public UserNotFoundException(string message)
        : base(message)
    {
    }

    public UserNotFoundException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public UserNotFoundException(Guid userId)
        : base($"User with id '{userId}' was not found.")
    {
        UserId = userId;
    }
}
