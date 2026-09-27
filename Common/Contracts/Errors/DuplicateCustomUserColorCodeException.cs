namespace PasswordManagerLocal.Common.Contracts.Errors;

public sealed class DuplicateCustomUserColorCodeException : Exception
{
    public string ColorCode { get; }

    public DuplicateCustomUserColorCodeException(string colorCode)
        : base($"A custom user color with the code '{colorCode}' already exists.")
    {
        ColorCode = colorCode;
    }
}
