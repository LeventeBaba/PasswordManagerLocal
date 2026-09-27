namespace PasswordManagerLocal.Common.Contracts.Errors;

public sealed class PasswordNotFoundException : Exception
{
    public Guid PasswordId { get; private set; }

    public PasswordNotFoundException(Guid passwordId) : base($"Password {passwordId} was not found.")
    {
        PasswordId = passwordId;
    }
}
