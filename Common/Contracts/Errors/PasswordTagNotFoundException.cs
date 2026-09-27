namespace PasswordManagerLocal.Common.Contracts.Errors;

public sealed class PasswordTagNotFoundException : Exception
{
    public Guid PasswordTagId { get; }

    public PasswordTagNotFoundException(Guid passwordTagId)
        : base($"Password tag was not found: {passwordTagId}")
    {
        PasswordTagId = passwordTagId;
    }
}
