namespace PasswordManagerLocal.Common.Contracts.Errors;

public sealed class DuplicatePasswordNameException : Exception
{
    public string PasswordName { get; }

    public DuplicatePasswordNameException(string passwordName)
        : base($"A saved password with the name '{passwordName}' already exists.")
    {
        PasswordName = passwordName;
    }
}
