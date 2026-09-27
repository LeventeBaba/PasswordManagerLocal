namespace PasswordManagerLocal.Common.Contracts.Errors;

public sealed class DuplicateCustomUserColorNameException : Exception
{
    public string ColorName { get; }

    public DuplicateCustomUserColorNameException(string colorName)
        : base($"A custom user color with the name '{colorName}' already exists.")
    {
        ColorName = colorName;
    }
}
